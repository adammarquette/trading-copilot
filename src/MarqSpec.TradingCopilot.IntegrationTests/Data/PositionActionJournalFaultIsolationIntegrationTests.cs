using System.Text.Json;
using MarqSpec.TradingCopilot.Api.Audit;
using MarqSpec.TradingCopilot.Api.Recovery;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Data.Events;
using MarqSpec.TradingCopilot.Data.Tenancy;
using MarqSpec.TradingCopilot.Domain.Audit;
using MarqSpec.TradingCopilot.IntegrationTests.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace MarqSpec.TradingCopilot.IntegrationTests.Data;

/// <summary>
/// Pre-merge integration coverage for the <b>fault isolation of the position-action journal</b> (gh#1143 ⇒ gh#656 /
/// gh#928; ADR-0001, ADR-0007, engineering §9) against <b>real Postgres</b>.
/// </summary>
/// <remarks>
/// <para>
/// <b>The claim under test, and why only this tier can witness it.</b> The journal promises that its two halves fail
/// <i>independently</i> — a failing event-log append still leaves the <see cref="AuditRecord"/>, and a failing audit
/// write still leaves the event. The unit tier proves that against a <b>fake</b> <c>IEventLog</c> that touches no
/// <see cref="TradingCopilotDbContext"/> at all, so it is structurally blind to the way the claim can be false in
/// production: <c>TimescaleEventLog</c> and <c>AuditLog</c> are both scoped over the <b>same</b> context, so a failed
/// <c>SaveChanges</c> can leave its entity tracked as <c>Added</c> and the <i>next</i> save on that context — the
/// audit's — re-attempts it, fails again, and rolls the whole batch back, losing both rows.
/// </para>
/// <para>
/// <b>Nothing is doubled.</b> The journal, the event log and the audit log are the shipped types over one real
/// context against the migrated schema; the failure is a <b>real</b> Postgres refusal, produced by a CHECK
/// constraint this suite adds on purpose and drops again. That is deliberate: a store double that throws without
/// ever reaching the shared change tracker would reproduce the fake's blindness rather than the defect.
/// </para>
/// <para>
/// <b>The positive control keeps the theory honest</b> — an unconstrained record must land <i>both</i> rows, or the
/// two isolation tests could be passing because nothing writes at all.
/// </para>
/// </remarks>
public class PositionActionJournalFaultIsolationIntegrationTests : IClassFixture<FlattenTestPostgresFactory>
{
    private const string RejectEventConstraint = "CK_Test_gh1143_RejectReduceEvent";
    private const string RejectAuditConstraint = "CK_Test_gh1143_RejectReduceAudit";

    private static DateTimeOffset At => new(2026, 9, 6, 14, 30, 0, TimeSpan.Zero);

    private readonly FlattenTestPostgresFactory _factory;

    public PositionActionJournalFaultIsolationIntegrationTests(FlattenTestPostgresFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task RecordAsync_ShouldStillWriteTheAuditRow_WhenTheRealEventAppendFailsOnTheSharedContext()
    {
        // The direction that was actually broken. The event insert is refused by the database, which leaves its
        // entity on the change tracker of the context the audit write is about to save through -- so without an
        // explicit detach the audit's SaveChanges batches the poisoned Event with it, the batch is refused as a
        // whole, and the operator's reduce ends up with NO record at all: the exact outcome the seam promises
        // cannot happen.
        Guid owner = Guid.NewGuid();
        PositionActionEntry entry = ReduceEntry(owner);

        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        await using TradingCopilotDbContext shared = ContextFor(scope, owner);

        await AddCheckAsync(shared, "Events", RejectEventConstraint, "\"Type\" <> 'position.reduce'");
        try
        {
            await JournalOver(shared).RecordAsync(entry, At);
        }
        finally
        {
            await DropCheckAsync(shared, "Events", RejectEventConstraint);
        }

        (await AuditRowsForAsync(owner)).Should().ContainSingle(
            "a failing event-log append must not cost the audit row — the two halves are contracted to fail "
            + "independently, and this row is the only surviving record that the operator asked for a reduce")
            .Which.After.Should().Be(entry.Outcome);

        (await EventsForAsync(entry.VenueAccountKey)).Should().BeEmpty(
            "the event insert really was refused by the database — otherwise this proves nothing");

        shared.ChangeTracker.Entries<Event>().Should().BeEmpty(
            "a refused append must leave nothing tracked behind it: an Added entity that survives its own failed "
            + "save is what poisons the next save on the shared context");
    }

    [Fact]
    public async Task RecordAsync_ShouldStillLeaveTheEventRow_WhenTheRealAuditWriteFailsOnTheSharedContext()
    {
        // The mirror. The event is already committed by the time the audit is refused, so the row must survive --
        // and the refused audit entity must likewise leave nothing tracked, or it poisons whatever the request
        // saves next.
        Guid owner = Guid.NewGuid();
        PositionActionEntry entry = ReduceEntry(owner);

        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        await using TradingCopilotDbContext shared = ContextFor(scope, owner);

        await AddCheckAsync(
            shared, "AuditRecords", RejectAuditConstraint,
            $"\"Action\" <> {(int)AuditAction.PositionReduceAttempted}");
        try
        {
            await JournalOver(shared).RecordAsync(entry, At);
        }
        finally
        {
            await DropCheckAsync(shared, "AuditRecords", RejectAuditConstraint);
        }

        (await EventsForAsync(entry.VenueAccountKey)).Should().ContainSingle(
            "a failing audit write must not cost the event — it was already committed, and it carries the "
            + "requested quantity venue truth cannot give back")
            .Which.Payload.Should().Match(payload => RequestedQuantityOf(payload) == 3,
                "the surviving row carries the one fact venue truth cannot give back");

        (await AuditRowsForAsync(owner)).Should().BeEmpty(
            "the audit insert really was refused by the database — otherwise this proves nothing");

        shared.ChangeTracker.Entries<AuditRecord>().Should().BeEmpty(
            "a refused audit write must leave nothing tracked behind it either");
    }

    [Fact]
    public async Task RecordAsync_ShouldWriteBothRows_WhenNeitherStoreRefuses()
    {
        // The control. Without it, both isolation tests above could be green because this journal writes nothing at
        // all against a real database.
        Guid owner = Guid.NewGuid();
        PositionActionEntry entry = ReduceEntry(owner);

        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        await using TradingCopilotDbContext shared = ContextFor(scope, owner);

        await JournalOver(shared).RecordAsync(entry, At);

        (await EventsForAsync(entry.VenueAccountKey)).Should().ContainSingle(
            "the shipped journal appends one position.reduce event per attempt");
        (await AuditRowsForAsync(owner)).Should().ContainSingle(
            "and one operator-owned audit row beside it")
            .Which.Action.Should().Be(AuditAction.PositionReduceAttempted);
    }

    // =================================================================================================================
    // Fixture.
    // =================================================================================================================

    private sealed record FixedUser(Guid UserId) : ICurrentUser;

    // Parsed, not string-matched: Postgres normalises jsonb on the way in, so the stored text is not the text the
    // producer serialised. Reading the value back is the only assertion that survives that.
    private static int? RequestedQuantityOf(string payload) =>
        JsonDocument.Parse(payload).RootElement.GetProperty("requestedQuantity") is { ValueKind: JsonValueKind.Number } value
            ? value.GetInt32()
            : null;

    private static PositionActionJournal JournalOver(TradingCopilotDbContext shared) =>
        new(new TimescaleEventLog(shared), new AuditLog(shared),
            NullLogger<PositionActionJournal>.Instance);

    private TradingCopilotDbContext ContextFor(AsyncServiceScope scope, Guid owner) =>
        new(scope.ServiceProvider.GetRequiredService<DbContextOptions<TradingCopilotDbContext>>(), new FixedUser(owner));

    // A venue account key unique to the run, so each test finds its own event among a shared table's rows.
    private static PositionActionEntry ReduceEntry(Guid owner) => new()
    {
        Action = PositionActionKind.Reduce,
        OwnerUserId = owner,
        AccountId = Guid.NewGuid(),
        VenueAccountKey = $"gh1143-{owner:N}",
        Instrument = "MES",
        Contract = "CON.F.US.MES.U26",
        RequestedQuantity = 3,
        NetQuantityBefore = 5,
        NetQuantityAfter = 2,
        Outcome = "Reduced",
    };

    // NOT VALID, deliberately: the suite shares one migrated database, so rows an earlier test committed would
    // otherwise make the ALTER itself fail. NOT VALID skips the back-check and still enforces the constraint on
    // every INSERT, which is the only thing these tests need it to do.
    //
    // DDL takes no bound parameters, so EF1002 is suppressed rather than worked around. Every value interpolated
    // below is a compile-time constant of this test class (or `(int)` of an enum member) — nothing crosses a
    // trust boundary, and the constraints are added and dropped inside the test that needs them.
#pragma warning disable EF1002 // Risk of vulnerability to SQL injection.
    private static Task AddCheckAsync(TradingCopilotDbContext database, string table, string name, string predicate) =>
        database.Database.ExecuteSqlRawAsync(
            $"ALTER TABLE \"{table}\" ADD CONSTRAINT \"{name}\" CHECK ({predicate}) NOT VALID");

    private static Task DropCheckAsync(TradingCopilotDbContext database, string table, string name) =>
        database.Database.ExecuteSqlRawAsync(
            $"ALTER TABLE \"{table}\" DROP CONSTRAINT IF EXISTS \"{name}\"");
#pragma warning restore EF1002

    // Read back through a FRESH context so the answer is what the database holds, never what a change tracker
    // still believes.
    private async Task<List<Event>> EventsForAsync(string venueAccountKey)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        await using TradingCopilotDbContext read = ContextFor(scope, Guid.NewGuid());
        List<Event> candidates = await read.Events
            .Where(candidate => candidate.Type == PositionActionJournal.ReduceEventType)
            .ToListAsync();
        return [.. candidates.Where(candidate => candidate.Payload.Contains(venueAccountKey, StringComparison.Ordinal))];
    }

    private async Task<List<AuditRecord>> AuditRowsForAsync(Guid owner)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        await using TradingCopilotDbContext read = ContextFor(scope, owner);
        return await read.AuditRecords.Where(candidate => candidate.UserId == owner).ToListAsync();
    }
}
