using System.Globalization;
using MarqSpec.TradingCopilot.Domain;
using MarqSpec.TradingCopilot.Domain.Venue;
using ClientModels = MarqSpec.Client.Tradovate.Api.Models;

namespace MarqSpec.TradingCopilot.Integration.Tradovate;

/// <summary>
/// Translates between the Tradovate client's vocabulary and the venue-neutral model. Everything venue-specific about
/// Tradovate — integer account / contract ids, an <b>already-signed</b> net position, and demo-vs-live as the mode
/// source (a brokerage, gh#780) — is absorbed here so none of it reaches the core (R-17).
/// </summary>
public static class TradovateMapping
{
    /// <summary>Maps a Tradovate contract onto the venue-neutral resolved contract, paired with its instrument.</summary>
    /// <param name="contract">The Tradovate contract.</param>
    /// <param name="instrument">The instrument it was resolved for.</param>
    /// <param name="venue">The venue to tag the handle with.</param>
    /// <returns>The handle paired with its instrument.</returns>
    /// <exception cref="TradovateVenueException">The contract has no id.</exception>
    /// <remarks>
    /// The pairing (handle + instrument) is the venue's to make — carrying it forward is what lets the execution path
    /// catch a proposal sized for one instrument sent as another's contract (<see cref="ResolvedContract"/>).
    /// </remarks>
    public static ResolvedContract ToResolvedContract(
        ClientModels.Contract contract,
        InstrumentId instrument,
        VenueId venue)
    {
        ArgumentNullException.ThrowIfNull(contract);

        return contract.Id is { } id
            ? new ResolvedContract(VenueContractId.Create(venue, id.ToString(CultureInfo.InvariantCulture)), instrument)
            : throw new TradovateVenueException($"Tradovate returned a contract for '{instrument}' with no id.");
    }

    /// <summary>Maps a Tradovate position onto the venue-neutral snapshot, keeping the sign.</summary>
    /// <param name="position">The Tradovate position.</param>
    /// <param name="venue">The venue to tag it with.</param>
    /// <returns>The snapshot, with a signed net quantity.</returns>
    /// <remarks>
    /// Tradovate's <c>netPos</c> is <b>already signed</b> (long positive, short negative) — unlike ProjectX's unsigned
    /// size plus a separate direction — so it maps straight through with no chance of an inverted exposure. A flat
    /// position's price is immaterial; a held one carries <c>netPrice</c>.
    /// </remarks>
    public static PositionSnapshot ToPositionSnapshot(ClientModels.Position position, VenueId venue)
    {
        ArgumentNullException.ThrowIfNull(position);

        return new PositionSnapshot(
            VenueAccountId.Create(venue, position.AccountId.ToString(CultureInfo.InvariantCulture)),
            VenueContractId.Create(venue, position.ContractId.ToString(CultureInfo.InvariantCulture)),
            position.NetPos,
            new Price(position.NetPrice ?? 0m));
    }

    /// <summary>Maps a Tradovate account, with its cash balance, onto the venue-neutral account.</summary>
    /// <param name="account">The Tradovate account.</param>
    /// <param name="balance">The account's cash balance, read separately and joined by the caller.</param>
    /// <param name="venue">The venue to tag it with.</param>
    /// <param name="conventions">The firm's conventions — for Tradovate, a brokerage's (gh#780).</param>
    /// <param name="venueReportsSimulated">Whether the venue's own host is demo/paper; <see langword="null"/> if unknown.</param>
    /// <returns>The venue-neutral account.</returns>
    /// <exception cref="TradovateVenueException">The account has no id.</exception>
    /// <remarks>
    /// <see cref="TradingMode"/> is resolved through <paramref name="conventions"/>. Tradovate is a brokerage
    /// (gh#780), so its conventions are <see cref="FirmConventions.ForBrokerage"/> and mode follows the venue's own
    /// host: <paramref name="venueReportsSimulated"/> is <see langword="true"/> on a demo host, <see langword="false"/>
    /// on a live host, and <see langword="null"/> on an unrecognised one — which resolves to
    /// <see cref="TradingMode.Undeclared"/> (tradeable nowhere), never a defaulted mode. The stage is always
    /// <see cref="AccountStage.Unknown"/>: a brokerage has no evaluation / funded ladder encoded in a name.
    /// </remarks>
    public static VenueAccount ToVenueAccount(
        ClientModels.Account account,
        decimal balance,
        VenueId venue,
        FirmConventions conventions,
        bool? venueReportsSimulated)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(conventions);

        return account.Id is { } id
            ? new VenueAccount(
                VenueAccountId.Create(venue, id.ToString(CultureInfo.InvariantCulture)),
                account.Name,
                balance,
                // Active and not read-only: Tradovate marks a view-only account Readonly, which cannot be traded.
                CanTrade: account.Active && account.Readonly != true,
                // Tradovate carries no per-account visibility flag; visibility is the operator's local toggle, so a
                // discovered account starts visible and the operator hides what they do not want to see.
                IsVisible: true,
                conventions.ModeFor(AccountStage.Unknown, venueReportsSimulated))
            {
                Stage = AccountStage.Unknown,
                VenueReportsSimulated = venueReportsSimulated ?? false,
            }
            : throw new TradovateVenueException($"Tradovate returned account '{account.Name}' with no id.");
    }

    /// <summary>Classifies the configured host as Tradovate's demo (paper) host, live host, or neither.</summary>
    /// <param name="configuredHost">The client's configured REST host.</param>
    /// <returns>
    /// <see langword="true"/> for the demo host, <see langword="false"/> for the live host, and <see langword="null"/>
    /// for any unrecognised host — which resolves to <see cref="TradingMode.Undeclared"/>, so an unexpected host fails
    /// closed (tradeable nowhere) rather than defaulting an account to live- or practice-tradeable.
    /// </returns>
    public static bool? IsSimulatedHost(string configuredHost)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configuredHost);

        if (configuredHost.Contains("demo.tradovateapi.com", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (configuredHost.Contains("live.tradovateapi.com", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return null;
    }

    /// <summary>Parses a venue-qualified account handle back into the integer id Tradovate expects.</summary>
    /// <param name="account">The venue-qualified account.</param>
    /// <param name="venue">This adapter's venue — the account must belong to it.</param>
    /// <returns>Tradovate's account id.</returns>
    /// <exception cref="ArgumentException">The account belongs to another venue, or its key is not a Tradovate id.</exception>
    /// <remarks>
    /// The qualifier is checked, not assumed: account handles are bare integers that collide freely across venues, so
    /// a <c>projectx:9001</c> reaching this adapter must never be sent to <i>Tradovate</i> account 9001 (R-17).
    /// </remarks>
    public static long ToAccountId(VenueAccountId account, VenueId venue)
    {
        EnsureBelongsTo(account.Venue, venue, account.ToString());

        return long.TryParse(account.Key, NumberStyles.Integer, CultureInfo.InvariantCulture, out long id)
            ? id
            : throw new ArgumentException($"'{account.Key}' is not a Tradovate account id.", nameof(account));
    }

    private static void EnsureBelongsTo(VenueId actual, VenueId expected, string qualified)
    {
        if (actual != expected)
        {
            throw new ArgumentException(
                $"'{qualified}' belongs to venue '{actual}', not '{expected}'.", nameof(qualified));
        }
    }
}
