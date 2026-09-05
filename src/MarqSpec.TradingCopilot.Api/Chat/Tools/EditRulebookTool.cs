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
/// The <c>edit_rulebook</c> chat tool (gh#1059, R-7 / R-6, ADR-0029) — the co-pilot turns a practice the trader
/// stated in chat into a durable, standing <see cref="TriggerRecord"/> they can see, confirm, edit or delete.
/// </summary>
/// <remarks>
/// <para>
/// <b>Authorship arms nothing.</b> Every trigger this writes — created or amended — is
/// <see cref="TriggerConfirmation.Unconfirmed"/>, which is <b>inert regardless of <c>Enabled</c></b> (gh#470): the
/// scan skips it, so it can never fire, page, or reach the agent-review reviewer. The operator's separate
/// <c>POST /api/triggers/{id}/confirm</c> is the only thing that arms a trigger, and it is deliberately not
/// reachable from here. An <b>amend</b> therefore <i>disarms</i>: a model edit to a trigger the operator had already
/// confirmed drops it back to Unconfirmed and awaits a fresh confirmation, so the operator can never be paged by a
/// condition they never agreed to.
/// </para>
/// <para>
/// <b>Chat authors the mechanical route only.</b> The agent-review route carries an account and a contract size —
/// the operator's, never the model's — so it is simply absent from this tool's schema and refused on an amend: a
/// chat-authored trigger alerts, it never issues a sized suggestion against an account. Widening that is a separate,
/// separately-reviewed decision.
/// </para>
/// <para>
/// <b>Provenance is recorded.</b> A trigger written here stamps <see cref="TriggerRecord.SourceConversationId"/>
/// (gh#471) from <see cref="IChatTurnScope"/>, so "why does this exist?" is answerable at the read path. The tool
/// <b>fails closed</b> when no conversation is in scope rather than writing an unattributed rule.
/// <see cref="TriggerRecord.SourceRuleId"/> stays <see langword="null"/>: the R-7 <c>Rule</c> entity is gh#866,
/// deliberately still backlogged so the NL→condition compiler pins its schema rather than this increment (operator
/// decision, 2026-09-04 — see ADR-0029).
/// </para>
/// <para>
/// The validation is <see cref="TriggerAuthoring"/>'s — the same refusals the authoring endpoint has always made,
/// shared rather than copied, so a model-authored trigger is never checked by an older copy of the rules. The write
/// runs in a fresh owner-scoped context (R-20) and its own transaction, like its sibling write tool.
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
    /// <param name="turnScope">The conversation this turn runs in — the trigger's authorship provenance (gh#471).</param>
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
        "Write a standing rule into the trader's rulebook as an alert trigger — \"tell me when <indicator>(<period>) "
        + "on <resolution>m is below/above <threshold> on <symbol>\" — or amend one they already have by passing its "
        + "triggerId. Use it when the trader states a practice or asks to be alerted on a condition. Whatever you "
        + "write is UNCONFIRMED and therefore INERT: it cannot fire or alert until the trader confirms it "
        + "themselves, and amending a rule they had already confirmed returns it to unconfirmed. It only alerts — it "
        + "never trades, sizes, or names an account.",
        "{\"type\":\"object\",\"properties\":{"
        + "\"triggerId\":{\"type\":\"string\",\"description\":\"The id of an existing rule to amend. Omit to author a new one.\"},"
        + "\"symbol\":{\"type\":\"string\",\"description\":\"The venue-neutral instrument symbol, e.g. ES. Required when authoring.\"},"
        + "\"indicator\":{\"type\":\"string\",\"enum\":[\"atr\",\"rsi\"],\"description\":\"The indicator to watch. Required when authoring.\"},"
        + "\"period\":{\"type\":\"integer\",\"description\":\"The indicator period; positive. Required when authoring.\"},"
        + "\"resolutionMinutes\":{\"type\":\"integer\",\"description\":\"The bar size in minutes; positive. Required when authoring.\"},"
        + "\"comparison\":{\"type\":\"string\",\"enum\":[\"Below\",\"Above\"],\"description\":\"Which side of the threshold fires. Required when authoring.\"},"
        + "\"threshold\":{\"type\":\"number\",\"description\":\"The value to compare against. Required when authoring.\"},"
        + "\"hysteresis\":{\"type\":\"number\",\"description\":\"An optional positive re-arm dead-band.\"},"
        + "\"severity\":{\"type\":\"string\",\"enum\":[\"Quiet\",\"Notify\",\"Page\"],\"description\":\"How loudly the alert arrives. Defaults to Notify.\"}},"
        + "\"required\":[]}");

    /// <inheritdoc />
    public async Task<string> ExecuteAsync(string inputJson, CancellationToken cancellationToken)
    {
        // FAIL CLOSED ON MISSING PROVENANCE. A chat tool only ever runs inside a turn, so a null conversation means
        // the wiring is broken -- and an unattributed rule is exactly the row nobody can later explain. Refuse rather
        // than write one.
        if (_turnScope.ConversationId is not { } conversationId)
        {
            _logger.LogError("edit_rulebook ran with no conversation in scope; refusing to author an unattributed rule.");
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

    /// <summary>Authors a new mechanical trigger — every condition field is required, and the result is inert.</summary>
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

        if (edit.Comparison is not { } comparison || TriggerAuthoring.RefuseComparison(comparison) is not null)
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

        // The ADR-0019 silent-monitor guard (gh#1007): a threshold outside the indicator's meaningful range seeds the
        // debounce straight to Fired and holds there, so the rule would look authored and never alert. Refused here
        // exactly as at the authoring endpoint.
        if (TriggerThreshold.Refusal(indicatorName, threshold) is { } thresholdError)
        {
            return Error(thresholdError);
        }

        TriggerRecord trigger = new()
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
            Enabled = true,

            // THE GATE (gh#470). Enabled but UNCONFIRMED is inert: authorship arms nothing, and only the operator's
            // own POST /{id}/confirm accepts a rule into the firing set.
            Confirmation = TriggerConfirmation.Unconfirmed,
            ArmState = TriggerArmState.Unseeded,
            ArmCycle = 0,
            CreatedAt = _clock.GetUtcNow(),

            // R-7 provenance (gh#471): the conversation this rule was authored in. SourceRuleId stays null -- the
            // Rule entity is gh#866, still future work (ADR-0029).
            SourceConversationId = conversationId,
            SourceRuleId = null,
        };

        database.Triggers.Add(trigger);
        await database.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "edit_rulebook authored trigger {Trigger} from conversation {Conversation}; Unconfirmed and inert.",
            trigger.Id, conversationId);
        return Describe(trigger, "authored");
    }

    /// <summary>
    /// Amends an existing trigger — only the condition half, and always <b>disarming</b> it back to Unconfirmed.
    /// The route, account and size are never touched, so an amend can never turn an alert into a sized proposal.
    /// </summary>
    private async Task<string> AmendAsync(
        TradingCopilotDbContext database, Guid id, Edit edit, Guid conversationId, CancellationToken cancellationToken)
    {
        // The default-deny R-20 filter scopes this read to the caller, so another operator's trigger -- or one that
        // does not exist -- is indistinguishable from absent.
        TriggerRecord? trigger = await database.Triggers
            .FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
        if (trigger is null)
        {
            return Error("No such rule for this trader, so nothing was changed.");
        }

        if (edit.Threshold is { } threshold
            && TriggerThreshold.Refusal(trigger.Indicator, threshold) is { } thresholdError)
        {
            return Error(thresholdError);
        }

        if (TriggerAuthoring.RefuseHysteresis(edit.Hysteresis) is { } hysteresisError)
        {
            return Error(hysteresisError);
        }

        if (edit.Comparison is { } comparison && TriggerAuthoring.RefuseComparison(comparison) is not null)
        {
            return Error("The comparison must be Below or Above.");
        }

        // Validated whole ABOVE, applied here -- the top-guard shape the patch endpoint uses, so a refused amend
        // leaves the stored rule untouched rather than half-edited.
        if (edit.Threshold is { } newThreshold)
        {
            trigger.Threshold = newThreshold;
        }

        if (edit.Hysteresis is { } newHysteresis)
        {
            trigger.Hysteresis = newHysteresis;
        }

        if (edit.Comparison is { } newComparison)
        {
            trigger.Comparison = newComparison;
        }

        if (edit.Severity is { } newSeverity)
        {
            trigger.Severity = newSeverity;
        }

        // THE DISARM. A model edit to a rule the operator had already confirmed returns it to Unconfirmed: the
        // operator confirmed the OLD condition, not this one, so the confirmation does not carry over. Without this,
        // an amend would be the one path by which a model change reaches the live firing set.
        trigger.Confirmation = TriggerConfirmation.Unconfirmed;

        // The same re-seed the patch endpoint does: a condition that became true under the old definition must
        // re-seed silently rather than fire, and a fresh cycle stops the next genuine crossing being deduped away.
        trigger.ArmState = TriggerArmState.Unseeded;
        trigger.ArmCycle++;

        // Provenance follows the edit: this rule's current definition came from this conversation.
        trigger.SourceConversationId = conversationId;

        await database.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "edit_rulebook amended trigger {Trigger} from conversation {Conversation}; returned to Unconfirmed.",
            trigger.Id, conversationId);
        return Describe(trigger, "amended");
    }

    /// <summary>The tool result the model reads — the stored rule, and the fact that it is inert until confirmed.</summary>
    private static string Describe(TriggerRecord trigger, string action) => JsonSerializer.Serialize(new
    {
        triggerId = trigger.Id,
        action,
        symbol = trigger.Symbol,
        indicator = trigger.Indicator,
        period = trigger.Period,
        resolutionMinutes = trigger.ResolutionMinutes,
        comparison = trigger.Comparison.ToString(),
        threshold = trigger.Threshold,
        hysteresis = trigger.Hysteresis,
        route = trigger.Route.ToString(),
        severity = trigger.Severity.ToString(),
        enabled = trigger.Enabled,
        confirmation = trigger.Confirmation.ToString(),
        inert = "The rule is UNCONFIRMED, so it is inert and cannot alert. Tell the trader to confirm it in the "
            + "rulebook if they want it live.",
    });

    /// <summary>The model's parsed edit. Every field is optional here; which are required depends on author vs amend.</summary>
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

    /// <summary>Parses the model's input into an <see cref="Edit"/>, or returns a refusal string.</summary>
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
            if (!Guid.TryParse(triggerIdText, out Guid parsed))
            {
                return "triggerId must be the id of an existing rule.";
            }

            triggerId = parsed;
        }

        IndicatorComparison? comparison = null;
        if (TryString(root, "comparison", out string comparisonText))
        {
            if (!Enum.TryParse(comparisonText, ignoreCase: true, out IndicatorComparison parsed)
                || parsed == IndicatorComparison.Unknown)
            {
                return "The comparison must be Below or Above.";
            }

            comparison = parsed;
        }

        NotificationSeverity? severity = null;
        if (TryString(root, "severity", out string severityText))
        {
            if (!Enum.TryParse(severityText, ignoreCase: true, out NotificationSeverity parsed))
            {
                return "The severity must be Quiet, Notify or Page.";
            }

            severity = parsed;
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

        value = element.GetString() ?? string.Empty;
        return !string.IsNullOrWhiteSpace(value);
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
