using MarqSpec.TradingCopilot.Api.Recovery;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Data.Tenancy;
using MarqSpec.TradingCopilot.Domain.Recovery;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MarqSpec.TradingCopilot.UnitTests.Api.Recovery;

/// <summary>
/// The durable pre-transmit intent for the operator's per-position exit and reduce (gh#1161): committed in its
/// own unit <b>before</b> the venue is touched, so a caller abort or a lock-cleanup fault after transmit still
/// leaves the requested quantity on a row the reconcile sweep can surface.
/// </summary>
public class PositionActionIntentStoreTests
{
    private readonly Guid _operator = Guid.NewGuid();
    private readonly string _database = Guid.NewGuid().ToString();

    private sealed record FixedUser(Guid UserId) : ICurrentUser;

    private TradingCopilotDbContext Context() =>
        new(new DbContextOptionsBuilder<TradingCopilotDbContext>().UseInMemoryDatabase(_database).Options,
            new FixedUser(_operator));

    private PositionActionIntentStore Store() =>
        new(Context(), NullLogger<PositionActionIntentStore>.Instance);

    private static PositionActionIntentDraft ReduceDraft(Guid owner, Guid account, int requested = 3) => new()
    {
        Action = PositionActionKind.Reduce,
        OwnerUserId = owner,
        AccountId = account,
        VenueAccountKey = "9001",
        Instrument = "MES",
        Contract = "CON.F.US.MES.U26",
        RequestedQuantity = requested,
    };

    [Fact]
    public async Task CommitAsync_ShouldPersistAnOpenIntentCarryingTheRequestedQuantity_WhenAReduceIsCommitted()
    {
        // The fact venue truth cannot reconstruct afterwards has to land BEFORE the send. A crash between this
        // commit and the #1160 journal leaves an Open row with the quantity, not silence.
        Guid account = Guid.NewGuid();
        PositionActionIntentStore store = Store();

        Guid id = await store.CommitAsync(ReduceDraft(_operator, account), CancellationToken.None);

        await using TradingCopilotDbContext read = Context();
        PositionActionIntent row = await read.PositionActionIntents.SingleAsync(candidate => candidate.Id == id);
        row.UserId.Should().Be(_operator);
        row.AccountId.Should().Be(account);
        row.Action.Should().Be((int)PositionActionKind.Reduce);
        row.Instrument.Should().Be("MES");
        row.Contract.Should().Be("CON.F.US.MES.U26");
        row.VenueAccountKey.Should().Be("9001");
        row.RequestedQuantity.Should().Be(3);
        row.Status.Should().Be(PositionActionIntentStatus.Open);
        row.Outcome.Should().BeNull();
        row.ResolvedAt.Should().BeNull();
    }

    [Fact]
    public async Task CommitAsync_ShouldPersistAnOpenIntentWithNoRequestedQuantity_WhenAnExitIsCommitted()
    {
        // Symmetry: the exit asks for flat, not a size. The row still exists so a crash after ClosePositionAsync
        // is a stranded intent the sweep can surface, not a silent miss.
        Guid account = Guid.NewGuid();
        PositionActionIntentDraft draft = new()
        {
            Action = PositionActionKind.Exit,
            OwnerUserId = _operator,
            AccountId = account,
            VenueAccountKey = "9001",
            Instrument = "MES",
            Contract = "CON.F.US.MES.U26",
            RequestedQuantity = null,
        };

        Guid id = await Store().CommitAsync(draft, CancellationToken.None);

        await using TradingCopilotDbContext read = Context();
        PositionActionIntent row = await read.PositionActionIntents.SingleAsync(candidate => candidate.Id == id);
        row.Action.Should().Be((int)PositionActionKind.Exit);
        row.RequestedQuantity.Should().BeNull();
        row.Status.Should().Be(PositionActionIntentStatus.Open);
    }

    [Fact]
    public async Task ResolveSafelyAsync_ShouldMarkTheIntentResolvedWithTheOutcome_WhenItIsStillOpen()
    {
        Guid id = await Store().CommitAsync(ReduceDraft(_operator, Guid.NewGuid()), CancellationToken.None);
        DateTimeOffset resolvedAt = new(2026, 9, 12, 18, 0, 0, TimeSpan.Zero);

        await Store().ResolveSafelyAsync(id, nameof(PositionReduceOutcome.Reduced), resolvedAt);

        await using TradingCopilotDbContext read = Context();
        PositionActionIntent row = await read.PositionActionIntents.SingleAsync(candidate => candidate.Id == id);
        row.Status.Should().Be(PositionActionIntentStatus.Resolved);
        row.Outcome.Should().Be(nameof(PositionReduceOutcome.Reduced));
        row.ResolvedAt.Should().Be(resolvedAt);
    }

    [Fact]
    public async Task ResolveSafelyAsync_ShouldNotThrow_WhenTheIntentIsMissing()
    {
        // A resolve that cannot find its row must not replace a verified close with an error. The sweep will
        // not see a missing row either — the journal (when it landed) is what an incident reads.
        Func<Task> act = () => Store().ResolveSafelyAsync(
            Guid.NewGuid(), "Reduced", DateTimeOffset.UtcNow);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task ResolveSafelyAsync_ShouldLeaveAnAlreadyResolvedIntentAlone_WhenCalledTwice()
    {
        Guid id = await Store().CommitAsync(ReduceDraft(_operator, Guid.NewGuid()), CancellationToken.None);
        DateTimeOffset first = new(2026, 9, 12, 18, 0, 0, TimeSpan.Zero);
        await Store().ResolveSafelyAsync(id, "Reduced", first);

        await Store().ResolveSafelyAsync(id, "Unconfirmed", first.AddMinutes(1));

        await using TradingCopilotDbContext read = Context();
        PositionActionIntent row = await read.PositionActionIntents.SingleAsync(candidate => candidate.Id == id);
        row.Outcome.Should().Be("Reduced");
        row.ResolvedAt.Should().Be(first);
    }
}
