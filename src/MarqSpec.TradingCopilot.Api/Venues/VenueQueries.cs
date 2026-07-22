using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Domain.Venue;
using Microsoft.EntityFrameworkCore;

namespace MarqSpec.TradingCopilot.Api.Venues;

/// <summary>Shared venue-side queries for the connection/account endpoints.</summary>
internal static class VenueQueries
{
    /// <summary>
    /// Resolves the declared <see cref="FirmConventions"/> governing a connection — connection → firm →
    /// declarations. Fail-closed: a broken chain (deleted rows mid-request) yields
    /// <see cref="FirmConventions.None"/>, under which every stage resolves <c>Undeclared</c> — refused
    /// everywhere, never guessed.
    /// </summary>
    /// <param name="database">The scoped database.</param>
    /// <param name="connectionId">The connection whose firm's conventions govern.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>The firm's declared conventions, or <see cref="FirmConventions.None"/>.</returns>
    public static async Task<FirmConventions> ConventionsForConnectionAsync(
        this TradingCopilotDbContext database,
        Guid connectionId,
        CancellationToken cancellationToken)
    {
        Connection? connection = await database.Connections
            .FirstOrDefaultAsync(candidate => candidate.Id == connectionId, cancellationToken);
        if (connection is null)
        {
            return FirmConventions.None;
        }

        Firm? firm = await database.Firms
            .Include(candidate => candidate.StageConventions)
            .FirstOrDefaultAsync(candidate => candidate.Id == connection.FirmId, cancellationToken);

        return firm?.ToConventions() ?? FirmConventions.None;
    }
}
