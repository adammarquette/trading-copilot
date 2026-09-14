namespace MarqSpec.TradingCopilot.Domain.Venue;

/// <summary>
/// A full trading venue — market data, accounts, and execution together (R-17). ProjectX/TopstepX is the v1
/// implementation; a second venue is a new adapter behind this same interface, not a rewrite.
/// </summary>
/// <remarks>
/// The decomposition is the point: depend on the narrowest slice that does the job. A charting surface takes
/// <see cref="IMarketDataSource"/>, the risk layer takes <see cref="IAccountSource"/>, and only the execution
/// path takes <see cref="IOrderExecutor"/>.
/// </remarks>
public interface ITradingVenue : IMarketDataSource, IAccountSource, IOrderExecutor
{
}
