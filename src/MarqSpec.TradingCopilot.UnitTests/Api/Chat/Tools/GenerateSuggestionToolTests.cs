using System.Text.Json;
using FakeItEasy;
using MarqSpec.TradingCopilot.Api.Chat.Tools;
using MarqSpec.TradingCopilot.Api.Realtime;
using MarqSpec.TradingCopilot.Api.Suggestions;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Data.Tenancy;
using MarqSpec.TradingCopilot.Domain;
using MarqSpec.TradingCopilot.Domain.Flatten;
using MarqSpec.TradingCopilot.Domain.Suggestions;
using MarqSpec.TradingCopilot.Domain.Triggers;
using MarqSpec.TradingCopilot.Domain.Venue;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MarqSpec.TradingCopilot.UnitTests.Api.Chat.Tools;

/// <summary>
/// The <c>generate_suggestion</c> chat write tool (gh#1059, R-4 / R-6, ADR-0029): the co-pilot <b>proposes</b> a
/// setup, staged through the system's own validation so geometry, size, mode and expiry are never the model's.
/// </summary>
/// <remarks>
/// These pin the three things a review of a write tool actually has to trust: that an invalid proposal writes
/// <b>nothing</b> (fail closed, not "staged and broken"), that the values the model does not get to choose really
/// come from the system, and that a staged row is <b>Active and untaken</b> — no order, no execution.
/// </remarks>
public class GenerateSuggestionToolTests
{
    private static readonly DateTimeOffset _now = new(2026, 9, 4, 14, 0, 0, TimeSpan.Zero);

    private readonly string _database = Guid.NewGuid().ToString();
    private readonly Guid _owner = Guid.NewGuid();
    private readonly ISessionDeadlineSource _deadlines = A.Fake<ISessionDeadlineSource>();
    private readonly ISuggestionRealtimeNotifier _notifier = A.Fake<ISuggestionRealtimeNotifier>();
    private readonly SuggestionOptions _options = new() { ValidityMinutes = 60, ChatProposalSize = 3 };

    private sealed record FixedUser(Guid UserId) : ICurrentUser;

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private DbContextOptions<TradingCopilotDbContext> ContextOptions =>
        new DbContextOptionsBuilder<TradingCopilotDbContext>().UseInMemoryDatabase(_database).Options;

    private TradingCopilotDbContext Context(Guid asUser) => new(ContextOptions, new FixedUser(asUser));

    private GenerateSuggestionTool Tool() => new(
        ContextOptions,
        new FixedUser(_owner),
        _deadlines,
        _notifier,
        new FixedClock(_now),
        Options.Create(_options),
        NullLogger<GenerateSuggestionTool>.Instance);

    /// <summary>A coherent long: stop below entry, target above. The happy input every negative case mutates one field of.</summary>
    private static string Input(
        string instrument = "ES",
        string side = "Buy",
        decimal entry = 5000m,
        decimal stop = 4990m,
        decimal target = 5030m,
        string rationale = "Reclaimed the overnight low with acceptance.",
        int confidence = 70,
        string? account = null) =>
        JsonSerializer.Serialize(new
        {
            instrument,
            side,
            entryPrice = entry,
            stopPrice = stop,
            targetPrice = target,
            rationale,
            confidence,
            account,
        });

    private async Task<Guid> SeedAccountAsync(
        Guid owner, string name, TradingMode mode = TradingMode.Practice, bool canTrade = true, bool isActive = true)
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
        return account.Id;
    }

    private async Task<IReadOnlyList<Suggestion>> StagedAsync()
    {
        await using TradingCopilotDbContext context = Context(_owner);
        return await context.Suggestions.IgnoreQueryFilters().AsNoTracking().ToListAsync();
    }

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    private static string? ErrorIn(string json) =>
        Parse(json).TryGetProperty("error", out JsonElement error) ? error.GetString() : null;

    // =================================================================================================================
    // The happy path — and what about it is the SYSTEM's rather than the model's.
    // =================================================================================================================

    [Fact]
    public async Task ExecuteAsync_ShouldStageAnActiveSuggestion_WhenTheProposalIsCoherent()
    {
        Guid accountId = await SeedAccountAsync(_owner, "PRAC-50K-1234");

        string result = await Tool().ExecuteAsync(Input(), CancellationToken.None);

        ErrorIn(result).Should().BeNull("a coherent proposal against a tradable account is staged");
        Suggestion staged = (await StagedAsync()).Should().ContainSingle(
            "one call stages exactly one proposal").Which;
        staged.UserId.Should().Be(_owner, "the row is written under the calling operator (R-20)");
        staged.AccountId.Should().Be(accountId);
        staged.Instrument.Should().Be("ES");
        staged.Side.Should().Be(OrderSide.Buy);
        staged.EntryPrice.Should().Be(5000m);
        staged.StopPrice.Should().Be(4990m);
        staged.TargetPrice.Should().Be(5030m);
        staged.State.Should().Be(
            SuggestionState.Active, "a proposal is surfaced for the operator — never taken, and never an order");
        staged.Version.Should().Be(1, "a chat proposal opens its own chain rather than joining a trigger's spine");
        staged.SupersedesId.Should().BeNull();
        staged.TriggerFiringId.Should().BeNull("no firing produced this — chat is not the scan");
        Parse(result).GetProperty("suggestionId").GetGuid().Should().Be(
            staged.Id, "the model is told which row it staged, so it can name it back to the trader");
    }

    [Fact]
    public async Task ExecuteAsync_ShouldTakeSizeModeAndExpiryFromTheSystem_NeverTheModel()
    {
        await SeedAccountAsync(_owner, "LIVE-1", TradingMode.Live);
        // A deadline twenty minutes from now IN MARKET WALL-CLOCK, derived rather than hard-coded so the case says
        // "twenty minutes out" in every DST regime instead of silently ceasing to clamp half the year.
        TimeOnly deadline = TimeOnly.FromDateTime(MarketClock.ToMarketTime(_now).AddMinutes(20));
        A.CallTo(() => _deadlines.DeadlineFor(A<InstrumentId>._)).Returns(deadline);

        // The input NAMES a size, a mode and an expiry. None of them are in the tool's schema, so all three must be
        // ignored outright — this is the assertion that the model cannot size its own proposal.
        string smuggled = JsonSerializer.Serialize(new
        {
            instrument = "ES",
            side = "Buy",
            entryPrice = 5000m,
            stopPrice = 4990m,
            targetPrice = 5030m,
            rationale = "Trend day continuation.",
            confidence = 90,
            size = 40,
            mode = "Live",
            expiresAt = _now.AddDays(3),
        });

        await Tool().ExecuteAsync(smuggled, CancellationToken.None);

        Suggestion staged = (await StagedAsync()).Should().ContainSingle().Which;
        staged.Size.Should().Be(
            _options.ChatProposalSize, "size is the operator's configured chat proposal size — never the model's 40");
        staged.Mode.Should().Be(TradingMode.Live, "mode is read LIVE off the account (R-14), not taken from the input");
        staged.ExpiresAt.Should().Be(
            _now.AddMinutes(20),
            "the expiry is the configured window CLAMPED to the market's auto-flatten deadline — never the model's "
            + "three days, and never past the moment this market gets flattened anyway");
    }

    [Fact]
    public async Task ExecuteAsync_ShouldPushTheStagedCardToItsOwner()
    {
        await SeedAccountAsync(_owner, "PRAC-50K-1234");

        await Tool().ExecuteAsync(Input(), CancellationToken.None);

        Suggestion staged = (await StagedAsync()).Should().ContainSingle().Which;
        A.CallTo(() => _notifier.SuggestionChangedAsync(
                _owner,
                A<RealtimeSuggestion>.That.Matches(push =>
                    push.SuggestionId == staged.Id && push.State == nameof(SuggestionState.Active)),
                A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task ExecuteAsync_ShouldStillStage_WhenTheRealtimePushFaults()
    {
        await SeedAccountAsync(_owner, "PRAC-50K-1234");
        A.CallTo(() => _notifier.SuggestionChangedAsync(A<Guid>._, A<RealtimeSuggestion>._, A<CancellationToken>._))
            .Throws(new InvalidOperationException("hub down"));

        string result = await Tool().ExecuteAsync(Input(), CancellationToken.None);

        ErrorIn(result).Should().BeNull("the push is presentation-only (ADR-0021) — its failure never unwinds the write");
        (await StagedAsync()).Should().ContainSingle("the proposal is durable regardless of delivery");
    }

    // =================================================================================================================
    // Fail closed. Every one of these must write NOTHING — a partial or incoherent row is worse than a refusal.
    // =================================================================================================================

    /// <summary>Geometry the system rejects, one field wrong in each direction, plus the non-positive-price case.</summary>
    public static TheoryData<string, decimal, decimal, decimal> IncoherentGeometry() => new()
    {
        { "Buy", 5000m, 5010m, 5030m },  // stop ABOVE entry on a long
        { "Buy", 5000m, 4990m, 4980m },  // target BELOW entry on a long
        { "Sell", 5000m, 4990m, 4970m }, // stop BELOW entry on a short
        { "Sell", 5000m, 5010m, 5020m }, // target ABOVE entry on a short
        { "Buy", 5000m, -1m, 5030m },    // a negative price is never coherent
    };

    [Theory]
    [MemberData(nameof(IncoherentGeometry))]
    public async Task ExecuteAsync_ShouldFailClosed_WhenTheGeometryIsIncoherent(
        string side, decimal entry, decimal stop, decimal target)
    {
        await SeedAccountAsync(_owner, "PRAC-50K-1234");

        string result = await Tool().ExecuteAsync(
            Input(side: side, entry: entry, stop: stop, target: target), CancellationToken.None);

        ErrorIn(result).Should().NotBeNull("an incoherent proposal is refused, not staged");
        (await StagedAsync()).Should().BeEmpty(
            "fail CLOSED: the row is never written, so the operator is never shown a broken setup to take");
    }

    /// <summary>Malformed inputs the model can genuinely produce — each must return an error, never throw.</summary>
    public static TheoryData<string, string> MalformedInputs() => new()
    {
        { "not json at all", "unparseable" },
        { "[]", "a JSON array rather than an object" },
        { "{}", "no fields at all" },
        { "{\"instrument\":\"ES\",\"side\":\"Sideways\",\"entryPrice\":1,\"stopPrice\":1,\"targetPrice\":1,\"rationale\":\"x\",\"confidence\":1}", "an invented side" },
        { "{\"instrument\":\"ES\",\"side\":\"Buy\",\"entryPrice\":\"cheap\",\"stopPrice\":1,\"targetPrice\":2,\"rationale\":\"x\",\"confidence\":1}", "a price that is not a number" },
        { "{\"instrument\":\"ES\",\"side\":\"Buy\",\"entryPrice\":5000,\"stopPrice\":4990,\"targetPrice\":5030,\"rationale\":\"x\",\"confidence\":140}", "a confidence outside 0-100" },
        { "{\"instrument\":\" \",\"side\":\"Buy\",\"entryPrice\":5000,\"stopPrice\":4990,\"targetPrice\":5030,\"rationale\":\"x\",\"confidence\":50}", "a blank instrument" },
    };

    [Theory]
    [MemberData(nameof(MalformedInputs))]
    public async Task ExecuteAsync_ShouldReturnAnErrorAndStageNothing_WhenTheInputIsMalformed(string input, string why)
    {
        await SeedAccountAsync(_owner, "PRAC-50K-1234");

        string result = await Tool().ExecuteAsync(input, CancellationToken.None);

        ErrorIn(result).Should().NotBeNull($"the tool fails closed with an error result rather than throwing on {why}");
        (await StagedAsync()).Should().BeEmpty("nothing is written on a refused input");
    }

    [Fact]
    public async Task ExecuteAsync_ShouldRefuseAnOverLongRationale_RatherThanTruncatingIt()
    {
        await SeedAccountAsync(_owner, "PRAC-50K-1234");

        string result = await Tool().ExecuteAsync(
            Input(rationale: new string('x', 2001)), CancellationToken.None);

        ErrorIn(result).Should().NotBeNull(
            "a truncated rationale is a DIFFERENT claim from the one the model made, so it is refused, not trimmed");
        (await StagedAsync()).Should().BeEmpty();
    }

    // =================================================================================================================
    // Account tradability and R-20 scoping — whose money, and whose data.
    // =================================================================================================================

    [Theory]
    [InlineData(TradingMode.Undeclared, true, true, "an UNDECLARED account is refused everywhere, production included")]
    [InlineData(TradingMode.Practice, false, true, "the venue does not permit trading this account")]
    [InlineData(TradingMode.Practice, true, false, "the account is deactivated")]
    public async Task ExecuteAsync_ShouldFailClosed_WhenNoAccountIsTradable(
        TradingMode mode, bool canTrade, bool isActive, string why)
    {
        await SeedAccountAsync(_owner, "PRAC-50K-1234", mode, canTrade, isActive);

        string result = await Tool().ExecuteAsync(Input(), CancellationToken.None);

        ErrorIn(result).Should().NotBeNull($"nothing may be proposed when {why}");
        (await StagedAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_ShouldFailClosed_WhenSeveralAccountsMatchAndNoneIsNamed()
    {
        await SeedAccountAsync(_owner, "PRAC-50K-1234");
        await SeedAccountAsync(_owner, "PRAC-50K-5678");

        string result = await Tool().ExecuteAsync(Input(), CancellationToken.None);

        ErrorIn(result).Should().Contain(
            "Name the account", "which money the setup is proposed against is not a choice to guess at");
        (await StagedAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_ShouldStageAgainstTheNamedAccount_WhenSeveralAreTradable()
    {
        await SeedAccountAsync(_owner, "PRAC-50K-1234");
        Guid chosen = await SeedAccountAsync(_owner, "PRAC-50K-5678");

        string result = await Tool().ExecuteAsync(Input(account: "prac-50k-5678"), CancellationToken.None);

        ErrorIn(result).Should().BeNull("naming the account resolves the ambiguity");
        (await StagedAsync()).Should().ContainSingle().Which.AccountId.Should().Be(
            chosen, "the named account is the one proposed against, matched case-insensitively");
    }

    [Fact]
    public async Task ExecuteAsync_ShouldNotSeeAnotherOperatorsAccount()
    {
        Guid stranger = Guid.NewGuid();
        await SeedAccountAsync(stranger, "SOMEONE-ELSES");

        string result = await Tool().ExecuteAsync(Input(account: "SOMEONE-ELSES"), CancellationToken.None);

        ErrorIn(result).Should().NotBeNull(
            "R-20: another operator's account is invisible, so it is indistinguishable from absent");
        (await StagedAsync()).Should().BeEmpty("and no row is written against it");
    }
}
