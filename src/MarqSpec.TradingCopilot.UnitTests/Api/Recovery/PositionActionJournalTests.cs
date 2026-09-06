using System.Text.Json;
using FakeItEasy;
using FakeItEasy.Core;
using MarqSpec.TradingCopilot.Api.Audit;
using MarqSpec.TradingCopilot.Api.Recovery;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Domain.Audit;
using MarqSpec.TradingCopilot.Domain.Events;
using Microsoft.Extensions.Logging.Abstractions;

namespace MarqSpec.TradingCopilot.UnitTests.Api.Recovery;

/// <summary>
/// The journal both operator position actions write through (gh#1143) — an event-log append (ADR-0001) plus an
/// immutable operator-owned <see cref="AuditRecord"/> (gh#220), the pair the auto-flatten already writes.
/// </summary>
/// <remarks>
/// The three properties these tests defend: <b>what is recorded</b> (the request, the exposure either side of the
/// attempt, and the outcome — read back off the wire, never asked of the writer); <b>both halves land, and they
/// fail independently</b>; and <b>nothing ever propagates</b>, because every caller reaches this seam holding an
/// outcome it is about to return to an operator, after a real order already reached a real venue.
/// </remarks>
public class PositionActionJournalTests
{
    private static readonly DateTimeOffset At = new(2026, 9, 6, 14, 30, 0, TimeSpan.Zero);

    private readonly Guid _operator = Guid.NewGuid();
    private readonly Guid _account = Guid.NewGuid();
    private readonly IEventLog _eventLog = A.Fake<IEventLog>();
    private readonly IAuditLog _auditLog = A.Fake<IAuditLog>();

    private PositionActionJournal Journal() =>
        new(_eventLog, _auditLog, NullLogger<PositionActionJournal>.Instance);

    private PositionActionEntry ExitEntry(
        string outcome = "Flat", int? netAfter = 0, string? contract = "CON.F.US.MES.U26") => new()
        {
            Action = PositionActionKind.Exit,
            OwnerUserId = _operator,
            AccountId = _account,
            VenueAccountKey = "9001",
            Instrument = "MES",
            Contract = contract,
            RequestedQuantity = null,
            NetQuantityBefore = null,
            NetQuantityAfter = netAfter,
            Outcome = outcome,
        };

    private PositionActionEntry ReduceEntry(
        string outcome = "Reduced", int requested = 3, int? netBefore = 5, int? netAfter = 2) => new()
        {
            Action = PositionActionKind.Reduce,
            OwnerUserId = _operator,
            AccountId = _account,
            VenueAccountKey = "9001",
            Instrument = "MES",
            Contract = "CON.F.US.MES.U26",
            RequestedQuantity = requested,
            NetQuantityBefore = netBefore,
            NetQuantityAfter = netAfter,
            Outcome = outcome,
        };

    /// <summary>The event this journal appended, read back off the seam rather than asked of the writer.</summary>
    private EventDraft AppendedEvent()
    {
        ICompletedFakeObjectCall call = Fake.GetCalls(_eventLog)
            .Single(candidate => candidate.Method.Name == nameof(IEventLog.AppendAsync));
        return (EventDraft)call.Arguments[0]!;
    }

    /// <summary>The audit row this journal wrote.</summary>
    private AuditRecord WrittenAudit()
    {
        ICompletedFakeObjectCall call = Fake.GetCalls(_auditLog)
            .Single(candidate => candidate.Method.Name == nameof(IAuditLog.WriteAsync));
        return ((IReadOnlyCollection<AuditRecord>)call.Arguments[0]!).Single();
    }

    private static JsonElement Payload(EventDraft draft) => JsonDocument.Parse(draft.Payload).RootElement;

    // --- The event-log half (ADR-0001) ---

    [Fact]
    public async Task RecordAsync_ShouldAppendTheExitEventWithItsOutcome_WhenAnExitIsRecorded()
    {
        await Journal().RecordAsync(ExitEntry(), At);

        EventDraft draft = AppendedEvent();
        draft.Type.Should().Be("position.exit");
        draft.Source.Should().Be("position-action");
        draft.OccurredAt.Should().Be(At);

        JsonElement payload = Payload(draft);
        payload.GetProperty("action").GetString().Should().Be("Exit");
        payload.GetProperty("account").GetGuid().Should().Be(_account);
        payload.GetProperty("venueAccount").GetString().Should().Be("9001");
        payload.GetProperty("instrument").GetString().Should().Be("MES");
        payload.GetProperty("contract").GetString().Should().Be("CON.F.US.MES.U26");
        payload.GetProperty("outcome").GetString().Should().Be("Flat");
        payload.GetProperty("netQuantityAfter").GetInt32().Should().Be(0);
        // A full close asks for flat, not for a size: there is no requested quantity to invent one for.
        payload.GetProperty("requestedQuantity").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task RecordAsync_ShouldCarryTheRequestedAndTheAchievedQuantities_WhenAReduceIsRecorded()
    {
        // The whole reason this card exists: after a reduce, HOW MANY the operator asked to take off is not
        // reconstructable from venue truth. A long 5 that becomes a long 2 looks the same whether 3 was requested,
        // or 1 was and a stop took 2. Both numbers therefore have to be on the row, not just the result.
        await Journal().RecordAsync(ReduceEntry(), At);

        EventDraft draft = AppendedEvent();
        draft.Type.Should().Be("position.reduce");
        draft.Source.Should().Be("position-action");

        JsonElement payload = Payload(draft);
        payload.GetProperty("action").GetString().Should().Be("Reduce");
        payload.GetProperty("requestedQuantity").GetInt32().Should().Be(3);
        payload.GetProperty("netQuantityBefore").GetInt32().Should().Be(5);
        payload.GetProperty("netQuantityAfter").GetInt32().Should().Be(2);
        payload.GetProperty("outcome").GetString().Should().Be("Reduced");
    }

    [Theory]
    [InlineData("Unconfirmed")]
    [InlineData("NotReduced")]
    [InlineData("Refused")]
    [InlineData("ExceedsPosition")]
    [InlineData("AccountBusy")]
    [InlineData("HeldPracticeOnly")]
    [InlineData("Unreachable")]
    public async Task RecordAsync_ShouldRecordTheOutcome_WhenTheReduceDidNotSucceed(string outcome)
    {
        // The non-success outcomes are the ones an incident is reconstructed from; a journal that only records the
        // happy path records the one case venue truth could have told you about anyway.
        await Journal().RecordAsync(ReduceEntry(outcome: outcome, netAfter: null), At);

        Payload(AppendedEvent()).GetProperty("outcome").GetString().Should().Be(outcome);
        WrittenAudit().After.Should().Be(outcome);
    }

    [Fact]
    public async Task RecordAsync_ShouldRecordNoQuantityAtAll_WhenTheExposureWasNeverEstablished()
    {
        // An exposure nobody could read is unknown. A 0 here would fabricate a flat out of an outage, which is the
        // failure gh#929 exists to prevent -- and the journal is precisely what an incident reads back.
        await Journal().RecordAsync(ExitEntry(outcome: "Unreachable", netAfter: null, contract: null), At);

        JsonElement payload = Payload(AppendedEvent());
        payload.GetProperty("netQuantityAfter").ValueKind.Should().Be(JsonValueKind.Null);
        payload.GetProperty("contract").ValueKind.Should().Be(JsonValueKind.Null);
    }

    // --- The audit half (gh#220, engineering §9) ---

    [Fact]
    public async Task RecordAsync_ShouldWriteTheOperatorOwnedAuditRow_WhenAnExitIsRecorded()
    {
        await Journal().RecordAsync(ExitEntry(), At);

        AuditRecord record = WrittenAudit();
        record.Action.Should().Be(AuditAction.PositionExitAttempted);
        record.UserId.Should().Be(_operator);          // R-20: stamped with the affected account's owner
        record.Placement.Should().Be(AuditPlacement.None);
        record.SyntheticRisk.Should().BeFalse();
        record.StopPlanId.Should().BeNull();
        record.After.Should().Be("Flat");
        record.RecordedAt.Should().Be(At);
        record.Detail.Should().Contain("MES").And.Contain("9001").And.Contain("Flat");
    }

    [Fact]
    public async Task RecordAsync_ShouldWriteTheReduceAuditRowCarryingBothQuantities_WhenAReduceIsRecorded()
    {
        await Journal().RecordAsync(ReduceEntry(), At);

        AuditRecord record = WrittenAudit();
        record.Action.Should().Be(AuditAction.PositionReduceAttempted);
        record.Before.Should().Be("5");
        record.After.Should().Be("Reduced");
        record.Detail.Should().Contain("3 contract(s)").And.Contain("5 → 2");
    }

    [Fact]
    public async Task RecordAsync_ShouldLeaveTheSourceNull_WhenAPositionActionIsRecorded()
    {
        // CK_AuditRecords_Source_MatchesAction binds a non-null source to the kill / flatten set (5-7) alone. These
        // two actions sit outside it -- the gh#909 precedent -- and there is nothing to disambiguate anyway: an
        // authenticated operator request is their only possible trigger. A stamped source would fail the insert.
        await Journal().RecordAsync(ReduceEntry(), At);

        WrittenAudit().Source.Should().BeNull();
    }

    [Fact]
    public async Task RecordAsync_ShouldBoundTheDetail_WhenTheFactsAreLong()
    {
        // Detail is capped at 512 by the model configuration. An over-long value must lose characters, never the row.
        PositionActionEntry entry = ReduceEntry() with { Instrument = new string('X', 600) };

        await Journal().RecordAsync(entry, At);

        WrittenAudit().Detail.Length.Should().BeLessThanOrEqualTo(512);
    }

    // --- It cannot fail the action, and the two halves fail independently ---

    [Fact]
    public async Task RecordAsync_ShouldStillWriteTheAuditRow_WhenTheEventLogFails()
    {
        A.CallTo(() => _eventLog.AppendAsync(A<EventDraft>._, A<CancellationToken>._))
            .Throws(new InvalidOperationException("the event log is down"));

        await Journal().RecordAsync(ReduceEntry(), At);

        WrittenAudit().After.Should().Be("Reduced");
    }

    [Fact]
    public async Task RecordAsync_ShouldStillAppendTheEvent_WhenTheAuditWriteFails()
    {
        A.CallTo(() => _auditLog.WriteAsync(A<IReadOnlyCollection<AuditRecord>>._, A<CancellationToken>._))
            .Throws(new InvalidOperationException("the audit store is down"));

        await Journal().RecordAsync(ReduceEntry(), At);

        AppendedEvent().Type.Should().Be("position.reduce");
    }

    [Fact]
    public async Task RecordAsync_ShouldNotThrow_WhenBothStoresFail()
    {
        A.CallTo(() => _eventLog.AppendAsync(A<EventDraft>._, A<CancellationToken>._))
            .Throws(new InvalidOperationException("the event log is down"));
        A.CallTo(() => _auditLog.WriteAsync(A<IReadOnlyCollection<AuditRecord>>._, A<CancellationToken>._))
            .Throws(new InvalidOperationException("the audit store is down"));

        await Journal().Invoking(journal => journal.RecordAsync(ReduceEntry(), At)).Should().NotThrowAsync();
    }

    [Fact]
    public async Task RecordAsync_ShouldNotThrow_WhenAStoreReportsCancellation()
    {
        // Cancellation is swallowed too, and that is the point rather than an oversight: the venue action has
        // already happened, so the record of a real order must not be dropped -- nor turned into an aborted
        // response -- because the HTTP client hung up. There is no token parameter to pass a cancelled one through.
        A.CallTo(() => _auditLog.WriteAsync(A<IReadOnlyCollection<AuditRecord>>._, A<CancellationToken>._))
            .Throws(new OperationCanceledException("the caller went away"));

        await Journal().Invoking(journal => journal.RecordAsync(ExitEntry(), At)).Should().NotThrowAsync();
    }

    [Fact]
    public async Task RecordAsync_ShouldNotHandTheStoresACancelledToken_WhenItWrites()
    {
        // The belt for the same property: whatever the caller was doing, these two writes run uncancelled.
        await Journal().RecordAsync(ExitEntry(), At);

        A.CallTo(() => _eventLog.AppendAsync(A<EventDraft>._, A<CancellationToken>.That.Matches(
            token => !token.CanBeCanceled))).MustHaveHappenedOnceExactly();
        A.CallTo(() => _auditLog.WriteAsync(A<IReadOnlyCollection<AuditRecord>>._, A<CancellationToken>.That.Matches(
            token => !token.CanBeCanceled))).MustHaveHappenedOnceExactly();
    }
}
