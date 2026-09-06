using System.Text.Json;
using MarqSpec.TradingCopilot.Api.Triggers;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Data.Tenancy;
using MarqSpec.TradingCopilot.Domain;
using MarqSpec.TradingCopilot.Domain.Ai;
using MarqSpec.TradingCopilot.Domain.Notifications;
using MarqSpec.TradingCopilot.Domain.Triggers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MarqSpec.TradingCopilot.Api.Chat.Tools;

/// <summary>
/// The <c>edit_rulebook</c> chat tool (gh#1135 of gh#1059, R-7 / R-6, ADR-0025) — the co-pilot turns a practice the
/// operator stated in chat into a durable, standing <see cref="TriggerRecord"/> they can see, confirm, edit or delete.
/// </summary>
/// <remarks>
/// <para>
/// <b>Authorship arms nothing.</b> Every trigger this writes — authored or amended — is
/// <see cref="TriggerConfirmation.Unconfirmed"/>, which is <b>inert regardless of <c>Enabled</c></b> (gh#470): the
/// scan's due-set predicate takes only <c>Confirmed</c> rows, so an authored rule can never fire, page, or wake the
/// agent-review reviewer. The operator's separate <c>POST /api/triggers/{id}/confirm</c> is the only thing that arms
/// a trigger, and it is deliberately not reachable from here — "written by the model" and "armed" must never be the
/// same act. An <b>amend</b> therefore <i>disarms</i>: a model edit to a rule the operator had already confirmed
/// drops it back to Unconfirmed and awaits a fresh confirmation, because what they confirmed was the <i>old</i>
/// condition. Without that, an amend would be the one path by which a model change reached the live firing set.
/// </para>
/// <para>
/// <b>Chat authors the mechanical route only.</b> The agent-review route carries an account and a contract size —
/// the operator's, never the model's — so it is simply <i>absent from this tool's schema</i> rather than validated,
/// and an amend never touches <see cref="TriggerRecord.Route"/>, <see cref="TriggerRecord.AccountId"/> or
/// <see cref="TriggerRecord.Size"/>. A chat-authored rule alerts; it never issues a sized suggestion against an
/// account. Widening that is a separate, separately-reviewed decision. The same "remove the choice from the schema"
/// rule (ADR-0025's write-tool update) covers <c>Enabled</c> and the confirmation itself: neither is parsed at all,
/// so a model that invents the property changes nothing.
/// </para>
/// <para>
/// <b>The validation is the authoring endpoint's, shared rather than copied.</b> <see cref="TriggerAuthoring"/> makes
/// the same symbol / indicator / period / resolution / comparison / hysteresis refusals the operator's own
/// <c>POST /api/triggers</c> has always made, and <see cref="TriggerThreshold"/> the same per-indicator range refusal
/// (gh#1007 / ADR-0019: a permanently-satisfied condition seeds the debounce straight to <c>Fired</c> and holds
/// there, so the rule looks authored and never alerts). A model-authored trigger is exactly the caller you least want
/// validated by an older copy of those rules.
/// </para>
/// <para>
/// <b>Provenance is recorded, and missing provenance fails closed.</b> A rule written here stamps
/// <see cref="TriggerRecord.SourceConversationId"/> (gh#471) from <see cref="IChatTurnScope"/>, so "why does this
/// exist?" is answerable at the read path. A chat tool only ever runs inside a turn, so a null scope means the wiring
/// is broken — the tool refuses rather than writing an <i>unattributed</i> rule nobody can later explain.
/// <see cref="TriggerRecord.SourceRuleId"/> stays <see langword="null"/>: the R-7 <c>Rule</c> entity is gh#866, still
/// backlogged, and inventing an id for a row that does not exist would be worse than leaving the seam empty.
/// </para>
/// <para>
/// <b>Its own transaction, owner-scoped.</b> The write runs in a fresh <see cref="TradingCopilotDbContext"/> built
/// for the caller from the shared options — deliberately <b>not</b> the chat endpoint's request context, which the
/// read tools beside it inject. Writing into the endpoint's context would enrol the rule in the endpoint's later
/// <c>SaveChanges</c>: a refused turn would commit it anyway, and a <c>Triggers</c> CHECK violation here would
/// surface as a failure of the endpoint's conversation write (a constraint backstops only its own transaction's
/// owner). Owner-scoped by construction (R-20): another operator's rulebook is not merely refused but invisible.
/// </para>
/// <para>
/// <b>It reaches no order, venue, or gate type</b> — it holds the operator's own store, the clock, and a
/// <see cref="Guid"/>. <c>ChatToolBoundaryTests</c> pins that structurally, with this tool's constructor dependency
/// set pinned <i>exactly</i>, since a fragment scan is defeated by one indirection.
/// </para>
/// </remarks>
public sealed class EditRulebookTool : IChatTool
{
    private readonly DbContextOptions<TradingCopilotDbContext> _options;
    private readonly ICurrentUser _currentUser;
    private readonly IChatTurnScope _turnScope;
    private readonly TimeProvider _clock;
    private readonly ILogger<EditRulebookTool> _logger;

    /// <summary>Creates the tool. Every dependency is required — an optional one would silently degrade to a no-op.</summary>
    /// <param name="options">The shared context options, used to build the caller's own R-20-scoped context per call.</param>
    /// <param name="currentUser">The request's operator (R-20) — the owner every row is written under.</param>
    /// <param name="turnScope">The conversation this turn runs in — the rule's authorship provenance (gh#471).</param>
    /// <param name="clock">The clock, so <c>CreatedAt</c> is testable.</param>
    /// <param name="logger">The logger (a refusal or a write fault is logged, then failed closed).</param>
    public EditRulebookTool(
        DbContextOptions<TradingCopilotDbContext> options,
        ICurrentUser currentUser,
        IChatTurnScope turnScope,
        TimeProvider clock,
        ILogger<EditRulebookTool> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(currentUser);
        ArgumentNullException.ThrowIfNull(turnScope);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);
        _options = options;
        _currentUser = currentUser;
        _turnScope = turnScope;
        _clock = clock;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "edit_rulebook";

    /// <inheritdoc />
    public LlmToolDefinition Definition => new(
        Name,
        "Write a standing rule into the trader's rulebook as an alert — \"tell me when <indicator>(<period>) on "
        + "<resolution>m is below/above <threshold> on <symbol>\" — or amend one they already have by passing its "
        + "triggerId. Use it when the trader states a practice or asks to be alerted on a condition. Whatever you "
        + "write is UNCONFIRMED and therefore INERT: it cannot fire or alert until the trader confirms it "
        + "themselves in the rulebook, and amending a rule they had already confirmed returns it to unconfirmed. It "
        + "only alerts — it never trades, sizes a position, or names an account.",
        "{\"type\":\"object\",\"properties\":{"
        + "\"triggerId\":{\"type\":\"string\",\"description\":\"The id of an existing rule to amend. Omit to author a new one.\"},"
        + "\"symbol\":{\"type\":\"string\",\"description\":\"The venue-neutral instrument symbol, e.g. ES. Required when authoring.\"},"
        + "\"indicator\":{\"type\":\"string\",\"enum\":[\"atr\",\"rsi\"],\"description\":\"The indicator to watch. Required when authoring.\"},"
        + "\"period\":{\"type\":\"integer\",\"description\":\"The indicator period; a positive whole number. Required when authoring.\"},"
        + "\"resolutionMinutes\":{\"type\":\"integer\",\"description\":\"The bar size in minutes; a positive whole number. Required when authoring.\"},"
        + "\"comparison\":{\"type\":\"string\",\"enum\":[\"Below\",\"Above\"],\"description\":\"Which side of the threshold alerts. Required when authoring.\"},"
        + "\"threshold\":{\"type\":\"number\",\"description\":\"The value to compare against; rsi must be between 0 and 100 exclusive, atr above 0. Required when authoring.\"},"
        + "\"hysteresis\":{\"type\":\"number\",\"description\":\"An optional positive re-arm dead-band.\"},"
        + "\"severity\":{\"type\":\"string\",\"enum\":[\"Quiet\",\"Notify\",\"Page\"],\"description\":\"How loudly the alert arrives. Defaults to Notify.\"}},"
        + "\"required\":[]}");

    /// <inheritdoc />
    public async Task<string> ExecuteAsync(string inputJson, CancellationToken cancellationToken)
    {
        // FAIL CLOSED ON MISSING PROVENANCE. A chat tool only ever runs inside a turn, so a null conversation means
        // the endpoint never entered the scope -- i.e. the wiring is broken. An unattributed rule is exactly the row
        // nobody can later explain, so refuse rather than write one.
        if (_turnScope.ConversationId is not { } conversationId)
        {
            _logger.LogError(
                "edit_rulebook ran with no conversation in scope; refusing to author an unattributed rule.");
            return Error("The rulebook could not be edited right now. Nothing was written.");
        }

        Edit edit;
        try
        {
            if (Parse(inputJson, out edit) is { } malformed)
            {
                return Error(malformed);
            }
        }
        catch (JsonException)
        {
            return Error("The tool input was not valid JSON.");
        }

        try
        {
            // The caller's OWN context (R-20) and its own transaction -- never the chat endpoint's. Built the way the
            // AI-usage ledger builds its per-owner context, for the same reason: a write here must not enrol in,
            // flush, or fail somebody else's SaveChanges.
            await using TradingCopilotDbContext database = new(_options, new OwnerUser(_currentUser.UserId));
            return edit.TriggerId is { } id
                ? await AmendAsync(database, id, edit, conversationId, cancellationToken)
                : await AuthorAsync(database, edit, conversationId, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // a genuine caller cancellation, not a write fault to swallow
        }
        catch (Exception error)
        {
            _logger.LogWarning(error, "edit_rulebook write faulted; returning a fail-closed tool error.");
            return Error("The rulebook could not be edited right now. Nothing was written.");
        }
    }

    /// <summary>
    /// Authors a new mechanical rule. Every condition field is required — an authoring call is a whole rule or
    /// nothing, since a half-specified condition has no safe default to fill in. Validated <b>whole, before</b> the
    /// row is built, so a refusal writes nothing at all.
    /// </summary>
    private async Task<string> AuthorAsync(
        TradingCopilotDbContext database, Edit edit, Guid conversationId, CancellationToken cancellationToken)
    {
        if (TriggerAuthoring.RefuseSymbol(edit.Symbol, out InstrumentId instrument) is { } symbolError)
        {
            return Error(symbolError);
        }

        if (TriggerAuthoring.RefuseIndicator(edit.Indicator, out string indicatorName) is { } indicatorError)
        {
            return Error(indicatorError);
        }

        if (edit.Period is not { } period || TriggerAuthoring.RefusePeriod(period) is not null)
        {
            return Error("The period must be a positive whole number.");
        }

        if (edit.ResolutionMinutes is not { } resolution || TriggerAuthoring.RefuseResolution(resolution) is not null)
        {
            return Error("The resolution must be a positive whole number of minutes.");
        }

        if (edit.Comparison is not { } comparison)
        {
            return Error("The comparison must be Below or Above.");
        }

        if (edit.Threshold is not { } threshold)
        {
            return Error("The threshold must be a number.");
        }

        if (TriggerAuthoring.RefuseHysteresis(edit.Hysteresis) is { } hysteresisError)
        {
            return Error(hysteresisError);
        }

        // The ADR-0019 silent-monitor guard (gh#1007), refused here exactly as at the authoring endpoint and against
        // the RESOLVED indicator, because the bound is that indicator's own semantics rather than a blanket rule.
        if (TriggerThreshold.Refusal(indicatorName, threshold) is { } thresholdError)
        {
            return Error(thresholdError);
        }

        TriggerRecord rule = new()
        {
            Id = Guid.NewGuid(),
            UserId = _currentUser.UserId,
            Symbol = instrument.Symbol,
            Indicator = indicatorName,
            Period = period,
            ResolutionMinutes = resolution,
            ConditionKind = TriggerConditionKind.IndicatorThreshold,
            Comparison = comparison,
            Threshold = threshold,
            Hysteresis = edit.Hysteresis,

            // MECHANICAL ONLY (see the type remarks): chat alerts, it never issues a sized suggestion against an
            // account, so no account and no size ride with a chat-authored rule.
            Route = TriggerRoute.Mechanical,
            AccountId = null,
            Size = null,

            Severity = edit.Severity ?? NotificationSeverity.Notify,

            // ENABLED BUT UNCONFIRMED IS INERT (gh#470). Enabled is the operator's later pause switch; the
            // confirmation is the gate. Authorship arms nothing, and only the operator's own POST /{id}/confirm
            // accepts a rule into the firing set -- neither value is the model's to send.
            Enabled = true,
            Confirmation = TriggerConfirmation.Unconfirmed,

            ArmState = TriggerArmState.Unseeded,
            ArmCycle = 0,
            CreatedAt = _clock.GetUtcNow(),

            // R-7 provenance (gh#471): the conversation this rule was authored in. SourceRuleId stays null -- the
            // Rule entity is gh#866, still backlogged, and an invented id would point at nothing.
            SourceConversationId = conversationId,
            SourceRuleId = null,
        };

        database.Triggers.Add(rule);
        await database.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "edit_rulebook authored trigger {Trigger} from conversation {Conversation}; Unconfirmed and inert.",
            rule.Id,
            conversationId);
        return Describe(rule, "authored");
    }

    /// <summary>
    /// Amends an existing rule — the condition's <b>values</b> only, and always <b>disarming</b> it back to
    /// Unconfirmed. The indicator, period, resolution and symbol are its identity: changing those is authoring a
    /// different rule, so they are not amendable and the route / account / size are never touched at all.
    /// </summary>
    private async Task<string> AmendAsync(
        TradingCopilotDbContext database, Guid id, Edit edit, Guid conversationId, CancellationToken cancellationToken)
    {
        // The default-deny R-20 filter scopes this read to the caller, so another operator's rule -- or one that does
        // not exist -- is indistinguishable from absent.
        TriggerRecord? rule = await database.Triggers
            .FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
        if (rule is null)
        {
            return Error("No such rule for this trader, so nothing was changed.");
        }

        // VALIDATED WHOLE, BEFORE ANYTHING IS APPLIED -- the top-guard shape the patch endpoint uses, so a refused
        // amend leaves the stored rule untouched rather than half-edited, and does not even disarm it.
        if (edit.Threshold is { } proposed
            && TriggerThreshold.Refusal(rule.Indicator, proposed) is { } thresholdError)
        {
            return Error(thresholdError);
        }

        if (TriggerAuthoring.RefuseHysteresis(edit.Hysteresis) is { } hysteresisError)
        {
            return Error(hysteresisError);
        }

        if (edit.Threshold is { } threshold)
        {
            rule.Threshold = threshold;
        }

        if (edit.Hysteresis is { } hysteresis)
        {
            rule.Hysteresis = hysteresis;
        }

        if (edit.Comparison is { } comparison)
        {
            rule.Comparison = comparison;
        }

        if (edit.Severity is { } severity)
        {
            rule.Severity = severity;
        }

        // THE DISARM. The operator confirmed the OLD condition, not this one, so the confirmation does not carry
        // over. Without this, an amend would be the one path by which a model change reaches the live firing set.
        rule.Confirmation = TriggerConfirmation.Unconfirmed;

        // The same re-seed the patch endpoint does: a condition that became true under the old definition must
        // re-seed silently rather than fire, and a fresh cycle stops the next genuine crossing being suppressed as a
        // duplicate of the incident the old cycle left open.
        rule.ArmState = TriggerArmState.Unseeded;
        rule.ArmCycle++;

        // Provenance follows the edit: this rule's CURRENT definition came from this conversation.
        rule.SourceConversationId = conversationId;

        await database.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "edit_rulebook amended trigger {Trigger} from conversation {Conversation}; returned to Unconfirmed.",
            rule.Id,
            conversationId);
        return Describe(rule, "amended");
    }

    /// <summary>The tool result the model reads — the stored rule, and the fact that it is inert until confirmed.</summary>
    private static string Describe(TriggerRecord rule, string action) => JsonSerializer.Serialize(new
    {
        triggerId = rule.Id,
        action,
        symbol = rule.Symbol,
        indicator = rule.Indicator,
        period = rule.Period,
        resolutionMinutes = rule.ResolutionMinutes,
        comparison = rule.Comparison.ToString(),
        threshold = rule.Threshold,
        hysteresis = rule.Hysteresis,
        route = rule.Route.ToString(),
        severity = rule.Severity.ToString(),
        enabled = rule.Enabled,
        confirmation = rule.Confirmation.ToString(),

        // Stated in the result, not only in the schema: a model that believes it armed an alert will tell the
        // operator it did, and the operator will then rely on an alert that cannot arrive.
        inert = "The rule is UNCONFIRMED, so it is inert and cannot alert. Tell the trader it is waiting for them to "
            + "confirm it in the rulebook.",
    });

    /// <summary>The owner the per-call context is scoped to (R-20) — the same idiom as the AI-usage ledger's.</summary>
    private sealed record OwnerUser(Guid UserId) : ICurrentUser;

    /// <summary>
    /// The model's parsed edit — everything it is allowed to choose, and nothing it is not. Every field is optional
    /// here; which are <i>required</i> depends on author versus amend, and is decided by those two paths.
    /// </summary>
    private sealed record Edit(
        Guid? TriggerId,
        string? Symbol,
        string? Indicator,
        int? Period,
        int? ResolutionMinutes,
        IndicatorComparison? Comparison,
        decimal? Threshold,
        decimal? Hysteresis,
        NotificationSeverity? Severity);

    /// <summary>
    /// Parses the model's input, returning a refusal string (or <see langword="null"/>) plus the edit. A value that
    /// is present but unusable — a corrupt enum, an unparseable id — is refused here rather than silently dropped,
    /// because "you sent something I could not read" and "you sent nothing" mean different things to the model.
    /// </summary>
    private static string? Parse(string inputJson, out Edit edit)
    {
        edit = null!;
        if (string.IsNullOrWhiteSpace(inputJson))
        {
            return "The tool input was empty.";
        }

        using JsonDocument document = JsonDocument.Parse(inputJson);
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return "The tool input must be a JSON object.";
        }

        Guid? triggerId = null;
        if (TryString(root, "triggerId", out string triggerIdText))
        {
            if (!Guid.TryParse(triggerIdText, out Guid parsedId))
            {
                return "The triggerId must be the id of an existing rule.";
            }

            triggerId = parsedId;
        }

        IndicatorComparison? comparison = null;
        if (TryString(root, "comparison", out string comparisonText))
        {
            if (!Enum.TryParse(comparisonText, ignoreCase: true, out IndicatorComparison parsedComparison)
                || parsedComparison == IndicatorComparison.Unknown)
            {
                return "The comparison must be Below or Above.";
            }

            comparison = parsedComparison;
        }

        NotificationSeverity? severity = null;
        if (TryString(root, "severity", out string severityText))
        {
            if (!Enum.TryParse(severityText, ignoreCase: true, out NotificationSeverity parsedSeverity)
                || !Enum.IsDefined(parsedSeverity))
            {
                return "The severity must be Quiet, Notify or Page.";
            }

            severity = parsedSeverity;
        }

        edit = new Edit(
            triggerId,
            TryString(root, "symbol", out string symbol) ? symbol : null,
            TryString(root, "indicator", out string indicator) ? indicator : null,
            TryInt(root, "period", out int period) ? period : null,
            TryInt(root, "resolutionMinutes", out int resolution) ? resolution : null,
            comparison,
            TryDecimal(root, "threshold", out decimal threshold) ? threshold : null,
            TryDecimal(root, "hysteresis", out decimal hysteresis) ? hysteresis : null,
            severity);
        return null;
    }

    private static bool TryString(JsonElement root, string name, out string value)
    {
        value = string.Empty;
        if (!root.TryGetProperty(name, out JsonElement element) || element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        // Trimmed BEFORE the blank check, so a whitespace-only value reads as absent rather than as three spaces
        // (the gh#1148 review's finding on the sibling tool, folded in here rather than rediscovered).
        value = (element.GetString() ?? string.Empty).Trim();
        return value.Length > 0;
    }

    private static bool TryDecimal(JsonElement root, string name, out decimal value)
    {
        value = 0m;
        return root.TryGetProperty(name, out JsonElement element)
            && element.ValueKind == JsonValueKind.Number
            && element.TryGetDecimal(out value);
    }

    private static bool TryInt(JsonElement root, string name, out int value)
    {
        value = 0;
        return root.TryGetProperty(name, out JsonElement element)
            && element.ValueKind == JsonValueKind.Number
            && element.TryGetInt32(out value);
    }

    private static string Error(string message) => JsonSerializer.Serialize(new { error = message });
}
