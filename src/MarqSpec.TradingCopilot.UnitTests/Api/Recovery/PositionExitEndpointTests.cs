using FakeItEasy;
using MarqSpec.TradingCopilot.Api.Recovery;
using MarqSpec.TradingCopilot.Api.Venues;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Tenancy;
using MarqSpec.TradingCopilot.Domain.Venue;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MarqSpec.TradingCopilot.UnitTests.Api.Recovery;

/// <summary>
/// The exit route's HTTP contract (gh#656) — how each outcome reaches the operator.
/// </summary>
/// <remarks>
/// The distinction that matters: <b>only a verified flat is a 200 success</b>. A close the venue accepted while
/// still reporting exposure, and a venue that could not be reached, must never present as "done" — the operator
/// would stop watching a position that is still live.
/// </remarks>
public class PositionExitEndpointTests
{
    private static int StatusOf(IResult result) => ((IStatusCodeHttpResult)result).StatusCode ?? 0;

    private readonly Guid _operator = Guid.NewGuid();
    private readonly string _database = Guid.NewGuid().ToString();
    private readonly IProjectXVenueFactory _factory = A.Fake<IProjectXVenueFactory>();

    private sealed record FixedUser(Guid UserId) : ICurrentUser;

    private TradingCopilotDbContext Context() =>
        new(new DbContextOptionsBuilder<TradingCopilotDbContext>().UseInMemoryDatabase(_database).Options,
            new FixedUser(_operator));

    private readonly IPositionActionJournal _journal = A.Fake<IPositionActionJournal>();

    private PositionExitService Service() =>
        new(Context(), _factory, Options.Create(new ProjectXConnectionOptions { CredentialKey = "topstep-main" }),
            _journal, NullLogger<PositionExitService>.Instance);

    [Fact]
    public async Task ExitAsync_ShouldReturnNotFound_WhenTheAccountIsNotTheCallers()
    {
        // Nothing seeded, so the account does not exist for this caller (R-20).
        IResult result = await PositionReconciliationEndpoints.ExitAsync(
            Guid.NewGuid(), "MES", Service(), CancellationToken.None);

        StatusOf(result).Should().Be(StatusCodes.Status404NotFound);
    }

    [Fact]
    public async Task ExitAsync_ShouldReturnBadRequest_WhenTheInstrumentIsNotParseable()
    {
        // A client mistake, and it must be told apart from "the venue could not be reached" -- retrying a typo
        // forever is not the same problem as retrying an outage.
        IResult result = await PositionReconciliationEndpoints.ExitAsync(
            Guid.NewGuid(), "   ", Service(), CancellationToken.None);

        StatusOf(result).Should().Be(StatusCodes.Status400BadRequest);
    }
}
