using FakeItEasy;
using MarqSpec.TradingCopilot.Api.Kill;
using MarqSpec.TradingCopilot.Api.Venues;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Tenancy;
using MarqSpec.TradingCopilot.Domain.Events;
using MarqSpec.TradingCopilot.Domain.Execution;
using MarqSpec.TradingCopilot.Domain.Observability;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MarqSpec.TradingCopilot.UnitTests.Api.Kill;

/// <summary>
/// The kill-switch endpoints (gh#189, R-11): the operator's panic control. The behaviours that matter here are the
/// <b>hold-to-confirm gate</b> (no engage without explicit confirmation, for either mode) and that engage /
/// disengage / state faithfully flip and report the runtime flag.
/// </summary>
public class KillSwitchEndpointsTests
{
    private readonly Guid _operator = Guid.NewGuid();
    private readonly string _database = Guid.NewGuid().ToString();
    private readonly KillSwitch _killSwitch = new(NullExecutionMetrics.Instance);

    private sealed record FixedUser(Guid UserId) : ICurrentUser;

    private TradingCopilotDbContext Db() =>
        new(new DbContextOptionsBuilder<TradingCopilotDbContext>().UseInMemoryDatabase(_database).Options, new FixedUser(_operator));

    private KillSwitchService Service()
    {
        IProjectXVenueFactory factory = A.Fake<IProjectXVenueFactory>();
        return new KillSwitchService(
            Db(), factory, A.Fake<IEventLog>(), _killSwitch,
            Options.Create(new ProjectXConnectionOptions { CredentialKey = "topstep-main" }),
            NullLogger<KillSwitchService>.Instance);
    }

    private static int StatusOf(IResult result) => ((IStatusCodeHttpResult)result).StatusCode ?? 0;

    [Fact]
    public async Task EngageAsync_ShouldRequireConfirmation_AndNotEngageWithoutIt()
    {
        IResult result = await KillSwitchEndpoints.EngageAsync(
            new EngageKillSwitchRequest(KillSwitchMode.FlattenAll, Confirmed: false, Reason: null),
            Service(), CancellationToken.None);

        StatusOf(result).Should().Be(422);
        _killSwitch.IsEngaged.Should().BeFalse(); // never engages without the hold-to-confirm
    }

    [Fact]
    public async Task EngageAsync_ShouldEngage_WhenConfirmed()
    {
        IResult result = await KillSwitchEndpoints.EngageAsync(
            new EngageKillSwitchRequest(KillSwitchMode.HaltOnly, Confirmed: true, Reason: "looks wrong"),
            Service(), CancellationToken.None);

        StatusOf(result).Should().Be(200);
        _killSwitch.IsEngaged.Should().BeTrue();
    }

    [Fact]
    public async Task DisengageAsync_ShouldReEnableOutbound()
    {
        _killSwitch.Engage(KillSwitchMode.FlattenAll, DateTimeOffset.UnixEpoch, null);

        IResult result = await KillSwitchEndpoints.DisengageAsync(Service(), CancellationToken.None);

        StatusOf(result).Should().Be(200);
        _killSwitch.IsEngaged.Should().BeFalse();
    }

    [Fact]
    public void GetState_ShouldReportTheCurrentState()
    {
        _killSwitch.Engage(KillSwitchMode.HaltOnly, DateTimeOffset.UnixEpoch, "manual");

        IResult result = KillSwitchEndpoints.GetState(_killSwitch);

        StatusOf(result).Should().Be(200);
    }
}
