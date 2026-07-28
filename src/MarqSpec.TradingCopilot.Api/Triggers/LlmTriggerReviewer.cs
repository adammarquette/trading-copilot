using System.Text.Json;
using MarqSpec.TradingCopilot.Domain.Ai;
using MarqSpec.TradingCopilot.Domain.Triggers;
using MarqSpec.TradingCopilot.Domain.Venue;
using Microsoft.Extensions.Logging;

namespace MarqSpec.TradingCopilot.Api.Triggers;

/// <summary>
/// The LLM-backed <see cref="ITriggerReviewer"/> (R-4 / ADR-0008): it assembles the fired setup's market facts into
/// one triage-tier completion, parses the model's structured answer, and maps it to a <see cref="ReviewOutcome"/>.
/// </summary>
/// <remarks>
/// <b>Fail-closed is the whole design.</b> This is the first of three layers guarding against a malformed or hostile
/// model output (the pure <see cref="SuggestionGeometry"/> sanity check and the take-time risk gate are below it).
/// Anything other than a well-formed <i>suggest</i> — a non-<see cref="LlmStopReason.Completed"/> stop (a refusal or a
/// truncation), unparseable JSON, an unknown decision, an unknown direction, or a missing price — becomes a
/// <see cref="ReviewOutcome.Suppress"/>, <b>never</b> a suggestion. Two things it deliberately does not do: it does
/// not read size from the model (size is the operator's trigger's), and it runs <b>no numeric geometry check</b> —
/// that is <see cref="SuggestionGeometry"/>'s job at issuance, below the model.
/// </remarks>
public sealed class LlmTriggerReviewer : ITriggerReviewer
{
    private const int MaxOutputTokens = 1024;

    // The structured answer the model is asked for. The reviewer -- not the schema -- is the enforcer, because the
    // provider is not guaranteed to constrain the model to it; the mapping below fails closed on anything unusable.
    private const string ReviewSchema = """
        {
          "type": "object",
          "properties": {
            "decision": { "type": "string", "enum": ["suggest", "suppress"] },
            "direction": { "type": "string", "enum": ["long", "short"] },
            "entry": { "type": "number" },
            "stop": { "type": "number" },
            "target": { "type": "number" },
            "rationale": { "type": "string" },
            "reason": { "type": "string" }
          },
          "required": ["decision"]
        }
        """;

    private const string SystemPrompt =
        "You review a single fired futures trigger and decide whether it is worth surfacing to the operator as a "
        + "trade setup. Respond ONLY as JSON matching the given schema. Either propose a setup (decision \"suggest\" "
        + "with direction long|short and numeric entry, stop and target prices) or decline (decision \"suppress\" with "
        + "a short reason). You never choose size and you never place an order — you only propose; a deterministic "
        + "risk gate sizes and validates everything below you.";

    // Property-name-insensitive so "Decision"/"decision" map the same. Immutable + reused per call.
    private static readonly JsonSerializerOptions _serializerOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly ILlmProvider _llm;
    private readonly ILogger<LlmTriggerReviewer> _logger;

    /// <summary>Creates the reviewer.</summary>
    /// <param name="llm">The provider-neutral LLM seam the review call goes through.</param>
    /// <param name="logger">The logger; a fail-closed suppression is logged, never swallowed.</param>
    public LlmTriggerReviewer(ILlmProvider llm, ILogger<LlmTriggerReviewer> logger)
    {
        _llm = llm;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<ReviewOutcome> ReviewAsync(TriggerReviewContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        LlmRequest request = new(
            LlmModelTier.Triage,
            SystemPrompt,
            [new LlmMessage(LlmRole.User, Render(context))],
            LlmResponseFormat.Json(ReviewSchema),
            MaxOutputTokens);

        LlmCompletion completion = await _llm.CompleteAsync(request, cancellationToken);

        // FAIL-CLOSED on anything but a clean completion: a refusal or a truncation is not a proposal.
        if (completion.StopReason != LlmStopReason.Completed)
        {
            _logger.LogWarning(
                "Trigger {TriggerId} review did not complete cleanly (stop reason {StopReason}); suppressing.",
                context.TriggerId,
                completion.StopReason);
            return new ReviewOutcome.Suppress(
                SuppressReason.MalformedOutput, $"the model stopped with {completion.StopReason}");
        }

        ReviewDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<ReviewDto>(completion.Text, _serializerOptions);
        }
        catch (JsonException error)
        {
            _logger.LogWarning(error, "Trigger {TriggerId} review output was not valid JSON; suppressing.", context.TriggerId);
            return new ReviewOutcome.Suppress(SuppressReason.MalformedOutput, "the model output was not valid JSON");
        }

        if (dto is null)
        {
            return new ReviewOutcome.Suppress(SuppressReason.MalformedOutput, "the model output was empty");
        }

        return Map(context, dto);
    }

    private ReviewOutcome Map(TriggerReviewContext context, ReviewDto dto)
    {
        string decision = dto.Decision?.Trim() ?? string.Empty;

        if (string.Equals(decision, "suppress", StringComparison.OrdinalIgnoreCase))
        {
            // A legitimate, silent decline: the agent reviewed it and judged it not worth surfacing.
            return new ReviewOutcome.Suppress(SuppressReason.NotWorthSurfacing, dto.Reason ?? string.Empty);
        }

        if (!string.Equals(decision, "suggest", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning(
                "Trigger {TriggerId} review returned an unknown decision '{Decision}'; suppressing.",
                context.TriggerId,
                decision);
            return new ReviewOutcome.Suppress(SuppressReason.MalformedOutput, $"unknown decision '{decision}'");
        }

        // A suggest MUST carry a known direction, or it is not a usable proposal. Note the direction is mapped to
        // OrderSide only HERE -- never deserialized as OrderSide, whose zero (Buy) a missing field would read as long.
        OrderSide? side = DirectionToSide(dto.Direction);
        if (side is null)
        {
            _logger.LogWarning(
                "Trigger {TriggerId} review proposed an unknown direction '{Direction}'; suppressing.",
                context.TriggerId,
                dto.Direction);
            return new ReviewOutcome.Suppress(SuppressReason.MalformedOutput, $"unknown direction '{dto.Direction}'");
        }

        // ...and all three prices, or it is not a usable proposal -- fail closed rather than default a missing one.
        if (dto.Entry is not { } entry || dto.Stop is not { } stop || dto.Target is not { } target)
        {
            _logger.LogWarning("Trigger {TriggerId} review omitted a price; suppressing.", context.TriggerId);
            return new ReviewOutcome.Suppress(
                SuppressReason.MalformedOutput, "a suggest must carry entry, stop and target");
        }

        return new ReviewOutcome.Suggest(side.Value, entry, stop, target, dto.Rationale ?? string.Empty);
    }

    private static OrderSide? DirectionToSide(string? direction) => direction?.Trim().ToLowerInvariant() switch
    {
        "long" => OrderSide.Buy,
        "short" => OrderSide.Sell,
        _ => null,
    };

    private static string Render(TriggerReviewContext context) =>
        $"A trigger fired at {context.FiredAt:O}.\n"
        + $"Instrument: {context.Instrument}\n"
        + $"Indicator: {context.Indicator}({context.Period}) over {context.ResolutionMinutes}m bars\n"
        + $"Condition: value {Word(context.Comparison)} {context.Threshold}\n"
        + $"Observed value: {context.ObservedValue}\n"
        + "Decide whether this is worth surfacing as a setup, and respond in the given JSON schema.";

    private static string Word(IndicatorComparison comparison) => comparison switch
    {
        IndicatorComparison.Below => "at or below",
        IndicatorComparison.Above => "at or above",
        _ => "compared to",
    };

    /// <summary>
    /// The structured answer the model returns. Every field is nullable so a missing one reads as <i>absent</i> and
    /// the mapping can fail closed — deliberately <b>not</b> <see cref="OrderSide"/> for <c>direction</c>, whose zero
    /// (<see cref="OrderSide.Buy"/>) a missing field would silently read as a long.
    /// </summary>
    private sealed record ReviewDto(
        string? Decision,
        string? Direction,
        decimal? Entry,
        decimal? Stop,
        decimal? Target,
        string? Rationale,
        string? Reason);
}
