using MarqSpec.TradingCopilot.Api.Realtime;
using MarqSpec.TradingCopilot.Api.Suggestions;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Domain;
using MarqSpec.TradingCopilot.Domain.Suggestions;
using MarqSpec.TradingCopilot.Domain.Venue;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MarqSpec.TradingCopilot.Api.MarketData;

/// <summary>
/// The suggestion-drift watcher's core (gh#546, R-4 / R-12): on each batch of <c>market.quote</c> events, mark every
/// <b>Active</b> suggestion whose price has drifted past the entry tolerance <see cref="SuggestionState.Stale"/> — so
/// a scratched setup is surfaced (greyed) <b>before</b> execution, not only re-checked at take time.
/// </summary>
/// <remarks>
/// <para>
/// <b>Symbol-driven, because the keys do not match.</b> A quote carries the venue <b>contract key</b>
/// (<c>CON.F.US.ES.U26</c>) but a suggestion stores only the venue-<b>neutral</b> symbol (<c>ES</c>), and the two are
/// not interconvertible without a venue resolve (there is no reverse lookup — the front month rolls, so a stored key
/// would go stale). So the loop resolves each distinct Active symbol <b>forward</b> to its current front-month key
/// <b>once per pass</b> (never per quote — <c>ResolveContractAsync</c> is a venue round-trip),
/// then matches the batch's quotes to it. The tick-scaled band is resolved from the instrument spec per symbol, so a
/// long and a short on the same instrument share one resolve.
/// </para>
/// <para>
/// <b>Cannot measure ⇒ do not transition</b> (R-4): a symbol with no configured spec, an unparseable one, or one the
/// venue cannot resolve this pass simply leaves its suggestions <see cref="SuggestionState.Active"/> — never a
/// fabricated price or a default tick. The take-time synchronous re-check (gh#548) is the authoritative backstop
/// inside this consumer's lag window. The actual <see cref="SuggestionState.Stale"/> write is the guarded, forward-only
/// <see cref="ISuggestionDrift"/> update (Active-only, so idempotent and race-free against the expire sweep).
/// </para>
/// </remarks>
public sealed class SuggestionDriftService
{
    private readonly TradingCopilotDbContext _discovery;
    private readonly IInstrumentSpecSource _specs;
    private readonly IOptions<SuggestionOptions> _options;
    private readonly ISuggestionDrift _drift;
    private readonly ISuggestionRealtimeNotifier _notifier;
    private readonly ILogger<SuggestionDriftService> _logger;

    /// <summary>Creates the service.</summary>
    /// <param name="discovery">The scoped context, used to discover which instruments have Active suggestions.</param>
    /// <param name="specs">The server-side spec source (gh#541) — the tick size the drift band scales by.</param>
    /// <param name="options">The suggestion options, for <see cref="SuggestionOptions.DriftToleranceTicks"/>.</param>
    /// <param name="drift">The guarded Active→Stale writer.</param>
    /// <param name="notifier">The per-owner realtime push seam (gh#684/#718) — best-effort, after the write commits.</param>
    /// <param name="logger">The logger.</param>
    public SuggestionDriftService(
        TradingCopilotDbContext discovery,
        IInstrumentSpecSource specs,
        IOptions<SuggestionOptions> options,
        ISuggestionDrift drift,
        ISuggestionRealtimeNotifier notifier,
        ILogger<SuggestionDriftService> logger)
    {
        _discovery = discovery;
        _specs = specs;
        _options = options;
        _drift = drift;
        _notifier = notifier;
        _logger = logger;
    }

    /// <summary>Marks the Active suggestions this batch of quotes has drifted <see cref="SuggestionState.Stale"/>.</summary>
    /// <param name="quotes">The pass's decoded quotes (only the latest per contract decides the current price).</param>
    /// <param name="venue">The venue used to resolve each Active symbol's front-month contract — created once per pass.</param>
    /// <param name="now">The current time, supplied by the caller — the transition never reads a clock.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>How many suggestions were moved to <see cref="SuggestionState.Stale"/>.</returns>
    public async Task<int> ProcessQuotesAsync(
        IReadOnlyList<StopPromotionService.DecodedQuote> quotes,
        ITradingVenue venue,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(quotes);
        ArgumentNullException.ThrowIfNull(venue);
        if (quotes.Count == 0)
        {
            return 0;
        }

        // The distinct instruments with Active suggestions -- background, so the R-20 filter is bypassed as in the
        // expire sweep. Stored as the venue-neutral symbol; resolved forward to a contract key below.
        List<string> symbols = await _discovery.Suggestions
            .IgnoreQueryFilters()
            .Where(suggestion => suggestion.State == SuggestionState.Active)
            .Select(suggestion => suggestion.Instrument)
            .Distinct()
            .ToListAsync(cancellationToken);
        if (symbols.Count == 0)
        {
            return 0;
        }

        int tolerance = _options.Value.DriftToleranceTicks;
        Dictionary<string, Match> byContract = new(StringComparer.Ordinal);
        foreach (string symbol in symbols)
        {
            // Cannot measure => do NOT transition: no spec / unparseable / venue can't resolve leaves the row Active.
            if (!InstrumentId.TryParse(symbol, out InstrumentId instrument)
                || !_specs.TryResolve(instrument, out InstrumentContractSpec? spec))
            {
                continue;
            }

            string contractKey;
            try
            {
                ResolvedContract resolved = await venue.ResolveContractAsync(instrument, cancellationToken);
                contractKey = resolved.Contract.Key;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception error)
            {
                _logger.LogWarning(
                    error, "Could not resolve the front-month contract for {Symbol}; its suggestions stay Active this pass.", symbol);
                continue;
            }

            if (!byContract.TryAdd(contractKey, new Match(symbol, tolerance * spec.Spec.TickSize)))
            {
                // Two distinct neutral symbols resolving to ONE front-month key is a catalog impossibility for distinct
                // products; keep the first and log rather than silently drop the loser's suggestions if it ever occurs.
                _logger.LogWarning(
                    "Two Active symbols resolved to the same contract {ContractKey}; kept {Kept}, skipped {Skipped}.",
                    contractKey, byContract[contractKey].Symbol, symbol);
            }
        }
        if (byContract.Count == 0)
        {
            return 0;
        }

        // Only the LATEST quote per contract decides the current price; a batch may carry many for one contract.
        Dictionary<string, StopPromotionService.DecodedQuote> latest = new(StringComparer.Ordinal);
        foreach (StopPromotionService.DecodedQuote quote in quotes)
        {
            latest[quote.ContractKey] = quote;
        }

        int stale = 0;
        foreach (StopPromotionService.DecodedQuote quote in latest.Values)
        {
            if (byContract.TryGetValue(quote.ContractKey, out Match match))
            {
                IReadOnlyList<SuggestionTransition> transitioned =
                    await _drift.MarkDriftedStaleAsync(match.Symbol, quote.Bid, quote.Ask, match.Band, now, cancellationToken);
                stale += transitioned.Count;

                // REALTIME PUSH (gh#718): the Stale write has committed, so signal each owning operator that their
                // suggestion greyed out -- per-owner (R-20, Clients.User), best-effort, AFTER the write. A hub fault
                // must never fail or roll back the transition (it already committed inside MarkDriftedStaleAsync); the
                // card surface reconciles against the REST read model regardless.
                foreach (SuggestionTransition transition in transitioned)
                {
                    await NotifySafelyAsync(transition, SuggestionState.Stale, now, cancellationToken);
                }
            }
        }
        return stale;
    }

    private async Task NotifySafelyAsync(
        SuggestionTransition transition, SuggestionState state, DateTimeOffset now, CancellationToken cancellationToken)
    {
        try
        {
            await _notifier.SuggestionChangedAsync(
                transition.UserId,
                new RealtimeSuggestion(transition.SuggestionId, state.ToString(), now),
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            _logger.LogError(
                error,
                "Realtime drift push for suggestion {SuggestionId} failed for owner {Owner}; the transition is committed regardless.",
                transition.SuggestionId, transition.UserId);
        }
    }

    // One resolved instrument: the neutral symbol its suggestions are keyed by, and the price-distance drift band.
    private readonly record struct Match(string Symbol, decimal Band);
}
