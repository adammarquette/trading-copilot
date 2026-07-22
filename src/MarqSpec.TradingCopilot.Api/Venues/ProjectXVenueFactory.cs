using MarqSpec.Client.ProjectX;
using MarqSpec.Client.ProjectX.WebSocket;
using MarqSpec.TradingCopilot.Domain.Venue;
using MarqSpec.TradingCopilot.Integration.ProjectX;
using Microsoft.Extensions.Options;

namespace MarqSpec.TradingCopilot.Api.Venues;

/// <summary>Builds venue-neutral <see cref="ITradingVenue"/> instances for a firm's declared conventions.</summary>
public interface IProjectXVenueFactory
{
    /// <summary>Creates a ProjectX venue scoped to one firm's conventions (R-17, gh#60).</summary>
    /// <param name="conventions">What the firm behind this login has declared each stage to mean.</param>
    /// <returns>The venue.</returns>
    ITradingVenue Create(FirmConventions conventions);
}

/// <summary>
/// The composition seam (ADR-0015: composition-root-agnostic core): a light per-request venue over the
/// process-wide ProjectX client. The client's credentials come from configuration; the conventions come from the
/// persisted firm — so the venue resolves real modes instead of the hard-coded <see cref="FirmConventions.None"/>.
/// </summary>
public sealed class ProjectXVenueFactory : IProjectXVenueFactory
{
    private readonly IProjectXApiClient _api;
    private readonly IProjectXWebSocketClient _webSocket;
    private readonly ProjectXDataTier _dataTier;

    /// <summary>Creates the factory over the process-wide client.</summary>
    /// <param name="api">The gateway's REST client (credentials from configuration).</param>
    /// <param name="webSocket">The gateway's realtime client (singleton — one credential per process).</param>
    /// <param name="options">The app-side ProjectX options carrying the data tier.</param>
    public ProjectXVenueFactory(
        IProjectXApiClient api,
        IProjectXWebSocketClient webSocket,
        IOptions<ProjectXConnectionOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _api = api;
        _webSocket = webSocket;
        _dataTier = options.Value.DataTier;
    }

    /// <inheritdoc />
    public ITradingVenue Create(FirmConventions conventions)
    {
        return new ProjectXVenue(_api, _webSocket, _dataTier, conventions);
    }
}
