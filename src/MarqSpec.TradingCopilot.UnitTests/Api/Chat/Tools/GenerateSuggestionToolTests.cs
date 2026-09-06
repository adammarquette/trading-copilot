using System.Text.Json;
using FakeItEasy;
using MarqSpec.TradingCopilot.Api.Chat.Tools;
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
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MarqSpec.TradingCopilot.UnitTests.Api.Chat.Tools;

/// <summary>
/// The <c>generate_suggestion</c> chat write tool (gh#1134 of gh#1059, R-6 / R-4, ADR-0025): the co-pilot
/// <b>proposes</b> a setup, staged for the operator to review.
/// </summary>
/// <remarks>
/// <para>
/// These cases are derived from the card's acceptance criteria, not from the implementation. The four they exist for:
/// (1) the values the model must <b>not</b> get to choose — size, mode and expiry — are the system's on the staged
/// row whatever the model's JSON says; (2) an incoherent or unauthorised proposal <b>stages nothing at all</b>, rather
/// than a partial or a "staged but broken" card; (3) what is staged is a <i>proposal</i> — no order, no disposition,
/// nothing taken; and (4) every malformed path returns an error string the model reads and never throws out of
/// <c>ExecuteAsync</c>.
/// </para>
/// <para>
/// <b>Why the staged row is read back rather than read off the return value.</b> Every assertion about what was
/// written reloads it from a fresh context under the owner's scope. The tool's own JSON reply is what the <i>model</i>
/// is told, and a tool that reported a size it did not persist would satisfy an assertion made against its reply — so
/// the reply is checked separately, and the row is the oracle for the row.
/// </para>
/// </remarks>
public class GenerateSuggestionToolTests
{
    /// <summary>18:00Z on a summer weekday — 13:00 Central, so a Central deadline later that hour is still ahead.</summary>
    private static readonly DateTimeOffset _now = new(2026, 8, 17, 18, 0, 0, TimeSpan.Zero);

    private readonly string _database = Guid.NewGuid().ToString();
    private readonly Guid _owner = Guid.NewGuid();
    private readonly Guid _stranger = Guid.NewGuid();

    private readonly ISessionDeadlineSource _deadlines = A.Fake<ISessionDeadlineSource>();
    private readonly ISuggestionRealtimeNotifier _notifier = A.Fake<ISuggestionRealtimeNotifier>();
    private readonly SuggestionOptions _options = new() { ValidityMinutes = 60, ChatProposalSize = 3 };

    private sealed record FixedUser(Guid UserId) : ICurrentUser;

    private DbContextOptions<TradingCopilotDbContext> DbOptions =>
        new DbContextOptionsBuilder<TradingCopilotDbContext>().UseInMemoryDatabase(_database).Options;

    private TradingCopilotDbContext Context(Guid asUser) => new(DbOptions, new FixedUser(asUser));

    private GenerateSuggestionTool Tool(Guid? asUser = null) => new(
        DbOptions,
        new FixedUser(asUser ?? _owner),
        _deadlines,
        _notifier,
        new FakeTimeProvider(_now),
        Options.Create(_options),
        NullLogger<GenerateSuggestionTool>.Instance);

    /// <summary>A minimal <see cref="TimeProvider"/> pinned to one instant, so issuance and expiry are deterministic.</summary>
    private sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    // A coherent long: stop below entry, target above. The one input shape every "the system decides" case varies from.
    private const string CoherentBuy =
        "{\"instrument\":\"MES\",\"side\":\"Buy\",\"entryPrice\":5000.25,\"stopPrice\":4990.00,"
        + "\"targetPrice\":5020.50,\"rationale\":\"Reclaimed the overnight low on rising delta.\",\"confidence\":72}";

    private static string Input(string extraJson) => CoherentBuy[..^1] + "," + extraJson + "}";

    private async Task<Account> SeedAccountAsync(
        Guid owner,
        string name = "Practice-1",
        TradingMode mode = TradingMode.Practice,
        bool canTrade = true,
        bool isActive = true)
    {
        Account account = new()
        {
            Id = Guid.NewGuid(),
            UserId = owner,
            ConnectionId = Guid.NewGuid(),
            VenueAccountKey = name,
            Name = name,
            Mode = mode,
            CanTrade = canTrade,
            IsActive = isActive,
            IsVisible = true,
        };

        await using TradingCopilotDbContext context = Context(owner);
        context.Accounts.Add(account);
        await context.SaveChangesAsync();
        return account;
    }

    private async Task<IReadOnlyList<Suggestion>> StagedAsync(Guid? forOwner = null)
    {
        await using TradingCopilotDbContext context = Context(forOwner ?? _owner);
        return await context.Suggestions.AsNoTracking().ToListAsync();
    }

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    private static string ErrorIn(string json) =>
        Parse(json).TryGetProperty("error", out JsonElement error) ? error.GetString() ?? string.Empty : string.Empty;

    // =================================================================================================================
    // AC1 — it stages through the production path, and the values that are the system's stay the system's.
    // =================================================================================================================

    [Fact]
    public async Task ExecuteAsync_ShouldStageAnActiveSuggestion_WhenTheProposalIsCoherent()
    {
        Account account = await SeedAccountAsync(_owner);

        string result = await Tool().ExecuteAsync(CoherentBuy, CancellationToken.None);

        Suggestion staged = (await StagedAsync()).Should().ContainSingle(
            "one accepted proposal stages exactly one row").Which;
        staged.UserId.Should().Be(_owner, "the row belongs to the calling operator (R-20)");
        staged.AccountId.Should().Be(account.Id);
        staged.Instrument.Should().Be("MES");
        staged.Side.Should().Be(OrderSide.Buy);
        staged.EntryPrice.Should().Be(5000.25m);
        staged.StopPrice.Should().Be(4990.00m);
        staged.TargetPrice.Should().Be(5020.50m);
        staged.Rationale.Should().Contain("overnight low");
        staged.Confidence.Should().Be(72);
        staged.State.Should().Be(SuggestionState.Active, "a staged proposal is live for the operator to consider");
        staged.CreatedAt.Should().Be(_now);
        staged.TriggerFiringId.Should().BeNull("no trigger fired — chat is not the scan");
        staged.Version.Should().Be(1);
        staged.SupersedesId.Should().BeNull("a chat proposal opens its own chain and supersedes nothing");

        Parse(result).GetProperty("suggestionId").GetGuid().Should().Be(
            staged.Id, "the model is told which row it staged, so its answer can name it");
    }

    [Fact]
    public async Task ExecuteAsync_ShouldSizeFromConfiguration_WhenTheModelTriesToChooseTheSize()
    {
        await SeedAccountAsync(_owner);

        // The schema offers no size property; this is the model sending one anyway, which is the case that matters.
        string result = await Tool().ExecuteAsync(Input("\"size\":50"), CancellationToken.None);

        Suggestion staged = (await StagedAsync()).Should().ContainSingle().Which;
        staged.Size.Should().Be(
            _options.ChatProposalSize,
            "size is the operator's configuration, never the model's — enforcement lives below the model");
        Parse(result).GetProperty("size").GetInt32().Should().Be(_options.ChatProposalSize);
    }

    /// <summary>
    /// Both declared modes, because a Practice-only case cannot fail: an implementation that hard-coded
    /// <see cref="TradingMode.Practice"/> — or read the model's own JSON — would satisfy it. The row's mode must
    /// track the ACCOUNT's in each direction, so the model can neither promote a practice proposal nor quietly
    /// demote a live one. (An in-memory fixture, not a venue: R-14's environment policy is enforced where accounts
    /// are declared, not here.)
    /// </summary>
    [Theory]
    [InlineData(TradingMode.Practice, "Live")]
    [InlineData(TradingMode.Live, "Practice")]
    public async Task ExecuteAsync_ShouldReadTheModeLiveOffTheAccount_WhenTheModelTriesToChooseTheMode(
        TradingMode accountMode, string modeTheModelAsksFor)
    {
        await SeedAccountAsync(_owner, mode: accountMode);

        await Tool().ExecuteAsync(Input("\"mode\":\"" + modeTheModelAsksFor + "\""), CancellationToken.None);

        Suggestion staged = (await StagedAsync()).Should().ContainSingle().Which;
        staged.Mode.Should().Be(
            accountMode, "the mode is read live off the account (R-14); the model's own JSON is not even read");
    }

    [Fact]
    public async Task ExecuteAsync_ShouldExpireOnTheConfiguredWindow_WhenTheMarketHasNoDeadlineToRespect()
    {
        await SeedAccountAsync(_owner);
        A.CallTo(() => _deadlines.DeadlineFor(A<InstrumentId>._)).Returns((TimeOnly?)null);

        await Tool().ExecuteAsync(Input("\"expiresAt\":\"2099-01-01T00:00:00Z\""), CancellationToken.None);

        Suggestion staged = (await StagedAsync()).Should().ContainSingle().Which;
        staged.ExpiresAt.Should().Be(
            _now.AddMinutes(_options.ValidityMinutes),
            "the window is the operator's configuration, and the model's own expiry is not even read");
    }

    [Fact]
    public async Task ExecuteAsync_ShouldClampTheExpiry_WhenTheAutoFlattenDeadlineIsSooner()
    {
        await SeedAccountAsync(_owner);

        // 13:10 Central against a 13:00 Central issuance: ten minutes, well inside the 60-minute configured window.
        A.CallTo(() => _deadlines.DeadlineFor(A<InstrumentId>._)).Returns(new TimeOnly(13, 10));

        await Tool().ExecuteAsync(CoherentBuy, CancellationToken.None);

        Suggestion staged = (await StagedAsync()).Should().ContainSingle().Which;
        staged.ExpiresAt.Should().Be(
            _now.AddMinutes(10),
            "a proposal must never stay live past the moment this market is flattened anyway (R-13)");
    }

    [Fact]
    public async Task ExecuteAsync_ShouldStageAgainstTheNamedAccount_WhenSeveralAreTradable()
    {
        await SeedAccountAsync(_owner, "Practice-1");
        Account second = await SeedAccountAsync(_owner, "Practice-2");

        await Tool().ExecuteAsync(Input("\"account\":\"practice-2\""), CancellationToken.None);

        (await StagedAsync()).Should().ContainSingle().Which.AccountId.Should().Be(
            second.Id, "the operator's named account is matched case-insensitively");
    }

    // =================================================================================================================
    // AC1 (the other half) — an invalid proposal FAILS CLOSED: nothing is staged at all.
    // =================================================================================================================

    /// <summary>Proposals whose geometry no coherent setup could have — each must stage nothing.</summary>
    public static TheoryData<string, string> IncoherentGeometry() => new()
    {
        { "stop above entry on a long", "{\"instrument\":\"MES\",\"side\":\"Buy\",\"entryPrice\":5000,\"stopPrice\":5010,\"targetPrice\":5020,\"rationale\":\"x\",\"confidence\":50}" },
        { "target below entry on a long", "{\"instrument\":\"MES\",\"side\":\"Buy\",\"entryPrice\":5000,\"stopPrice\":4990,\"targetPrice\":4980,\"rationale\":\"x\",\"confidence\":50}" },
        { "stop below entry on a short", "{\"instrument\":\"MES\",\"side\":\"Sell\",\"entryPrice\":5000,\"stopPrice\":4990,\"targetPrice\":4980,\"rationale\":\"x\",\"confidence\":50}" },
        { "a non-positive price", "{\"instrument\":\"MES\",\"side\":\"Buy\",\"entryPrice\":0,\"stopPrice\":-1,\"targetPrice\":5020,\"rationale\":\"x\",\"confidence\":50}" },
    };

    [Theory]
    [MemberData(nameof(IncoherentGeometry))]
    public async Task ExecuteAsync_ShouldStageNothing_WhenTheGeometryIsIncoherent(string reason, string inputJson)
    {
        await SeedAccountAsync(_owner);

        string result = await Tool().ExecuteAsync(inputJson, CancellationToken.None);

        (await StagedAsync()).Should().BeEmpty(
            "an incoherent proposal ({0}) fails closed — no partial row, and no broken card for the operator", reason);
        ErrorIn(result).Should().NotBeEmpty("the model is told why, so it can correct itself or apologise");
        A.CallTo(() => _notifier.SuggestionChangedAsync(A<Guid>._, A<RealtimeSuggestion>._, A<CancellationToken>._))
            .MustNotHaveHappened();
    }

    [Theory]
    [InlineData(TradingMode.Undeclared, true, true, "an undeclared account is refused everywhere, production included")]
    [InlineData(TradingMode.Practice, false, true, "an account the venue will not trade cannot carry a proposal")]
    [InlineData(TradingMode.Practice, true, false, "a deactivated account is not a place to stage new exposure")]
    public async Task ExecuteAsync_ShouldStageNothing_WhenNoAccountIsTradable(
        TradingMode mode, bool canTrade, bool isActive, string because)
    {
        await SeedAccountAsync(_owner, mode: mode, canTrade: canTrade, isActive: isActive);

        string result = await Tool().ExecuteAsync(CoherentBuy, CancellationToken.None);

        (await StagedAsync()).Should().BeEmpty(because);
        ErrorIn(result).Should().NotBeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_ShouldStageNothing_AndNameTheChoices_WhenSeveralAccountsAreTradableAndNoneIsNamed()
    {
        await SeedAccountAsync(_owner, "Practice-1");
        await SeedAccountAsync(_owner, "Practice-2");

        string result = await Tool().ExecuteAsync(CoherentBuy, CancellationToken.None);

        (await StagedAsync()).Should().BeEmpty(
            "which account a setup is proposed against is which money is at risk — guessing is exactly the choice "
            + "that must not silently become the model's");
        ErrorIn(result).Should().Contain("Practice-1").And.Contain("Practice-2");
    }

    [Fact]
    public async Task ExecuteAsync_ShouldStageNothing_WhenTheNamedAccountIsNotTheOperatorsOwn()
    {
        await SeedAccountAsync(_stranger, "Someone-Elses");

        string result = await Tool().ExecuteAsync(Input("\"account\":\"Someone-Elses\""), CancellationToken.None);

        (await StagedAsync(_owner)).Should().BeEmpty("the caller has no tradable account of their own");
        (await StagedAsync(_stranger)).Should().BeEmpty(
            "and R-20 means another operator's account is not merely refused — it is invisible, so nothing could be "
            + "staged onto it either");
        ErrorIn(result).Should().NotBeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_ShouldStageNothing_AndNotThrow_WhenADependencyFaults()
    {
        await SeedAccountAsync(_owner);
        A.CallTo(() => _deadlines.DeadlineFor(A<InstrumentId>._)).Throws(new InvalidOperationException("boom"));

        string result = await Tool().ExecuteAsync(CoherentBuy, CancellationToken.None);

        (await StagedAsync()).Should().BeEmpty("a fault mid-stage leaves nothing written");
        ErrorIn(result).Should().NotBeEmpty("the turn recovers with an error the model reads, never a throw");
    }

    // =================================================================================================================
    // AC2 — staged and surfaced, NEVER auto-taken.
    // =================================================================================================================

    [Fact]
    public async Task ExecuteAsync_ShouldStageAProposalOnly_AndWriteNoOrderOrDisposition()
    {
        await SeedAccountAsync(_owner);

        string result = await Tool().ExecuteAsync(CoherentBuy, CancellationToken.None);

        await using TradingCopilotDbContext reload = Context(_owner);
        (await reload.Orders.CountAsync()).Should().Be(
            0, "a proposal is not an execution — the chat path writes no Order row");
        (await reload.SuggestionDispositions.CountAsync()).Should().Be(
            0, "nor is it taken: only the operator's own take disposes a suggestion, and the risk gate runs then");
        (await reload.Suggestions.SingleAsync()).State.Should().Be(SuggestionState.Active);

        JsonElement reply = Parse(result);
        reply.GetProperty("state").GetString().Should().Be("Active");
        reply.GetProperty("staged").GetString().Should().Contain(
            "NOT been taken", "the model is told plainly, so it cannot report a trade it did not place");
    }

    [Fact]
    public async Task ExecuteAsync_ShouldPushTheStagedCardToItsOwner()
    {
        await SeedAccountAsync(_owner);

        await Tool().ExecuteAsync(CoherentBuy, CancellationToken.None);

        Suggestion staged = (await StagedAsync()).Should().ContainSingle().Which;
        A.CallTo(() => _notifier.SuggestionChangedAsync(
                _owner,
                A<RealtimeSuggestion>.That.Matches(push =>
                    push.SuggestionId == staged.Id && push.State == nameof(SuggestionState.Active)),
                A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task ExecuteAsync_ShouldStillStageTheProposal_WhenTheRealtimePushFails()
    {
        await SeedAccountAsync(_owner);
        A.CallTo(() => _notifier.SuggestionChangedAsync(A<Guid>._, A<RealtimeSuggestion>._, A<CancellationToken>._))
            .Throws(new InvalidOperationException("hub down"));

        string result = await Tool().ExecuteAsync(CoherentBuy, CancellationToken.None);

        (await StagedAsync()).Should().ContainSingle(
            "the push is presentation-only (ADR-0021) — a hub fault must never unwind a durable proposal");
        ErrorIn(result).Should().BeEmpty();
    }

    // =================================================================================================================
    // AC5 — malformed arguments fail closed with an error result, never a throw.
    // =================================================================================================================

    /// <summary>Every shape of input a model can produce that this tool must refuse without throwing.</summary>
    public static TheoryData<string, string> MalformedInput() => new()
    {
        { "not JSON at all", "not json" },
        { "an empty string", "" },
        { "a JSON array rather than an object", "[1,2,3]" },
        { "no instrument", "{\"side\":\"Buy\",\"entryPrice\":5000,\"stopPrice\":4990,\"targetPrice\":5020,\"rationale\":\"x\",\"confidence\":50}" },
        { "a blank instrument", "{\"instrument\":\"   \",\"side\":\"Buy\",\"entryPrice\":5000,\"stopPrice\":4990,\"targetPrice\":5020,\"rationale\":\"x\",\"confidence\":50}" },
        { "a side that is neither Buy nor Sell", "{\"instrument\":\"MES\",\"side\":\"Hedge\",\"entryPrice\":5000,\"stopPrice\":4990,\"targetPrice\":5020,\"rationale\":\"x\",\"confidence\":50}" },
        { "a price sent as a string", "{\"instrument\":\"MES\",\"side\":\"Buy\",\"entryPrice\":\"5000\",\"stopPrice\":4990,\"targetPrice\":5020,\"rationale\":\"x\",\"confidence\":50}" },
        { "a missing rationale", "{\"instrument\":\"MES\",\"side\":\"Buy\",\"entryPrice\":5000,\"stopPrice\":4990,\"targetPrice\":5020,\"confidence\":50}" },
        { "confidence above 100", "{\"instrument\":\"MES\",\"side\":\"Buy\",\"entryPrice\":5000,\"stopPrice\":4990,\"targetPrice\":5020,\"rationale\":\"x\",\"confidence\":150}" },
        { "confidence sent as a string", "{\"instrument\":\"MES\",\"side\":\"Buy\",\"entryPrice\":5000,\"stopPrice\":4990,\"targetPrice\":5020,\"rationale\":\"x\",\"confidence\":\"high\"}" },
    };

    [Theory]
    [MemberData(nameof(MalformedInput))]
    public async Task ExecuteAsync_ShouldReturnAnErrorAndStageNothing_WhenTheInputIsMalformed(
        string reason, string inputJson)
    {
        await SeedAccountAsync(_owner);

        string result = await Tool().ExecuteAsync(inputJson, CancellationToken.None);

        ErrorIn(result).Should().NotBeEmpty("{0} must come back as a tool error the model reads", reason);
        (await StagedAsync()).Should().BeEmpty("and nothing is written on the way to refusing it");
    }

    [Fact]
    public async Task ExecuteAsync_ShouldRefuseARationaleLongerThanThePersistedColumn()
    {
        await SeedAccountAsync(_owner);

        string overLong = "{\"instrument\":\"MES\",\"side\":\"Buy\",\"entryPrice\":5000,\"stopPrice\":4990,"
            + "\"targetPrice\":5020,\"confidence\":50,\"rationale\":\"" + new string('x', 2001) + "\"}";

        string result = await Tool().ExecuteAsync(overLong, CancellationToken.None);

        ErrorIn(result).Should().NotBeEmpty(
            "an over-long rationale is REFUSED, never truncated — a truncated rationale is a different claim from the "
            + "one the model made");
        (await StagedAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_ShouldNotSwallowTheCallersCancellation()
    {
        await SeedAccountAsync(_owner);
        using CancellationTokenSource cancelled = new();
        await cancelled.CancelAsync();

        Func<Task> execute = () => Tool().ExecuteAsync(CoherentBuy, cancelled.Token);

        await execute.Should().ThrowAsync<OperationCanceledException>(
            "a genuine caller cancellation is not a fault to swallow into a tool error");
    }

    // =================================================================================================================
    // The offered contract — what the model is invited to decide is itself part of the boundary.
    // =================================================================================================================

    [Fact]
    public void Name_ShouldBeTheStableToolId() => Tool().Name.Should().Be("generate_suggestion");

    [Theory]
    [InlineData("size")]
    [InlineData("mode")]
    [InlineData("expiresAt")]
    [InlineData("accountId")]
    public void Definition_ShouldNotOfferTheModelAPropertyTheSystemOwns(string property)
    {
        LlmToolDefinition definition = Tool().Definition;

        definition.Name.Should().Be("generate_suggestion");
        definition.InputSchema.Should().NotContain(
            "\"" + property + "\"",
            "the schema is the first place the model learns what it may decide — offering {0} would invite it to "
            + "choose something enforcement below the model owns",
            property);
    }

    [Fact]
    public void Definition_ShouldTellTheModelTheProposalIsStagedNotTaken()
    {
        Tool().Definition.Description.Should().ContainAny(
            "never taken", "not taken", "STAGED", "staged");
        Tool().Definition.InputSchema.Should().Contain("instrument").And.Contain("stopPrice");
    }
}
