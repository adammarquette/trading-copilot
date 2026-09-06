using System.Text.Json;
using MarqSpec.TradingCopilot.Api.Realtime;
using MarqSpec.TradingCopilot.Api.Suggestions;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Data.Tenancy;
using MarqSpec.TradingCopilot.Domain;
using MarqSpec.TradingCopilot.Domain.Ai;
using MarqSpec.TradingCopilot.Domain.Suggestions;
using MarqSpec.TradingCopilot.Domain.Triggers;
using MarqSpec.TradingCopilot.Domain.Venue;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MarqSpec.TradingCopilot.Api.Chat.Tools;

/// <summary>
/// The <c>generate_suggestion</c> chat tool (gh#1134 of gh#1059, R-6 / R-4, ADR-0025) — the co-pilot <b>proposes</b>
/// a setup on request, staged as an <c>Active</c> <see cref="Suggestion"/> the operator then reads, takes, or ignores.
/// </summary>
/// <remarks>
/// <para>
/// <b>A write tool that is still not an execution path.</b> It stages a row and pushes a card; it reaches no order,
/// venue or gate type, and nothing it writes can execute without the operator's own explicit take (R-11), which runs
/// the risk gate below the model exactly as it does for a scan-issued suggestion. <c>ChatToolBoundaryTests</c> pins
/// that structurally — the constructor's dependency set is pinned <i>exactly</i>, so execution cannot arrive one
/// indirection down — and the gh#930 boundary suite pins it behaviourally, against venue counters that would have
/// moved.
/// </para>
/// <para>
/// <b>What the model does NOT get to decide.</b> The input schema carries <i>no</i> size, mode or expiry:
/// <see cref="Suggestion.Size"/> comes from the operator's configured <see cref="SuggestionOptions.ChatProposalSize"/>,
/// <see cref="Suggestion.Mode"/> is read live off the account (R-14), and <see cref="Suggestion.ExpiresAt"/> is
/// <see cref="SuggestionValidity"/>'s clamp against the market's auto-flatten deadline (R-13) — the same three
/// "the system's, never the model's" values the scan's staging path sets. The model's prices go through the same
/// <see cref="SuggestionGeometry"/> check the scan applies, and an incoherent proposal <b>fails closed</b>: nothing is
/// written and the model is told why.
/// </para>
/// <para>
/// <b>Its own transaction.</b> The write runs in a fresh owner-scoped <see cref="TradingCopilotDbContext"/> built from
/// the shared options — deliberately <b>not</b> the chat endpoint's request context, which the read tools beside it
/// inject. Staging into the endpoint's context would enrol this row in the endpoint's later <c>SaveChanges</c>: a
/// refused turn would commit the proposal anyway, and a CHECK violation here would surface as a failure of the
/// endpoint's conversation write (a constraint backstops only its own transaction's owner). Owner-scoped by
/// construction (R-20): the context is built for the caller, so every read and the write are the operator's own, and
/// another operator's account is not merely refused but invisible.
/// </para>
/// <para>
/// <b>The R-4 issuance throttle is deliberately not applied here.</b> It is a *scan* policy — derived from an
/// account's daily-drawdown headroom, and it exists so unprompted, agent-issued suggestions thin out as headroom
/// depletes. A chat proposal is asked for by the operator in the moment, is bounded by the turn's tool-round cap,
/// and is still gated at take time by R-5, which is the enforcing layer. Wiring the throttle in would silently
/// suppress an answer the operator explicitly requested; if that is ever wanted it is a carded decision, not a
/// side effect of this tool.
/// </para>
/// </remarks>
public sealed class GenerateSuggestionTool : IChatTool
{
    /// <summary>The staged suggestion's version — chat opens its own chain and supersedes nothing.</summary>
    private const int FirstVersion = 1;

    /// <summary>The persisted rationale cap (the <c>Suggestion.Rationale</c> column's <c>HasMaxLength</c>).</summary>
    private const int RationaleMaxLength = 2000;

    private readonly DbContextOptions<TradingCopilotDbContext> _options;
    private readonly ICurrentUser _currentUser;
    private readonly ISessionDeadlineSource _deadlines;
    private readonly ISuggestionRealtimeNotifier _notifier;
    private readonly TimeProvider _clock;
    private readonly SuggestionOptions _suggestions;
    private readonly ILogger<GenerateSuggestionTool> _logger;

    /// <summary>Creates the tool. Every dependency is required — an optional one would silently degrade to a no-op.</summary>
    /// <param name="options">The shared context options, used to build the caller's own R-20-scoped context per call.</param>
    /// <param name="currentUser">The request's operator (R-20) — the owner every row is written under.</param>
    /// <param name="deadlines">The read-only session-deadline seam the expiry clamp reads (never the flatten machinery).</param>
    /// <param name="notifier">The per-owner realtime notifier the staged card is pushed on (presentation-only, best-effort).</param>
    /// <param name="clock">The clock, so issuance and expiry are testable.</param>
    /// <param name="suggestionOptions">The operator's suggestion configuration — the validity window and the chat proposal size.</param>
    /// <param name="logger">The logger (a refusal or a write fault is logged, then failed closed).</param>
    public GenerateSuggestionTool(
        DbContextOptions<TradingCopilotDbContext> options,
        ICurrentUser currentUser,
        ISessionDeadlineSource deadlines,
        ISuggestionRealtimeNotifier notifier,
        TimeProvider clock,
        IOptions<SuggestionOptions> suggestionOptions,
        ILogger<GenerateSuggestionTool> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(currentUser);
        ArgumentNullException.ThrowIfNull(deadlines);
        ArgumentNullException.ThrowIfNull(notifier);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(suggestionOptions);
        ArgumentNullException.ThrowIfNull(logger);
        _options = options;
        _currentUser = currentUser;
        _deadlines = deadlines;
        _notifier = notifier;
        _clock = clock;
        _suggestions = suggestionOptions.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "generate_suggestion";

    /// <inheritdoc />
    public LlmToolDefinition Definition => new(
        Name,
        "Propose a trade setup for the trader to review. It is STAGED for them, never taken: the trader must "
        + "explicitly take it, and the risk gate re-checks it then. Give the instrument, the direction, and the "
        + "entry / stop / target prices you are proposing, plus a short rationale and your confidence. You do NOT "
        + "choose the position quantity, the trading mode, or how long the proposal stays valid — the system sets "
        + "those. A proposal whose stop or target is on the wrong side of entry is rejected and nothing is staged.",
        "{\"type\":\"object\",\"properties\":{"
        + "\"instrument\":{\"type\":\"string\",\"description\":\"The venue-neutral instrument symbol, e.g. ES or MNQ.\"},"
        + "\"side\":{\"type\":\"string\",\"enum\":[\"Buy\",\"Sell\"],\"description\":\"The proposed direction.\"},"
        + "\"entryPrice\":{\"type\":\"number\",\"description\":\"The proposed entry price.\"},"
        + "\"stopPrice\":{\"type\":\"number\",\"description\":\"The protective stop price - below entry for a Buy, above for a Sell.\"},"
        + "\"targetPrice\":{\"type\":\"number\",\"description\":\"The target price - above entry for a Buy, below for a Sell.\"},"
        + "\"rationale\":{\"type\":\"string\",\"description\":\"A short plain-language reason the trader will read.\"},"
        + "\"confidence\":{\"type\":\"integer\",\"description\":\"Your confidence, 0-100. Display only - it changes nothing about risk.\"},"
        + "\"account\":{\"type\":\"string\",\"description\":\"The account to propose against. Omit when the trader has only one tradable account; if a refusal lists account labels, send one of them back exactly.\"}},"
        + "\"required\":[\"instrument\",\"side\",\"entryPrice\",\"stopPrice\",\"targetPrice\",\"rationale\",\"confidence\"]}");

    /// <inheritdoc />
    public async Task<string> ExecuteAsync(string inputJson, CancellationToken cancellationToken)
    {
        Proposal proposal;
        try
        {
            if (Parse(inputJson, out proposal) is { } malformed)
            {
                return Error(malformed);
            }
        }
        catch (JsonException)
        {
            return Error("The tool input was not valid JSON.");
        }

        // THE SAME CHECK THE SCAN MAKES (SuggestionGeometry, R-4): a self-evidently broken proposal -- a non-positive
        // price, or a stop / target on the wrong side of entry -- is refused before anything is written. Fail CLOSED:
        // no partial row, and no "staged but incoherent" card for the operator to trip over.
        if (SuggestionGeometry.Validate(proposal.Side, proposal.EntryPrice, proposal.StopPrice, proposal.TargetPrice)
            is { } geometryError)
        {
            _logger.LogInformation(
                "generate_suggestion refused: incoherent geometry ({Reason}); nothing staged.", geometryError);
            return Error($"That proposal was rejected: {geometryError}. Nothing was staged.");
        }

        try
        {
            // The caller's OWN context (R-20) and its own transaction -- never the chat endpoint's. Built the way the
            // AI-usage ledger builds its per-owner context, for the same reason: a write here must not enrol in,
            // flush, or fail somebody else's SaveChanges.
            await using TradingCopilotDbContext database = new(_options, new OwnerUser(_currentUser.UserId));

            // TRADABILITY IS THE SYSTEM'S. The R-20 filter already limits this to the operator's own accounts; on top
            // of that only a mode-DECLARED, venue-tradable, active account may be proposed against -- an undeclared
            // account is refused everywhere, production included, so nothing is ever suggested on one.
            List<Account> tradable = await database.Accounts
                .AsNoTracking()
                .Where(account => account.Mode != TradingMode.Undeclared && account.CanTrade && account.IsActive)
                .OrderBy(account => account.Name)
                .ToListAsync(cancellationToken);

            // ADDRESSABILITY (gh#1134 review). `Account.Name` is NOT unique -- only (ConnectionId, VenueAccountKey)
            // is -- and two connections yielding same-named venue accounts is a state this product's own firms /
            // connections model produces. Matching on the raw name alone therefore had a dead end with no way out: a
            // refusal listing "PRAC-101, PRAC-101" that no input could resolve. So each candidate gets a label that is
            // unique WITHIN this operator's proposable set, the model is offered those labels, and it can send one
            // back. A label is exactly the account's name whenever that name is already unambiguous, so the ordinary
            // single-account case reads no differently than before.
            List<LabelledAccount> labelled = [.. AddressableLabels(tradable)];

            List<LabelledAccount> candidates = proposal.Account is { Length: > 0 } named
                ? [.. labelled.Where(candidate => candidate.Matches(named))]
                : labelled;

            if (candidates.Count == 0)
            {
                return Error(
                    "No tradable account matched, so nothing was staged. The trader needs an active account whose "
                    + "trading mode is declared."
                    + (labelled.Count == 0
                        ? string.Empty
                        : " The accounts available are: " + string.Join(", ", labelled.Select(c => c.Label)) + "."));
            }

            if (candidates.Count > 1)
            {
                // Ambiguity fails CLOSED rather than picking one: the account is which MONEY the setup is proposed
                // against, and guessing is exactly the kind of choice that must not silently become the model's. The
                // labels below are distinct BY CONSTRUCTION, so this refusal is always resolvable -- that is the whole
                // reason they exist.
                _logger.LogInformation(
                    "generate_suggestion refused: {Count} tradable accounts matched '{Named}'; nothing staged.",
                    candidates.Count,
                    proposal.Account ?? "(unnamed)");
                return Error(
                    "Several tradable accounts matched, so nothing was staged. Name one of these exactly — "
                    + string.Join(", ", candidates.Select(candidate => candidate.Label)) + ".");
            }

            Account target = candidates[0].Account;
            DateTimeOffset now = _clock.GetUtcNow();

            Suggestion staged = new()
            {
                Id = Guid.NewGuid(),
                UserId = _currentUser.UserId,
                AccountId = target.Id,
                Instrument = proposal.Instrument.Symbol,
                Side = proposal.Side,

                // THE SYSTEM'S, NEVER THE MODEL'S -- exactly as on the scan's path. Size is the operator's configured
                // chat proposal size (the schema above has no size property at all), mode is read live off the account
                // (R-14), and the expiry is the configured window clamped so a live suggestion can never outlast the
                // market's auto-flatten deadline (R-13).
                Size = _suggestions.ChatProposalSize,
                Mode = target.Mode,
                ExpiresAt = SuggestionValidity.ExpiresAt(
                    now, _suggestions.Validity, _deadlines.DeadlineFor(proposal.Instrument)),

                EntryPrice = proposal.EntryPrice,
                StopPrice = proposal.StopPrice,
                TargetPrice = proposal.TargetPrice,
                Rationale = proposal.Rationale,
                Confidence = proposal.Confidence,
                State = SuggestionState.Active,
                CreatedAt = now,

                // The producer, stated rather than inferred (gh#1134). The operator's card reads THIS to say
                // "proposed in chat"; a null TriggerFiringId below is a consequence of that provenance, not the
                // evidence for it.
                Origin = SuggestionOrigin.Chat,

                // No firing produced this and no signal is cited: chat is not the scan. A chat proposal opens its own
                // chain (Version 1, superseding nothing) rather than joining a trigger's supersede spine, which is
                // keyed on the firing's trigger identity this row has none of.
                TriggerFiringId = null,
                Version = FirstVersion,
                SupersedesId = null,
            };

            database.Suggestions.Add(staged);
            await database.SaveChangesAsync(cancellationToken);

            // SURFACED, NOT TAKEN: push the card to the owner AFTER the write commits. Presentation-only and
            // best-effort per ADR-0021 -- a hub fault must never unwind a durable proposal.
            await PushSafelyAsync(staged, now, cancellationToken);

            return JsonSerializer.Serialize(new
            {
                suggestionId = staged.Id,
                instrument = staged.Instrument,
                side = staged.Side.ToString(),
                size = staged.Size,
                entryPrice = staged.EntryPrice,
                stopPrice = staged.StopPrice,
                targetPrice = staged.TargetPrice,
                mode = staged.Mode.ToString(),
                account = target.Name,
                expiresAt = staged.ExpiresAt,
                state = staged.State.ToString(),
                staged = "The proposal is staged for the trader to review. It has NOT been taken and no order exists; "
                    + "only the trader can take it, and the risk gate checks it again then.",
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // a genuine caller cancellation, not a write fault to swallow
        }
        catch (Exception error)
        {
            _logger.LogWarning(error, "generate_suggestion write faulted; returning a fail-closed tool error.");
            return Error("The proposal could not be staged right now. Nothing was written.");
        }
    }

    /// <summary>Pushes the staged card to its owner, swallowing a delivery fault (the write is already durable).</summary>
    private async Task PushSafelyAsync(Suggestion staged, DateTimeOffset now, CancellationToken cancellationToken)
    {
        try
        {
            await _notifier.SuggestionChangedAsync(
                staged.UserId, new RealtimeSuggestion(staged.Id, staged.State.ToString(), now), cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            _logger.LogWarning(
                error,
                "generate_suggestion realtime push failed for suggestion {Suggestion}; it is staged regardless.",
                staged.Id);
        }
    }

    /// <summary>
    /// One proposable account paired with the label the model addresses it by (gh#1134 review).
    /// </summary>
    /// <param name="Account">The account itself.</param>
    /// <param name="Label">Its label — unique within the operator's proposable set, by construction.</param>
    private sealed record LabelledAccount(Account Account, string Label)
    {
        /// <summary>
        /// Whether <paramref name="named"/> addresses this account: its label, or its bare name <b>only when that
        /// name is the label</b>. A bare name is deliberately not accepted once it has been qualified — accepting it
        /// would re-introduce the ambiguity the qualification exists to remove.
        /// </summary>
        /// <param name="named">What the model sent.</param>
        /// <returns><see langword="true"/> when this is the account meant.</returns>
        public bool Matches(string named) => string.Equals(Label, named.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Assigns each proposable account a label that is <b>unique within the set</b>, so every refusal this tool emits
    /// names accounts the model can actually send back.
    /// </summary>
    /// <remarks>
    /// The chain widens only as far as it must, so the common case is unchanged: a name that is already unique
    /// <i>is</i> the label. A duplicate name is qualified by its venue account key — the other half of the
    /// <c>(ConnectionId, VenueAccountKey)</c> uniqueness the database actually guarantees — and, if two connections
    /// carry the same account key as well, by the row id, which is unique by definition. So the last rung cannot
    /// collide, which is what makes "name one of these exactly" a promise rather than a hope.
    /// </remarks>
    /// <param name="accounts">The operator's proposable accounts.</param>
    /// <returns>Each account with its label.</returns>
    private static IEnumerable<LabelledAccount> AddressableLabels(IReadOnlyList<Account> accounts)
    {
        HashSet<string> ambiguousNames = [.. accounts
            .GroupBy(account => account.Name, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)];

        HashSet<string> ambiguousKeys = [.. accounts
            .GroupBy(account => account.Name + " " + account.VenueAccountKey, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)];

        foreach (Account account in accounts)
        {
            string label = !ambiguousNames.Contains(account.Name)
                ? account.Name
                : !ambiguousKeys.Contains(account.Name + " " + account.VenueAccountKey)
                    ? $"{account.Name} ({account.VenueAccountKey})"
                    : $"{account.Name} ({account.VenueAccountKey} · {account.Id})";

            yield return new LabelledAccount(account, label);
        }
    }

    /// <summary>The model's parsed proposal — everything it is allowed to choose, and nothing it is not.</summary>
    private sealed record Proposal(
        InstrumentId Instrument,
        OrderSide Side,
        decimal EntryPrice,
        decimal StopPrice,
        decimal TargetPrice,
        string Rationale,
        int Confidence,
        string? Account);

    /// <summary>The owner the per-call context is scoped to (R-20) — the same idiom as the AI-usage ledger's.</summary>
    private sealed record OwnerUser(Guid UserId) : ICurrentUser;

    /// <summary>
    /// Parses and validates the model's input, returning a refusal string (or <see langword="null"/>) plus the
    /// proposal. Every field is checked against the bounds the database CHECKs enforce, so a malformed proposal is
    /// refused as a tool error the model can read rather than surfacing as a write failure it cannot.
    /// </summary>
    private static string? Parse(string inputJson, out Proposal proposal)
    {
        proposal = null!;
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

        if (!TryString(root, "instrument", out string symbol)
            || !InstrumentId.TryParse(symbol, out InstrumentId instrument))
        {
            return "A proposal needs a non-blank instrument symbol.";
        }

        if (!TryString(root, "side", out string sideText)
            || !Enum.TryParse(sideText, ignoreCase: true, out OrderSide side)
            || side is not (OrderSide.Buy or OrderSide.Sell))
        {
            return "The side must be Buy or Sell.";
        }

        if (!TryDecimal(root, "entryPrice", out decimal entry)
            || !TryDecimal(root, "stopPrice", out decimal stop)
            || !TryDecimal(root, "targetPrice", out decimal target))
        {
            return "entryPrice, stopPrice and targetPrice must all be numbers.";
        }

        if (!TryString(root, "rationale", out string rationale))
        {
            return "A proposal needs a short rationale the trader can read.";
        }

        if (rationale.Length > RationaleMaxLength)
        {
            // Refused, never truncated: a truncated rationale is a different claim from the one the model made.
            return $"The rationale must be at most {RationaleMaxLength} characters.";
        }

        if (!TryInt(root, "confidence", out int confidence) || confidence is < 0 or > 100)
        {
            return "confidence must be a whole number between 0 and 100.";
        }

        _ = TryString(root, "account", out string account);
        proposal = new Proposal(
            instrument, side, entry, stop, target, rationale, confidence, account.Length == 0 ? null : account);
        return null;
    }

    private static bool TryString(JsonElement root, string name, out string value)
    {
        value = string.Empty;
        if (!root.TryGetProperty(name, out JsonElement element) || element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        // Trimmed BEFORE the blank check, so a whitespace-only value leaves `value` empty rather than three spaces.
        // Every other caller reads `value` only when this returns true, but `account` reads it regardless -- so
        // without the trim, a model sending "account": "   " instead of omitting the key would be treated as having
        // NAMED an account, and would miss the single-account fallback the schema promises it (gh#1148 review).
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
