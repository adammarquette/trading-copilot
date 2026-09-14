using MarqSpec.TradingCopilot.Api.Observability;
using MarqSpec.TradingCopilot.Api.Venues;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Domain.Execution;
using MarqSpec.TradingCopilot.Domain.Venue;
using MarqSpec.TradingCopilot.IntegrationTests.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MarqSpec.TradingCopilot.IntegrationTests.Observability;

/// <summary>
/// Integration coverage for the <b>protection census</b> (gh#533 ⇒ gh#370, R-20, ADR-0008) — the gauges behind the
/// <b>P1 <c>UnprotectedPosition</c></b> alert.
/// </summary>
/// <remarks>
/// <para>
/// That rule evaluates <c>trading_positions_unprotected &gt; 0</c> — the Prometheus rendering of
/// <see cref="ExecutionMetrics.UnprotectedPositions"/> — and it is the alert that says <i>the venue reports open
/// exposure with no protective stop behind it</i>. Until this suite, <b>nothing read those gauges back at any
/// tier</b>: `ExecutionMetricsTests` has no case for `SetPositionProtection` at all, so transposing the two
/// `Interlocked.Exchange` targets would leave every existing test green while the P1 fired on the wrong series.
/// </para>
/// <para>
/// The gauges are <b>observable</b>, so they report only when the listener samples them — hence the explicit
/// <c>PumpGauges()</c> before every read. And the fixture swaps the <b>concrete</b> <see cref="ExecutionMetrics"/>
/// sink rather than re-registering <c>IExecutionMetrics</c>: replacing the interface descriptor would delete the
/// shipped <c>FailureTolerantExecutionMetrics</c> decorator and test a composition production does not have.
/// </para>
/// </remarks>
public class ProtectionCensusIntegrationTests : IClassFixture<ExecutionSliTestPostgresFactory>
{
    private readonly ExecutionSliTestPostgresFactory _factory;

    public ProtectionCensusIntegrationTests(ExecutionSliTestPostgresFactory factory)
    {
        _factory = factory;
    }

    private const string ContractKey = "CON.F.US.MES.U26";
    private const string FirstAccount = "PRAC-50K-101";
    private const string SecondAccount = "50KTC-V2-202";

    [Fact]
    public async Task Census_ShouldPublishTheUnprotectedCount_ThroughTheShippedMetricsChain()
    {
        Guid operatorId = await OperatorIdAsync();
        await SeedAccountsAsync(operatorId, FirstAccount);
        VenueFactory.SeedPosition(FirstAccount, ContractKey, netQuantity: 2);
        // No working order seeded: the position rests at the venue with nothing protecting it. This is the exact
        // state the P1 exists to catch.

        await ObserveAsync();

        // Asserted by the LITERAL instrument name the alert rule resolves to, not by a constant compared with
        // itself. A rename that left the rule pointing at a series nobody publishes would show up here.
        LatestValue(ExecutionMetrics.UnprotectedPositions).Should().Be(1,
            "one open position with no protective stop is one unprotected position — this is the number "
            + "trading_positions_unprotected > 0 evaluates");
        LatestValue(ExecutionMetrics.OpenPositions).Should().Be(1,
            "and it is counted as open too — the two gauges must not be transposed");
    }

    [Fact]
    public async Task Census_ShouldCountAProtectedPosition_AsOpenButNotUnprotected()
    {
        // The control for the case above. Without it, a census that reported everything as unprotected — or that
        // wrote the open count into both gauges — would satisfy it perfectly.
        Guid operatorId = await OperatorIdAsync();
        await SeedAccountsAsync(operatorId, FirstAccount);
        VenueFactory.SeedPosition(FirstAccount, ContractKey, netQuantity: 2);
        VenueFactory.SeedWorkingOrder(FirstAccount, "STOP-1", ContractKey);

        await ObserveAsync();

        LatestValue(ExecutionMetrics.OpenPositions).Should().Be(1);
        LatestValue(ExecutionMetrics.UnprotectedPositions).Should().Be(0,
            "a stop resting at the venue is what protection MEANS — the alert must stay silent");
    }

    [Fact]
    public async Task Census_ShouldPublishNothingAtAll_WhenOneOfSeveralAccountsCannotBeRead()
    {
        // The partial-census failure mode, and the one the unit tier cannot reach: it seeds a single account, so
        // "one of several unreachable" has never been exercised.
        //
        // A total that silently omits an unreadable account is indistinguishable from one where that account is
        // flat — and it errs toward FEWER unprotected positions, which is the direction that quietens a P1. So the
        // whole pass must be abandoned, leaving the previous value standing rather than publishing a smaller one.
        Guid operatorId = await OperatorIdAsync();
        await SeedAccountsAsync(operatorId, FirstAccount, SecondAccount);
        VenueFactory.SeedPosition(FirstAccount, ContractKey, netQuantity: 2);
        VenueFactory.SeedPosition(SecondAccount, ContractKey, netQuantity: 2);

        // Establish a known published value first, so "published nothing" is distinguishable from "never ran".
        await ObserveAsync();
        LatestValue(ExecutionMetrics.UnprotectedPositions).Should().Be(2, "the positive control for this case");

        VenueFactory.MakeAccountUnreadable(SecondAccount);
        _factory.Capture.Clear();
        await ObserveAsync();

        _factory.Capture.For(ExecutionMetrics.UnprotectedPositions)
            .Should().OnlyContain(measurement => measurement.Value == 2,
                "the pass is abandoned, so the gauge still carries the LAST COMPLETE census. A 1 here would be a "
                + "partial count published as a whole one — the unprotected position on the unreadable account "
                + "silently dropped, and the P1 quietened by exactly the thing it should be reporting");
    }

    // NOT COVERED HERE: that the shipped ProtectionMonitorHost actually drives this. It swallows every exception
    // into a LogWarning, so an unregistered or first-pass-throwing host is invisible — the gauges never move and
    // the P1 never fires. It cannot be asserted on THIS fixture, which strips every IHostedService for
    // determinism: the descriptor is removed after production registers it, so a "the host is registered" check
    // here would be testing the harness rather than the composition root. It needs a non-stripping fixture. See
    // the PR; the same structural split applies to gh#557's live-host cases.

    // ---------------------------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------------------------

    private AdversarialTestProjectXVenueFactory VenueFactory =>
        _factory.Services.GetRequiredService<AdversarialTestProjectXVenueFactory>();

    /// <summary>Runs one census pass through the real service and samples the observable gauges.</summary>
    private async Task ObserveAsync()
    {
        await using (AsyncServiceScope scope = _factory.Services.CreateAsyncScope())
        {
            ProtectionMonitorService monitor = scope.ServiceProvider.GetRequiredService<ProtectionMonitorService>();
            IProjectXVenueFactory venues = scope.ServiceProvider.GetRequiredService<IProjectXVenueFactory>();
            await monitor.ObserveAsync(venues.Create(FirmConventions.None), CancellationToken.None);
        }

        // Observable gauges report only when sampled; without this the capture is empty and every assertion above
        // would be comparing against nothing.
        _factory.Capture.PumpGauges();
    }

    private long LatestValue(string instrument) =>
        (long)_factory.Capture.For(instrument).Last().Value;

    private async Task SeedAccountsAsync(Guid operatorId, params string[] accountKeys)
    {
        VenueFactory.ResetPositions();
        _factory.Capture.Clear();

        Guid firmId = Guid.NewGuid();
        Guid connectionId = Guid.NewGuid();
        await ExecuteDbAsync(async db =>
        {
            await db.Accounts.IgnoreQueryFilters().ExecuteDeleteAsync();
            await db.Connections.IgnoreQueryFilters().ExecuteDeleteAsync();
            await db.Firms.IgnoreQueryFilters().ExecuteDeleteAsync();

            db.Firms.Add(new Firm { Id = firmId, UserId = operatorId, Name = "Topstep", Type = FirmType.PropFirm });
            db.Connections.Add(new Connection
            {
                Id = connectionId,
                UserId = operatorId,
                FirmId = firmId,
                Platform = "projectx",
                CredentialKey = "topstep-main",
            });

            foreach (string accountKey in accountKeys)
            {
                db.Accounts.Add(new Account
                {
                    Id = Guid.NewGuid(),
                    UserId = operatorId,
                    ConnectionId = connectionId,
                    VenueAccountKey = accountKey,
                    Name = accountKey,
                    Stage = AccountStage.Practice,
                    Mode = TradingMode.Practice,
                });
            }

            await db.SaveChangesAsync();
        });
    }

    private Task<Guid> OperatorIdAsync() =>
        QueryDbAsync(async db => (await db.Users.FirstAsync(user => user.Email == PostgresApiFactory.OperatorEmail)).Id);

    private async Task ExecuteDbAsync(Func<TradingCopilotDbContext, Task> action)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        TradingCopilotDbContext database = scope.ServiceProvider.GetRequiredService<TradingCopilotDbContext>();
        await action(database);
    }

    private async Task<T> QueryDbAsync<T>(Func<TradingCopilotDbContext, Task<T>> query)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        TradingCopilotDbContext database = scope.ServiceProvider.GetRequiredService<TradingCopilotDbContext>();
        return await query(database);
    }
}
