using FakeItEasy;
using MarqSpec.TradingCopilot.Api.MarketData;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Data.Tenancy;
using MarqSpec.TradingCopilot.Domain;
using MarqSpec.TradingCopilot.Domain.MarketData;
using MarqSpec.TradingCopilot.Domain.Notifications;
using MarqSpec.TradingCopilot.Domain.Triggers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MarqSpec.TradingCopilot.UnitTests.Api.MarketData;

/// <summary>
/// The trigger scan's core (gh#385). Two quiet failures carry the weight: firing on a value never measured (a
/// null indicator must never fire), and double-paging across a restart (the debounce state is persisted and the
/// alert's dedup key makes a re-send a no-op). It <b>notifies, it never places an order</b>.
/// </summary>
public class TriggerEvaluationServiceTests
{
    private static DateTimeOffset Now => new(2026, 7, 28, 15, 0, 0, TimeSpan.Zero);

    private static readonly Guid _operatorA = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid _operatorB = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");

    private readonly DbContextOptions<TradingCopilotDbContext> _options =
        new DbContextOptionsBuilder<TradingCopilotDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;

    private sealed record FixedUser(Guid UserId) : ICurrentUser;

    private TradingCopilotDbContext Context(Guid user) => new(_options, new FixedUser(user));

    private static IIndicatorSource IndicatorReturning(decimal? value)
    {
        IIndicatorSource source = A.Fake<IIndicatorSource>();
        A.CallTo(() => source.GetValueAsync(
                A<InstrumentId>._, A<string>._, A<int>._, A<int>._, A<DateTimeOffset>._, A<CancellationToken>._))
            .Returns(value);
        return source;
    }

    private static INotificationChannel Channel()
    {
        INotificationChannel channel = A.Fake<INotificationChannel>();
        A.CallTo(() => channel.SendAsync(A<Notification>._, A<CancellationToken>._)).Returns(true);
        A.CallTo(() => channel.ResolveAsync(A<string>._, A<CancellationToken>._)).Returns(true);
        return channel;
    }

    private TriggerEvaluationService Service(IIndicatorSource indicators, INotificationChannel notifications) =>
        new(Context(Guid.Empty), _options, indicators, notifications, NullLogger<TriggerEvaluationService>.Instance);

    private async Task<Guid> SeedTriggerAsync(
        Guid owner,
        TriggerArmState state = TriggerArmState.Armed,
        string instrument = "ES",
        int armCycle = 0,
        bool enabled = true,
        TriggerRoute route = TriggerRoute.MechanicalAlert)
    {
        Guid id = Guid.NewGuid();
        await using TradingCopilotDbContext database = Context(owner);
        database.Triggers.Add(new Trigger
        {
            Id = id,
            UserId = owner,
            Instrument = instrument,
            Indicator = "rsi",
            Period = 14,
            ResolutionMinutes = 1,
            Comparison = ThresholdComparison.Below,
            Threshold = 30m,
            Route = route,
            Enabled = enabled,
            ArmState = state,
            ArmCycle = armCycle,
            CreatedAt = Now,
            UpdatedAt = Now,
        });
        await database.SaveChangesAsync();
        return id;
    }

    private async Task<Trigger> ReadTriggerAsync(Guid owner, Guid id)
    {
        await using TradingCopilotDbContext database = Context(owner);
        return await database.Triggers.SingleAsync(trigger => trigger.Id == id);
    }

    // --- The arming edge fires, once ---

    [Fact]
    public async Task EvaluateAsync_ShouldFireOnce_OnTheArmingEdge()
    {
        Guid id = await SeedTriggerAsync(_operatorA, TriggerArmState.Armed);
        INotificationChannel channel = Channel();

        int fired = await Service(IndicatorReturning(25m), channel).EvaluateAsync(Now, CancellationToken.None);

        fired.Should().Be(1);
        A.CallTo(() => channel.SendAsync(
                A<Notification>.That.Matches(n => n.DedupKey == $"trigger:{id}:0" && n.Severity == NotificationSeverity.Notify),
                A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();

        Trigger stored = await ReadTriggerAsync(_operatorA, id);
        stored.ArmState.Should().Be(TriggerArmState.Fired);
        stored.LastFiredAt.Should().Be(Now);

        await using TradingCopilotDbContext database = Context(_operatorA);
        TriggerFiringRecord firing = await database.TriggerFirings.SingleAsync();
        firing.TriggerId.Should().Be(id);
        firing.Value.Should().Be(25m);
        firing.ArmCycle.Should().Be(0);
    }

    [Fact]
    public async Task EvaluateAsync_ShouldDebounce_WhileTheConditionStaysSatisfied()
    {
        Guid id = await SeedTriggerAsync(_operatorA, TriggerArmState.Armed);
        INotificationChannel channel = Channel();
        TriggerEvaluationService service = Service(IndicatorReturning(25m), channel);

        await service.EvaluateAsync(Now, CancellationToken.None);
        await service.EvaluateAsync(Now.AddMinutes(1), CancellationToken.None);

        A.CallTo(() => channel.SendAsync(A<Notification>._, A<CancellationToken>._)).MustHaveHappenedOnceExactly();

        await using TradingCopilotDbContext database = Context(_operatorA);
        (await database.TriggerFirings.CountAsync()).Should().Be(1);
    }

    // --- Re-arm on the opposite edge ---

    [Fact]
    public async Task EvaluateAsync_ShouldReArmResolveAndBumpTheCycle_OnTheOppositeEdge()
    {
        Guid id = await SeedTriggerAsync(_operatorA, TriggerArmState.Fired, armCycle: 0);
        INotificationChannel channel = Channel();

        // The indicator is now above 30 -> NotSatisfied -> the opposite edge.
        await Service(IndicatorReturning(35m), channel).EvaluateAsync(Now, CancellationToken.None);

        A.CallTo(() => channel.ResolveAsync($"trigger:{id}:0", A<CancellationToken>._)).MustHaveHappenedOnceExactly();
        A.CallTo(() => channel.SendAsync(A<Notification>._, A<CancellationToken>._)).MustNotHaveHappened();

        Trigger stored = await ReadTriggerAsync(_operatorA, id);
        stored.ArmState.Should().Be(TriggerArmState.Armed);
        stored.ArmCycle.Should().Be(1);
    }

    // --- Fail-closed: never fire on absence ---

    [Fact]
    public async Task EvaluateAsync_ShouldNeverFire_WhenTheIndicatorIsUnmeasurable()
    {
        Guid id = await SeedTriggerAsync(_operatorA, TriggerArmState.Armed);
        INotificationChannel channel = Channel();

        await Service(IndicatorReturning(null), channel).EvaluateAsync(Now, CancellationToken.None);

        A.CallTo(() => channel.SendAsync(A<Notification>._, A<CancellationToken>._)).MustNotHaveHappened();
        (await ReadTriggerAsync(_operatorA, id)).ArmState.Should().Be(TriggerArmState.Armed);

        await using TradingCopilotDbContext database = Context(_operatorA);
        (await database.TriggerFirings.AnyAsync()).Should().BeFalse();
    }

    // --- Cold start: seed silently, never fire on pre-existing truth ---

    [Fact]
    public async Task EvaluateAsync_ShouldSeedSilently_WhenTheTriggerWasCreatedWhileAlreadySatisfied()
    {
        Guid id = await SeedTriggerAsync(_operatorA, TriggerArmState.Unseeded);
        INotificationChannel channel = Channel();

        await Service(IndicatorReturning(25m), channel).EvaluateAsync(Now, CancellationToken.None);

        A.CallTo(() => channel.SendAsync(A<Notification>._, A<CancellationToken>._)).MustNotHaveHappened();
        (await ReadTriggerAsync(_operatorA, id)).ArmState.Should().Be(TriggerArmState.Fired);

        await using TradingCopilotDbContext database = Context(_operatorA);
        (await database.TriggerFirings.AnyAsync()).Should().BeFalse();
    }

    // --- R-20: the firing carries the trigger's owner and is invisible to others ---

    [Fact]
    public async Task EvaluateAsync_ShouldStampTheFiringWithTheTriggersOwner()
    {
        await SeedTriggerAsync(_operatorA, TriggerArmState.Armed);

        await Service(IndicatorReturning(25m), Channel()).EvaluateAsync(Now, CancellationToken.None);

        await using (TradingCopilotDbContext asOwner = Context(_operatorA))
        {
            (await asOwner.TriggerFirings.SingleAsync()).UserId.Should().Be(_operatorA);
        }

        // A different operator sees none of it (the default-deny filter).
        await using TradingCopilotDbContext asStranger = Context(_operatorB);
        (await asStranger.TriggerFirings.AnyAsync()).Should().BeFalse();
    }

    // --- Failure containment ---

    [Fact]
    public async Task EvaluateAsync_ShouldContinuePastAThrowingTrigger()
    {
        await SeedTriggerAsync(_operatorA, TriggerArmState.Armed, instrument: "ES");
        await SeedTriggerAsync(_operatorA, TriggerArmState.Armed, instrument: "NQ");

        IIndicatorSource source = A.Fake<IIndicatorSource>();
        A.CallTo(() => source.GetValueAsync(
                A<InstrumentId>.That.Matches(i => i.Symbol == "NQ"), A<string>._, A<int>._, A<int>._, A<DateTimeOffset>._, A<CancellationToken>._))
            .Throws<InvalidOperationException>();
        A.CallTo(() => source.GetValueAsync(
                A<InstrumentId>.That.Matches(i => i.Symbol == "ES"), A<string>._, A<int>._, A<int>._, A<DateTimeOffset>._, A<CancellationToken>._))
            .Returns(25m);
        INotificationChannel channel = Channel();

        Func<Task> act = () => Service(source, channel).EvaluateAsync(Now, CancellationToken.None);

        await act.Should().NotThrowAsync();
        // The ES trigger still fired despite NQ throwing.
        A.CallTo(() => channel.SendAsync(A<Notification>._, A<CancellationToken>._)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task EvaluateAsync_ShouldIgnoreDisabledTriggers()
    {
        await SeedTriggerAsync(_operatorA, TriggerArmState.Armed, enabled: false);
        INotificationChannel channel = Channel();

        int fired = await Service(IndicatorReturning(25m), channel).EvaluateAsync(Now, CancellationToken.None);

        fired.Should().Be(0);
        A.CallTo(() => channel.SendAsync(A<Notification>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task EvaluateAsync_ShouldIgnoreNonMechanicalRoutes()
    {
        // The scan honors Route == MechanicalAlert, so an agent-review trigger never mechanical-alerts even if one
        // were somehow persisted (defence in depth, since the endpoint refuses it at authoring).
        await SeedTriggerAsync(_operatorA, TriggerArmState.Armed, route: TriggerRoute.AgentReview);
        INotificationChannel channel = Channel();

        int fired = await Service(IndicatorReturning(25m), channel).EvaluateAsync(Now, CancellationToken.None);

        fired.Should().Be(0);
        A.CallTo(() => channel.SendAsync(A<Notification>._, A<CancellationToken>._)).MustNotHaveHappened();
    }
}
