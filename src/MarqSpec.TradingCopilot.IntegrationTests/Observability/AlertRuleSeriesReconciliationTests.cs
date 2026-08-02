using System.Diagnostics.Metrics;
using System.Text.Json;
using System.Text.RegularExpressions;
using MarqSpec.TradingCopilot.Api.Ai;
using MarqSpec.TradingCopilot.Api.Observability;
using MarqSpec.TradingCopilot.Domain.Observability;
using Microsoft.Extensions.Options;

namespace MarqSpec.TradingCopilot.IntegrationTests.Observability;

/// <summary>
/// Reconciles every series and label the alert rules (<c>observability/rules/trading-alerts.yml</c>) and the
/// Grafana dashboards reference against what the application <b>actually emits</b> (gh#536, R-20, ADR-0008). A typo
/// in an <c>outcome=</c> or <c>tier=</c> matcher, or a wrong Prometheus suffix, yields an empty series and a rule
/// that <b>silently never fires</b> — the highest-probability observability defect, invisible precisely because it
/// looks healthy.
/// </summary>
/// <remarks>
/// <para>
/// <b>The source of truth is a live <see cref="MeterListener"/> over real meter instances, never reflection over
/// name constants</b> — a constant compared to itself proves nothing (the QA guard discipline). The instruments
/// are published on meter construction; the Prometheus series names are derived from the OTLP →
/// <c>prometheusremotewrite</c> translation (<c>add_metric_suffixes: true</c> on
/// <c>otel/opentelemetry-collector-contrib:0.115.1</c>): dots to underscores, brace-annotation units <c>{…}</c>
/// dropped, real units mapped (<c>ms → milliseconds</c>), counters gain <c>_total</c>, histograms gain
/// <c>_bucket</c>/<c>_sum</c>/<c>_count</c>, gauges bare.
/// </para>
/// <para>
/// This tier is venue- and database-independent: it starts no <c>PostgresApiFactory</c>, so it needs no container.
/// It reads the observability assets copied next to the test binary (the csproj <c>Content Include</c>).
/// </para>
/// </remarks>
public sealed class AlertRuleSeriesReconciliationTests
{
    // ---- what the application publishes: harvested from real meters via a MeterListener ----

    private sealed record Published(
        IReadOnlySet<string> Series,             // valid Prometheus series names (with the correct suffix)
        IReadOnlySet<string> Bases,              // the instrument base names (before _total / _bucket / …)
        IReadOnlyDictionary<string, IReadOnlySet<string>> TagValues); // tag key -> the values the app emits

    private static Published HarvestPublished()
    {
        // Unique meter names so this listener observes ONLY these instances, never a parallel test class's (a data
        // race on the listener buffer, not hypothetical — see ExecutionMetrics' own note).
        string executionMeter = "test.reconcile.exec." + Guid.NewGuid().ToString("N");
        string aiMeter = "test.reconcile.ai." + Guid.NewGuid().ToString("N");

        using ExecutionMetrics execution = new(executionMeter);
        using LlmMetrics llm = new(aiMeter);
        using GovernorMetrics governor = new(Options.Create(new GovernorOptions()), aiMeter);
        using EmbeddingMetrics embedding = new(aiMeter);

        List<Instrument> instruments = [];
        Dictionary<string, HashSet<string>> tagValues = [];

        void CaptureTags(ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            foreach (KeyValuePair<string, object?> tag in tags)
            {
                (tagValues.TryGetValue(tag.Key, out HashSet<string>? set)
                    ? set
                    : tagValues[tag.Key] = []).Add(tag.Value?.ToString() ?? "");
            }
        }

        using MeterListener listener = new();
        listener.InstrumentPublished = (instrument, active) =>
        {
            if (instrument.Meter.Name == executionMeter || instrument.Meter.Name == aiMeter)
            {
                instruments.Add(instrument);
                active.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) => CaptureTags(tags));
        listener.SetMeasurementEventCallback<double>((_, _, tags, _) => CaptureTags(tags));
        listener.Start(); // enumerates instruments already created in the meter constructors above

        // Drive only the labelled series the rules/dashboards actually filter on — flatten deadlines by tier and
        // outcome — so TagValues carries exactly the emitted matcher values (no more, no less).
        foreach (FlattenTier tier in Enum.GetValues<FlattenTier>())
        {
            foreach (string outcome in _flattenOutcomes)
            {
                execution.RecordFlattenDeadline(tier, outcome);
            }
        }

        var series = new HashSet<string>(StringComparer.Ordinal);
        var bases = new HashSet<string>(StringComparer.Ordinal);
        foreach (Instrument instrument in instruments)
        {
            string promBase = PrometheusBase(instrument.Name, instrument.Unit);
            bases.Add(promBase);
            foreach (string full in PrometheusSeries(promBase, instrument))
            {
                series.Add(full);
            }
        }

        return new Published(
            series,
            bases,
            tagValues.ToDictionary(pair => pair.Key, pair => (IReadOnlySet<string>)pair.Value, StringComparer.Ordinal));
    }

    private static readonly string[] _flattenOutcomes =
    [
        ExecutionMetrics.FlattenExecuted, ExecutionMetrics.FlattenNothingToDo, ExecutionMetrics.FlattenEscalated,
        ExecutionMetrics.FlattenMissed, ExecutionMetrics.FlattenDisabled, ExecutionMetrics.FlattenWarning,
        ExecutionMetrics.FlattenUnconfigured, ExecutionMetrics.FlattenRejected,
    ];

    // The OTLP -> Prometheus name (prometheusremotewrite, add_metric_suffixes default true): dots to underscores,
    // brace-annotation units dropped, real units mapped and appended.
    private static string PrometheusBase(string instrumentName, string? unit)
    {
        string name = instrumentName.Replace('.', '_');
        if (!string.IsNullOrEmpty(unit) && !unit.StartsWith('{'))
        {
            name += "_" + MapUnit(unit);
        }

        return name;
    }

    private static string MapUnit(string unit) => unit switch
    {
        "ms" => "milliseconds",
        "s" => "seconds",
        "By" => "bytes",
        _ => unit,
    };

    private static IEnumerable<string> PrometheusSeries(string promBase, Instrument instrument)
    {
        string kind = instrument.GetType().Name;
        if (kind.StartsWith("Histogram", StringComparison.Ordinal))
        {
            return [promBase + "_bucket", promBase + "_sum", promBase + "_count"];
        }

        // Counter / UpDownCounter are monotonic-or-not sums; both gain _total. Observable gauges stay bare.
        if (kind.Contains("Counter", StringComparison.Ordinal))
        {
            return [promBase + "_total"];
        }

        return [promBase];
    }

    // ---- what the rules and dashboards reference: harvested from expr text only ----

    private sealed record Referenced(IReadOnlySet<string> Series, IReadOnlyList<(string Key, string Value)> Matchers);

    private static Referenced HarvestReferenced()
    {
        var series = new HashSet<string>(StringComparer.Ordinal);
        var matchers = new List<(string, string)>();

        void Collect(string expr)
        {
            // Every app series is prefixed trading_ / ai_; harvest those tokens ([A-Za-z0-9_], not lowercase-only,
            // so a stray uppercase suffix would be caught rather than dropped).
            foreach (Match match in Regex.Matches(expr, "(?:trading|ai)_[A-Za-z0-9_]+"))
            {
                series.Add(match.Value);
            }

            foreach (Match match in Regex.Matches(expr, "([A-Za-z_][A-Za-z0-9_]*)\\s*=~?\\s*\"([^\"]*)\""))
            {
                matchers.Add((match.Groups[1].Value, match.Groups[2].Value));
            }
        }

        foreach (string expr in RuleExprs(ReadAsset("rules", "trading-alerts.yml")))
        {
            Collect(expr);
        }

        foreach (string dashboard in DashboardFiles())
        {
            foreach (string expr in DashboardExprs(ReadAsset("grafana", "dashboards", dashboard)))
            {
                Collect(expr);
            }
        }

        return new Referenced(series, matchers);
    }

    // Harvest ONLY `expr:` values from the rules YAML — never description / summary / runbook / comments, or the
    // suite would reconcile prose. Handles inline exprs and `>-` folded blocks (join continuation lines).
    private static IEnumerable<string> RuleExprs(string yaml)
    {
        string[] lines = yaml.Replace("\r\n", "\n").Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            Match header = Regex.Match(lines[i], "^(\\s*)expr:\\s*(.*)$");
            if (!header.Success)
            {
                continue;
            }

            int indent = header.Groups[1].Value.Length;
            string inline = header.Groups[2].Value.Trim();
            if (inline.Length > 0 && inline is not (">-" or ">" or "|" or "|-" or ">+" or "|+"))
            {
                yield return inline;
                continue;
            }

            // Folded / block scalar: gather the more-indented continuation lines.
            var block = new List<string>();
            for (int j = i + 1; j < lines.Length; j++)
            {
                if (lines[j].Trim().Length == 0)
                {
                    continue;
                }

                int lineIndent = lines[j].Length - lines[j].TrimStart().Length;
                if (lineIndent <= indent)
                {
                    break;
                }

                block.Add(lines[j].Trim());
            }

            yield return string.Join(' ', block);
        }
    }

    // Harvest every `expr` string anywhere in the dashboard JSON (targets[].expr, at any nesting), never panel
    // descriptions or markdown.
    private static IEnumerable<string> DashboardExprs(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        var found = new List<string>();
        Walk(document.RootElement, found);
        return found;

        static void Walk(JsonElement element, List<string> found)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (JsonProperty property in element.EnumerateObject())
                    {
                        if (property.NameEquals("expr") && property.Value.ValueKind == JsonValueKind.String)
                        {
                            found.Add(property.Value.GetString() ?? "");
                        }

                        Walk(property.Value, found);
                    }

                    break;
                case JsonValueKind.Array:
                    foreach (JsonElement item in element.EnumerateArray())
                    {
                        Walk(item, found);
                    }

                    break;
                default:
                    break;
            }
        }
    }

    private static readonly string[] _observableTagKeys = ["outcome", "tier"];

    private static string[] DashboardFiles() =>
    [
        "ai-usage-and-spend.json", "auto-flatten-reliability.json",
        "execution-and-risk-gate.json", "synthetic-risk-and-pipeline.json",
    ];

    private static string ReadAsset(params string[] relative) =>
        File.ReadAllText(Path.Combine([AppContext.BaseDirectory, "observability", .. relative]));

    // ---- Case 0: the anti-vacuity anchor. Without it, every case below passes by matching nothing. ----

    [Fact]
    public void TheReconciliation_ShouldActuallyResolveRulesAndInstruments()
    {
        Published published = HarvestPublished();
        Referenced referenced = HarvestReferenced();

        referenced.Series.Should().NotBeEmpty("the rules and dashboards reference series");
        referenced.Matchers.Should().NotBeEmpty("the rules and dashboards filter on labels");
        published.Series.Should().NotBeEmpty("the meters publish instruments");

        // Explicit membership of a couple of known series, so a harvest that silently returns the wrong thing fails
        // here rather than passing everything else vacuously.
        referenced.Series.Should().Contain("trading_flatten_deadlines_total");
        published.Series.Should().Contain("trading_flatten_deadlines_total");
        published.Series.Should().Contain("trading_flatten_time_to_flat_milliseconds_bucket");
        published.Series.Should().Contain("trading_killswitch_engaged");
        referenced.Matchers.Should().Contain(("outcome", "executed"));
    }

    // ---- Case 1: every referenced series resolves to an instrument the meter publishes ----

    [Fact]
    public void EveryRuleAndDashboardSeries_ShouldResolveToAnInstrumentTheMeterPublishes()
    {
        Published published = HarvestPublished();
        Referenced referenced = HarvestReferenced();

        foreach (string reference in referenced.Series)
        {
            bool resolves = published.Bases.Any(
                b => reference == b || reference.StartsWith(b + "_", StringComparison.Ordinal));
            resolves.Should().BeTrue(
                $"series '{reference}' referenced by a rule/dashboard must resolve to an instrument the app publishes "
                + "(a typo yields an empty series and a rule that silently never fires — gh#536)");
        }
    }

    // ---- Case 2: every referenced series' suffix matches its instrument's kind and unit ----

    [Fact]
    public void EverySeriesSuffix_ShouldMatchItsInstrumentKindAndUnit()
    {
        Published published = HarvestPublished();
        Referenced referenced = HarvestReferenced();

        foreach (string reference in referenced.Series)
        {
            published.Series.Should().Contain(reference,
                $"'{reference}' must carry the exact suffix the OTLP->Prometheus translation produces for its "
                + "instrument's kind and unit (a bare _bucket without _milliseconds, or a missing _total, is an empty series)");
        }
    }

    // ---- Case 3: every outcome and tier label matcher is a value the app emits ----

    [Fact]
    public void EveryOutcomeAndTierLabelMatcher_ShouldBeAValueTheAppEmits()
    {
        Published published = HarvestPublished();
        Referenced referenced = HarvestReferenced();

        foreach ((string key, string value) in referenced.Matchers.Where(m => _observableTagKeys.Contains(m.Key)))
        {
            published.TagValues.Should().ContainKey(key, $"the app emits a '{key}' label");

            // A `=~"a|b"` regex matcher is a set of alternatives; each literal must be emitted.
            foreach (string alternative in value.Split('|', StringSplitOptions.RemoveEmptyEntries))
            {
                published.TagValues[key].Should().Contain(alternative,
                    $"the matcher {key}=\"{alternative}\" must select a value the app actually emits — an unemitted "
                    + "value matches nothing and the rule silently never fires (gh#536)");
            }
        }
    }

    // ---- Case 4: the positive control — a renamed instrument or a bare bucket suffix turns the check red ----

    [Fact]
    public void ARenamedInstrumentOrABareBucketSuffix_ShouldTurnTheReconciliationRed()
    {
        Published published = HarvestPublished();

        // A histogram read without its `_milliseconds` unit segment — the exact shape a hand-written rule gets wrong.
        published.Series.Should().NotContain("trading_flatten_time_to_flat_bucket",
            "a bare _bucket suffix (missing the _milliseconds unit) is NOT what the collector emits; case 2 must reject it");

        // A renamed instrument no rule should resolve to.
        published.Series.Should().NotContain("trading_flatten_deadlines_renamed_total");
        published.Bases.Should().NotContain("trading_flatten_deadlines_renamed",
            "case 1 must reject a series that resolves to no published instrument");
    }
}
