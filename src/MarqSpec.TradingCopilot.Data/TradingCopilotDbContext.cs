using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Data.Tenancy;
using MarqSpec.TradingCopilot.Domain.Suggestions;
using Microsoft.EntityFrameworkCore;

namespace MarqSpec.TradingCopilot.Data;

/// <summary>
/// The application <see cref="DbContext"/>. Inherits the fail-closed scoping model from
/// <see cref="TenantDbContext"/> — every <see cref="IUserOwned"/> entity gets a default-deny per-user query
/// filter (R-20 / ADR-0017).
/// </summary>
public class TradingCopilotDbContext : TenantDbContext
{
    /// <summary>Creates the context.</summary>
    /// <param name="options">The context options (provider, connection).</param>
    /// <param name="currentUser">The authenticated-user accessor used to scope every user-owned query.</param>
    public TradingCopilotDbContext(DbContextOptions<TradingCopilotDbContext> options, ICurrentUser currentUser)
        : base(options, currentUser)
    {
    }

    /// <summary>The tenant-root users.</summary>
    public DbSet<User> Users => Set<User>();

    /// <summary>Onboarding invitations (R-18) — not user-owned; accepted anonymously by token hash.</summary>
    public DbSet<Invitation> Invitations => Set<Invitation>();

    /// <summary>The operator's firms — prop firms and brokerages they trade with (gh#76). Operator-owned.</summary>
    public DbSet<Firm> Firms => Set<Firm>();

    /// <summary>Per-firm stage declarations (gh#60) — what each stage means. Operator-owned.</summary>
    public DbSet<FirmStageConvention> FirmStageConventions => Set<FirmStageConvention>();

    /// <summary>Firm logins — one per firm × platform (ADR-0016). Operator-owned; no secrets stored.</summary>
    public DbSet<Connection> Connections => Set<Connection>();

    /// <summary>Trading accounts as discovered through connections. Operator-owned.</summary>
    public DbSet<Account> Accounts => Set<Account>();

    /// <summary>AI trade suggestions — the journal spine (gh#7). Operator-owned; mode-guarded (R-14).</summary>
    public DbSet<Suggestion> Suggestions => Set<Suggestion>();

    /// <summary>Operator dispositions of a suggestion (gh#547, R-4/R-8/R-9). Operator-owned; append-only, one per suggestion.</summary>
    public DbSet<SuggestionDisposition> SuggestionDispositions => Set<SuggestionDisposition>();

    /// <summary>The cited-factor set behind each suggestion (gh#729, ADR-0026, R-4). Operator-owned; one primary + supporting.</summary>
    public DbSet<CitedFactor> CitedFactors => Set<CitedFactor>();

    /// <summary>Journaled orders — the journal spine (gh#7). Operator-owned; mode-guarded (R-14).</summary>
    public DbSet<Order> Orders => Set<Order>();

    /// <summary>Native executions of orders. Operator-owned; mode inherited through the order.</summary>
    public DbSet<Fill> Fills => Set<Fill>();

    /// <summary>Journaled trades — round-trip outcomes. Operator-owned.</summary>
    public DbSet<Trade> Trades => Set<Trade>();

    /// <summary>Journal outcomes — how a suggestion / trade resolved, with the R-15 removal flags (gh#832). Operator-owned.</summary>
    public DbSet<Outcome> Outcomes => Set<Outcome>();

    /// <summary>Recomposition-suppression tombstones — a hard-deleted outcome's source, so no sweep re-derives it (gh#955). Operator-owned.</summary>
    public DbSet<OutcomeSuppression> OutcomeSuppressions => Set<OutcomeSuppression>();

    /// <summary>Post-close operator (or future AI) feedback on a trade (R-8, gh#1064). Operator-owned.</summary>
    public DbSet<TradeFeedback> TradeFeedbacks => Set<TradeFeedback>();

    /// <summary>Declared per-account risk rules (R-5, gh#10). Operator-owned; one per account.</summary>
    public DbSet<RiskProfileRecord> RiskProfiles => Set<RiskProfileRecord>();

    /// <summary>Staged-stop plans — one per order (ADR-0007, gh#11). Operator-owned.</summary>
    public DbSet<StopPlanRecord> StopPlans => Set<StopPlanRecord>();

    /// <summary>Synthetic conditional entry orders — "send when conditions met" (ADR-0007, gh#176). Operator-owned.</summary>
    public DbSet<ConditionalOrderRecord> ConditionalOrders => Set<ConditionalOrderRecord>();

    /// <summary>Persisted gate decisions — every sized send attempt, auditable (R-5/R-16, gh#11). Operator-owned.</summary>
    public DbSet<GateDecisionRecord> GateDecisions => Set<GateDecisionRecord>();

    /// <summary>The append-only event log (ADR-0001). System plumbing — an acknowledged global, not operator-owned.</summary>
    public DbSet<Event> Events => Set<Event>();

    /// <summary>
    /// The clean-historical bar store (gh#302, R-1) — the <b>system of record</b> the 24-hour event log is not.
    /// Market data, so global by R-20's own rule, not operator-owned.
    /// </summary>
    public DbSet<BarRecord> Bars => Set<BarRecord>();

    /// <summary>
    /// Pre-computed indicator values (gh#310) — projections over <see cref="Bars"/>, reproducible from it
    /// (ADR-0001: rebuild = replay). Derived market data, so global like the bars they come from (R-20).
    /// </summary>
    public DbSet<IndicatorValueRecord> IndicatorValues => Set<IndicatorValueRecord>();

    /// <summary>
    /// Persisted key-level zones (gh#596, R-10) — support / resistance bands per timeframe. Derived market data,
    /// so global (R-20); the detector that populates it is gh#597.
    /// </summary>
    public DbSet<PriceLevel> PriceLevels => Set<PriceLevel>();

    /// <summary>
    /// The raw news / soft-signal store of record (gh#358, R-2) — deduped across sources, the reference template
    /// for non-market feeds. Shared / global reference data (R-20) like the bars; the per-user salience that
    /// reweights it (gh#27) is a separate operator-owned entity, not this row.
    /// </summary>
    public DbSet<NewsRecord> News => Set<NewsRecord>();

    /// <summary>
    /// The stored vector width (gh#109). Cohere's embed-v3 family is 1024, and an ANN index requires a fixed
    /// width — so this is a schema constant, not configuration: changing it is a migration.
    /// </summary>
    public const int EmbeddingDimensions = 1024;

    /// <summary>
    /// The polymorphic embedding store (gh#109, ADR-0001, engineering §2) — the system's third storage shape.
    /// Global / not operator-owned (R-20), following the owners it embeds.
    /// </summary>
    public DbSet<EmbeddingRecord> Embeddings => Set<EmbeddingRecord>();

    /// <summary>Global ticker↔instrument relevance maps (gh#359) — deployment config, not operator-owned.</summary>
    public DbSet<TickerInstrumentMap> TickerInstrumentMaps => Set<TickerInstrumentMap>();

    /// <summary>Global relevance topics (gh#359) — deployment config, not operator-owned.</summary>
    public DbSet<NewsTopic> NewsTopics => Set<NewsTopic>();

    /// <summary>The single-row relevance-config version marker (gh#359) — drives re-resolution. System plumbing.</summary>
    public DbSet<RelevanceConfigState> RelevanceConfigStates => Set<RelevanceConfigState>();

    /// <summary>
    /// Notifications held durably until delivered (gh#400). An acknowledged global (R-20): an alert belongs to
    /// the deployment, and the relay runs as background plumbing with no authenticated user.
    /// </summary>
    public DbSet<NotificationOutboxRecord> NotificationOutbox => Set<NotificationOutboxRecord>();

    /// <summary>Per-consumer-group replay cursors over the event log (ADR-0001). System plumbing.</summary>
    public DbSet<EventCursor> EventCursors => Set<EventCursor>();

    /// <summary>The durable kill-switch state (gh#189) — one row, rehydrated at startup so the lock survives a restart.</summary>
    public DbSet<KillSwitchState> KillSwitchStates => Set<KillSwitchState>();

    /// <summary>The append-only audit trail (ADR-0007, gh#220) — safety-relevant transitions, immutable. Operator-owned.</summary>
    public DbSet<AuditRecord> AuditRecords => Set<AuditRecord>();

    /// <summary>Standing deterministic triggers (gh#385, R-4 / R-7, ADR-0008). Operator-owned.</summary>
    public DbSet<TriggerRecord> Triggers => Set<TriggerRecord>();

    /// <summary>The append-only journal of trigger firings (gh#385, ADR-0008). Operator-owned.</summary>
    public DbSet<TriggerFiringRecord> TriggerFirings => Set<TriggerFiringRecord>();

    /// <summary>Per-operator news importance feedback — stars and mutes (gh#27, ADR-0014). Operator-owned.</summary>
    public DbSet<SoftSignalFeedback> SoftSignalFeedbacks => Set<SoftSignalFeedback>();

    /// <summary>The append-only per-call AI spend ledger (gh#431, ADR-0008 / ADR-0002). Operator-owned.</summary>
    public DbSet<AiUsageRecord> AiUsage => Set<AiUsageRecord>();

    /// <summary>Co-pilot chat conversations (gh#18, R-6) — the thread a session's messages belong to. Operator-owned.</summary>
    public DbSet<Conversation> Conversations => Set<Conversation>();

    /// <summary>Messages within a conversation (gh#18, R-6) — ordered by <c>Sequence</c>. Operator-owned.</summary>
    public DbSet<ChatMessage> ChatMessages => Set<ChatMessage>();

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<User>(user =>
        {
            user.HasIndex(u => u.Email).IsUnique();
            user.Property(u => u.Email).HasMaxLength(320);
            user.Property(u => u.DisplayName).HasMaxLength(128);
        });

        modelBuilder.Entity<Invitation>(invitation =>
        {
            invitation.HasIndex(i => i.TokenHash).IsUnique();
            invitation.Property(i => i.Email).HasMaxLength(320);
            invitation.Property(i => i.TokenHash).HasMaxLength(128);
        });

        modelBuilder.Entity<Firm>(firm =>
        {
            firm.Property(f => f.Name).HasMaxLength(128);

            // Unique within an operator's workspace, not globally: two operators may each trade "Topstep".
            firm.HasIndex(f => new { f.UserId, f.Name }).IsUnique();

            firm.HasMany(f => f.StageConventions)
                .WithOne()
                .HasForeignKey(convention => convention.FirmId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<FirmStageConvention>(convention =>
        {
            // One declaration per (firm, stage) -- a stage cannot mean two things at one firm.
            convention.HasIndex(c => new { c.FirmId, c.Stage }).IsUnique();

            // AccountStage.Unknown (0) is not declarable -- FirmConventions.For rejects it -- so the DB refuses
            // to persist it, defense-in-depth below the service (gh#60). Ties to the enum's fail-closed zero.
            convention.ToTable(table =>
                table.HasCheckConstraint("CK_FirmStageConvention_Stage_NotUnknown", "\"Stage\" <> 0"));
        });

        modelBuilder.Entity<Connection>(connection =>
        {
            connection.Property(c => c.Platform).HasMaxLength(64);
            connection.Property(c => c.CredentialKey).HasMaxLength(128);

            // One login per firm x platform (ADR-0016): Apex-on-Tradovate and Apex-on-Rithmic are two rows;
            // a second login for the same pair is a mistake, not a variant.
            connection.HasIndex(c => new { c.UserId, c.FirmId, c.Platform }).IsUnique();

            connection.HasOne<Firm>()
                .WithMany()
                .HasForeignKey(c => c.FirmId)
                .OnDelete(DeleteBehavior.Cascade);

            connection.HasMany(c => c.Accounts)
                .WithOne()
                .HasForeignKey(account => account.ConnectionId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Account>(account =>
        {
            account.Property(a => a.VenueAccountKey).HasMaxLength(64);
            account.Property(a => a.Name).HasMaxLength(128);

            // One row per venue handle within a connection -- rediscovery updates, never duplicates.
            account.HasIndex(a => new { a.ConnectionId, a.VenueAccountKey }).IsUnique();

            // An override IS a declaration, so Unknown (0) is refused the same way FirmStageConvention refuses
            // it (gh#60) -- clearing the override (NULL) is how the operator says "I don't know".
            account.ToTable(table =>
                table.HasCheckConstraint("CK_Accounts_StageOverride_NotUnknown", "\"StageOverride\" <> 0"));
        });

        modelBuilder.Entity<Suggestion>(suggestion =>
        {
            suggestion.Property(s => s.Instrument).HasMaxLength(64);

            suggestion.HasOne<Account>()
                .WithMany()
                .HasForeignKey(s => s.AccountId)
                .OnDelete(DeleteBehavior.Cascade);

            // The supersede chain (gh#550, R-4 / ADR-0013): a re-formed setup issues a NEW row linked to the one it
            // supersedes, rather than mutating it. RESTRICT, not cascade: a superseded row can never be silently
            // deleted while a later version still points at it, so the lineage the R-9 loop reads never vanishes.
            // EF adds the FK's index automatically. Single-incumbent is enforced in APPLICATION code (the scan), NOT a
            // partial unique index -- gh#455: the suggestion is staged into the scan pass's shared DbContext with the
            // firing journal + arm transition, so a unique-index violation would abort that whole SaveChanges and lose
            // them. Version defaults to 1 (first issuance), the superseding row is one higher.
            suggestion.HasOne<Suggestion>()
                .WithMany()
                .HasForeignKey(s => s.SupersedesId)
                .OnDelete(DeleteBehavior.Restrict);

            suggestion.Property(s => s.Version).HasDefaultValue(1);


            // The read model's query shape (gh#540): the R-20 filter pins UserId, the list filters on State and
            // orders by CreatedAt. Owned by this card so the index does not end up owned by nobody once the
            // suggestion table starts growing -- it is an append-only journal.
            suggestion.HasIndex(s => new { s.UserId, s.State, s.CreatedAt })
                .HasDatabaseName("IX_Suggestions_UserId_State_CreatedAt");

            // The R-14 persistence guard, half one: Undeclared (0) and an unset state are refused outright --
            // nothing is ever suggested on an undeclared account. Half two (mode must equal the account's
            // persisted mode) is a cross-table rule a single-row CHECK cannot express; it lives in the
            // enforce_mode_matches_account constraint trigger added by the AddExecutionJournal migration.
            suggestion.Property(s => s.Rationale).HasMaxLength(2000);

            suggestion.ToTable(table =>
            {
                table.HasCheckConstraint("CK_Suggestions_Mode_NotUndeclared", "\"Mode\" <> 0");
                table.HasCheckConstraint("CK_Suggestions_State_NotUnknown", "\"State\" <> 0");

                // The producer is a FACT of the row (gh#1134), so an unset one is refused exactly as an unset state
                // is: since the chat tool joined the scan as a second writer, "which producer?" can no longer be
                // inferred from a null TriggerFiringId, and a row that answered it with the zero would be a row whose
                // card cannot say what it is showing.
                table.HasCheckConstraint("CK_Suggestions_Origin_NotUnknown", "\"Origin\" <> 0");
                table.HasCheckConstraint("CK_Suggestions_Size_Positive", "\"Size\" > 0");

                // The model's confidence is display-only (gh#543), but an out-of-range number is still a malformed
                // row -- the reviewer fails closed on one, and this refuses a direct write that bypassed it.
                table.HasCheckConstraint("CK_Suggestions_Confidence_Range", "\"Confidence\" BETWEEN 0 AND 100");

                // The validity window is the system's (gh#544). Strictly after issuance: a row that expires at or
                // before the moment it was issued is never actionable, so it is a defect rather than a fast expiry.
                table.HasCheckConstraint("CK_Suggestions_ExpiresAfterCreated", "\"ExpiresAt\" > \"CreatedAt\"");
            });
        });

        modelBuilder.Entity<SuggestionDisposition>(disposition =>
        {
            // Subordinate to its suggestion: the suggestion is append-only journal in production, but if a row ever
            // is deleted the disposition goes with it rather than dangling.
            disposition.HasOne<Suggestion>()
                .WithMany()
                .HasForeignKey(d => d.SuggestionId)
                .OnDelete(DeleteBehavior.Cascade);

            // One disposition per suggestion (data dictionary §6, gh#539): append-only journal evidence, so a second
            // disposition CONFLICTS rather than overwriting. The endpoint pre-checks for a clean 409; this is the DB
            // backstop against a race.
            disposition.HasIndex(d => d.SuggestionId)
                .IsUnique()
                .HasDatabaseName("IX_SuggestionDispositions_SuggestionId");

            disposition.Property(d => d.Note).HasMaxLength(SuggestionDisposition.NoteMaxLength);

            // Take-time price snapshot (gh#549) -- the Taken* columns mirror the suggestion's own bare `numeric`
            // prices (arbitrary precision, tick-size-aware), so they carry no HasPrecision, matching EntryPrice /
            // StopPrice / TargetPrice above.
            disposition.ToTable(table =>
            {
                // Kind records an operator ACT, so the refusable zero is refused (the CK_Suggestions_State_NotUnknown
                // pattern). Reasons deliberately carries NO such check: a pass is a neutral decline and its reason is
                // optional (R-4), so 0 (none) is a legitimate answer, not a sentinel.
                table.HasCheckConstraint("CK_SuggestionDispositions_Kind_NotUnknown", "\"Kind\" <> 0");

                // R-9 integrity, backstopped below the model (gh#549): the classifier records deviations IFF the take
                // was Modified (Kind = 2). A Modified with no deviated field, or a Taken/Passed carrying deviations, is
                // a malformed row -- this refuses a direct write that bypassed SuggestionDisposition.ForTake.
                table.HasCheckConstraint(
                    "CK_SuggestionDispositions_Deviations_MatchModified",
                    "(\"Kind\" = 2) = (\"Deviations\" <> 0)");

                // A take (Taken = 1 / Modified = 2) carries its price snapshot; a pass (Passed = 3) carries none.
                // TakenTargetPrice is exempt on the take side -- a take may legitimately drop the target (it is null).
                table.HasCheckConstraint(
                    "CK_SuggestionDispositions_TakeSnapshot",
                    "(\"Kind\" IN (1, 2) AND \"TakenEntryPrice\" IS NOT NULL AND \"TakenStopPrice\" IS NOT NULL "
                        + "AND \"TakenSize\" IS NOT NULL) "
                        + "OR (\"Kind\" = 3 AND \"TakenEntryPrice\" IS NULL AND \"TakenStopPrice\" IS NULL "
                        + "AND \"TakenTargetPrice\" IS NULL AND \"TakenSize\" IS NULL)");
            });
        });

        modelBuilder.Entity<CitedFactor>(factor =>
        {
            // Child of its suggestion (gh#729): cascade like SuggestionDisposition — a factor never outlives the
            // suggestion it cites. WithMany wires the CitedFactors navigation so the read model can Include the set.
            factor.HasOne<Suggestion>()
                .WithMany(suggestion => suggestion.CitedFactors)
                .HasForeignKey(f => f.SuggestionId)
                .OnDelete(DeleteBehavior.Cascade);

            // The Include join reads a suggestion's whole set; the FK index carries it.
            factor.HasIndex(f => f.SuggestionId)
                .HasDatabaseName("IX_SuggestionCitedFactors_SuggestionId");

            // Exactly one primary per suggestion (ADR-0026) — a PARTIAL unique index over the primary rows only, so
            // supporting factors stay unconstrained in number. Safe as a unique index here (unlike the supersede
            // single-incumbent, gh#455): issuance stages exactly one primary per suggestion, so it never self-conflicts.
            factor.HasIndex(f => f.SuggestionId, "UX_SuggestionCitedFactors_OnePrimary")
                .IsUnique()
                .HasFilter("\"IsPrimary\"");

            // The indicator arm copies the fired read exactly as Suggestion.CitedIndicator did (same 32 cap); the
            // level snapshot mirrors PriceLevel's own widths and 18,8 price precision.
            factor.Property(f => f.Indicator).HasMaxLength(CitedFactor.IndicatorMaxLength);
            factor.Property(f => f.LevelVenue).HasMaxLength(CitedFactor.LevelVenueMaxLength);
            factor.Property(f => f.LevelTop).HasPrecision(18, 8);
            factor.Property(f => f.LevelBottom).HasPrecision(18, 8);
            factor.Property(f => f.LevelSignificance).HasPrecision(18, 8);

            factor.ToTable("SuggestionCitedFactors", table =>
            {
                // Fail-closed zeros: a factor always has a kind and a real timeframe (the refusable-zero pattern).
                table.HasCheckConstraint("CK_SuggestionCitedFactors_Kind_NotUnknown", "\"Kind\" <> 0");
                table.HasCheckConstraint("CK_SuggestionCitedFactors_Timeframe_Positive", "\"TimeframeMinutes\" > 0");

                // The kind/arm pairing, below the model (mirrors CK_SuggestionDispositions_TakeSnapshot): an Indicator
                // (1) fills the indicator columns and nulls the whole level snapshot; a Level (2) is the exact reverse.
                // This refuses a half-built row that bypassed issuance — the arm can never disagree with the kind.
                table.HasCheckConstraint(
                    "CK_SuggestionCitedFactors_KindColumns",
                    "(\"Kind\" = 1 AND \"Indicator\" IS NOT NULL AND \"Period\" IS NOT NULL "
                        + "AND \"LevelId\" IS NULL AND \"LevelVenue\" IS NULL AND \"LevelKind\" IS NULL "
                        + "AND \"LevelTop\" IS NULL AND \"LevelBottom\" IS NULL AND \"LevelSignificance\" IS NULL) "
                        + "OR (\"Kind\" = 2 AND \"Indicator\" IS NULL AND \"Period\" IS NULL "
                        + "AND \"LevelId\" IS NOT NULL AND \"LevelVenue\" IS NOT NULL AND \"LevelKind\" IS NOT NULL "
                        + "AND \"LevelTop\" IS NOT NULL AND \"LevelBottom\" IS NOT NULL AND \"LevelSignificance\" IS NOT NULL)");

                // When the level arm is present it is a well-formed zone (mirrors PriceLevel's own checks): an ordered
                // band with a real side. NULL passes, so the indicator arm is unaffected.
                table.HasCheckConstraint(
                    "CK_SuggestionCitedFactors_LevelZoneOrdered",
                    "\"LevelTop\" IS NULL OR \"LevelBottom\" IS NULL OR \"LevelTop\" > \"LevelBottom\"");
                table.HasCheckConstraint(
                    "CK_SuggestionCitedFactors_LevelKind_NotUnknown",
                    "\"LevelKind\" IS NULL OR \"LevelKind\" <> 0");
            });
        });

        modelBuilder.Entity<Order>(order =>
        {
            order.Property(o => o.Instrument).HasMaxLength(64);
            order.Property(o => o.VenueOrderKey).HasMaxLength(64);

            order.HasOne<Account>()
                .WithMany()
                .HasForeignKey(o => o.AccountId)
                .OnDelete(DeleteBehavior.Cascade);

            // The journal outlives its provenance: deleting a suggestion nulls the link, never the order.
            order.HasOne<Suggestion>()
                .WithMany()
                .HasForeignKey(o => o.SuggestionId)
                .OnDelete(DeleteBehavior.SetNull);

            // One row per venue handle within an account (null venue keys -- synthetic/unplaced -- excepted).
            order.HasIndex(o => new { o.AccountId, o.VenueOrderKey }).IsUnique();

            // The R-14 persistence guard, half one (see the Suggestion comment): no Undeclared order, ever --
            // gh#7 records that outright rejection beats merely matching the parent. The cross-table half is
            // the enforce_mode_matches_account constraint trigger.
            order.ToTable(table =>
            {
                table.HasCheckConstraint("CK_Orders_Mode_NotUndeclared", "\"Mode\" <> 0");
                table.HasCheckConstraint("CK_Orders_Status_NotUnknown", "\"Status\" <> 0");
                table.HasCheckConstraint("CK_Orders_Size_Positive", "\"Size\" > 0");

                // The entry-method taxonomy (R-11, gh#181): a real order records how it was placed, so the sentinel
                // Unknown (0) is refused -- NULL passes for rows journaled before the field existed (fail-closed zero).
                table.HasCheckConstraint("CK_Orders_EntryMethod_NotUnknown", "\"EntryMethod\" IS NULL OR \"EntryMethod\" <> 0");

                // The take-profit-on-the-winning-side invariant, below the domain (ADR-0007, gh#173): a
                // take-profit only means anything above entry for a long, below it for a short -- the mirror of
                // CK_StopPlans_SafetyBeyondActual. Side-dependent, so a cross-column CHECK expresses it --
                // OrderSide.Buy = 0, Sell = 1; NULL (no profit leg) passes.
                table.HasCheckConstraint(
                    "CK_Orders_TakeProfit_WinningSide",
                    "\"TakeProfitPrice\" IS NULL "
                    + "OR (\"Side\" = 0 AND \"TakeProfitPrice\" > \"EntryPrice\") "
                    + "OR (\"Side\" = 1 AND \"TakeProfitPrice\" < \"EntryPrice\")");
            });
        });

        modelBuilder.Entity<Fill>(fill =>
        {
            fill.Property(f => f.VenueFillKey).HasMaxLength(64);

            fill.HasOne<Order>()
                .WithMany()
                .HasForeignKey(f => f.OrderId)
                .OnDelete(DeleteBehavior.Cascade);

            fill.HasIndex(f => new { f.OrderId, f.VenueFillKey }).IsUnique();

            fill.ToTable(table =>
                table.HasCheckConstraint("CK_Fills_Size_Positive", "\"Size\" > 0"));
        });

        modelBuilder.Entity<Trade>(trade =>
        {
            trade.Property(t => t.Instrument).HasMaxLength(64);

            trade.HasOne<Account>()
                .WithMany()
                .HasForeignKey(t => t.AccountId)
                .OnDelete(DeleteBehavior.Cascade);

            trade.HasOne<Suggestion>()
                .WithMany()
                .HasForeignKey(t => t.SuggestionId)
                .OnDelete(DeleteBehavior.SetNull);

            // (ClosingFillId, OpeningFillId) is the trade LEG's natural key -- the writer's idempotency (gh#731,
            // gh#759, ADR-0022). A leg is one opening fill and the closing fills that retire it, so a single closing
            // fill can retire two legs (a spanning exit) and ClosingFillId ALONE stopped being unique; the pair is.
            // A replayed flat PositionEvent recomposes the same legs and this index rejects the second insert, so a
            // duplicate can never double-count into the daily governor. Filtered on BOTH non-null: pre-#759 rows carry
            // a closing key but no opening one and are historical, not re-journalled, so they stay out of the index.
            trade.HasIndex(t => new { t.ClosingFillId, t.OpeningFillId })
                .IsUnique()
                .HasFilter("\"ClosingFillId\" IS NOT NULL AND \"OpeningFillId\" IS NOT NULL");

            trade.HasOne<Fill>()
                .WithMany()
                .HasForeignKey(t => t.ClosingFillId)
                .OnDelete(DeleteBehavior.SetNull);

            trade.HasOne<Fill>()
                .WithMany()
                .HasForeignKey(t => t.OpeningFillId)
                .OnDelete(DeleteBehavior.SetNull);

            // The two live readers (DailyRealizedReader, ConsistencyWindowReader) filter AccountId + ClosedAt and
            // require RealizedPnL -- only AccountId was indexed (gh#731 decision 7). Cover the composite so the
            // day-realized read stays a range scan as the journal grows. They also filter Mode (R-14, gh#746) so a
            // practice result never counts toward a live limit; Mode is left OUT of the index deliberately -- it is a
            // cheap residual over the few rows in one account's day, and adding it would not change the range-seek at
            // single-operator volume.
            trade.HasIndex(t => new { t.AccountId, t.ClosedAt });

            // Mode is a journal fact (practice results never blend into live results) -- check-constrained,
            // but NOT trigger-guarded: a trade closes after placement, when the declaration may legitimately
            // have moved on. The placement-time guard lives on Orders and Suggestions.
            trade.ToTable(table =>
            {
                table.HasCheckConstraint("CK_Trades_Mode_NotUndeclared", "\"Mode\" <> 0");
                table.HasCheckConstraint("CK_Trades_Size_Positive", "\"Size\" > 0");
            });
        });

        modelBuilder.Entity<Outcome>(outcome =>
        {
            outcome.ToTable(table =>
            {
                // A persisted outcome always carries a REAL resolution -- a defaulted or bad-cast Unknown (0) cannot
                // masquerade as one, the same posture SuggestionState takes.
                table.HasCheckConstraint("CK_Outcomes_Resolution_NotUnknown", "\"Resolution\" <> 0");

                // An outcome resolves a trade or scores a suggestion (data-dictionary ERD) -- at least one parent,
                // never a free-floating row that resolves nothing.
                table.HasCheckConstraint(
                    "CK_Outcomes_ParentPresent",
                    "\"TradeId\" IS NOT NULL OR \"SuggestionId\" IS NOT NULL");
            });

            // Dies WITH its trade -- which dies with its account (Trade.AccountId cascades) -- so removing the
            // operator's account (R-20) carries the outcome away too and never strands a trade-only row against
            // CK_Outcomes_ParentPresent.
            outcome.HasOne<Trade>()
                .WithMany()
                .HasForeignKey(o => o.TradeId)
                .OnDelete(DeleteBehavior.Cascade);

            // Cascade: an outcome dies WITH its suggestion (gh#939, operator decision 2026-08-16), settling the orphan
            // the gh#832 comment flagged -- an untaken outcome (no TradeId) that SetNull would have stranded against
            // CK_Outcomes_ParentPresent when a suggestion is deleted. The safety rests on a CONVENTION, not an enforced
            // invariant: a suggestion is append-only, so in production it is only ever removed via an account-removal
            // cascade (Suggestion.AccountId cascades), where the outcome should die anyway -- a TAKEN suggestion's
            // outcome already dies via its Trade's cascade above. The DB nonetheless ALLOWS a direct suggestion delete
            // (no production path does one today), and under Cascade that would also delete a trade-derived outcome
            // carrying this SuggestionId -- a lineage loss SetNull avoided; acceptable only while no such path exists,
            // and the reason the untaken path (gh#955) will suppress recomposition rather than lean on deletes.
            // Postgres permits the two cascade paths to one Outcome row (Trade and Suggestion); a row reached by both
            // is deleted once. (Account-cascade over a supersede chain still meets Suggestion.SupersedesId's RESTRICT.)
            outcome.HasOne<Suggestion>()
                .WithMany()
                .HasForeignKey(o => o.SuggestionId)
                .OnDelete(DeleteBehavior.Cascade);

            // Zero-or-one Outcome per Trade (data-dictionary ERD `Trade ||--o| Outcome`) -- the DB-enforced 1:1-FK
            // posture StopPlanRecord.OrderId / SuggestionDisposition.SuggestionId take, so a retry or write-path bug
            // cannot silently mint two outcomes (one Win, one Loss) for one trade. Filtered to non-null (the outbox
            // partial-index pattern) so the many untaken-suggestion outcomes -- null TradeId, no trade -- are not
            // forced unique against each other; Postgres treats those nulls as distinct anyway, but the filter states
            // the intent and keeps the index off the null rows.
            outcome.HasIndex(o => o.TradeId).IsUnique().HasFilter("\"TradeId\" IS NOT NULL");

            // One UNTAKEN outcome per suggestion (gh#939) -- the untaken path's idempotency backstop, mirroring the
            // TradeId index. Filtered to no-trade rows: a taken suggestion can produce several trade legs (gh#759),
            // each a trade-derived outcome carrying the same SuggestionId, so uniqueness must apply only where there is
            // no trade. OutcomeJournalService.OutcomeSuggestionKeyIndex pins this name; a metadata test guards it.
            outcome.HasIndex(o => o.SuggestionId).IsUnique()
                .HasFilter("\"SuggestionId\" IS NOT NULL AND \"TradeId\" IS NULL");
        });

        modelBuilder.Entity<OutcomeSuppression>(suppression =>
        {
            suppression.ToTable("OutcomeSuppressions", table =>
            {
                // A tombstone suppresses EXACTLY ONE recomposition source -- the closed-trade sweep's TradeId or the
                // unfilled sweep's SuggestionId, never both -- so it silences precisely the sweep that would re-derive
                // the row. num_nonnulls states the one-key shape the writer always builds; a defaulted two-null row, or
                // a both-set one, cannot be stored even by a direct write.
                table.HasCheckConstraint(
                    "CK_OutcomeSuppressions_OneParent",
                    "num_nonnulls(\"TradeId\", \"SuggestionId\") = 1");
            });

            // Dies WITH its source (Cascade, mirroring Outcome's own FKs): removing the operator's account cascades the
            // trade / suggestion away and the tombstone with it, so a suppression never outlives what it suppresses -- a
            // gone source composes nothing, so nothing is left to suppress. Each row sets exactly one key, so it is
            // reached by exactly one of the two cascade paths.
            suppression.HasOne<Trade>()
                .WithMany()
                .HasForeignKey(s => s.TradeId)
                .OnDelete(DeleteBehavior.Cascade);

            suppression.HasOne<Suggestion>()
                .WithMany()
                .HasForeignKey(s => s.SuggestionId)
                .OnDelete(DeleteBehavior.Cascade);

            // One suppression per source -- the hard delete is idempotent against a replay, and only one outcome ever
            // exists per trade (IX_Outcomes_TradeId) or per untaken suggestion (IX_Outcomes_SuggestionId) to delete.
            // Filtered to the non-null key: every row of the opposite kind leaves this column null.
            suppression.HasIndex(s => s.TradeId).IsUnique().HasFilter("\"TradeId\" IS NOT NULL");
            suppression.HasIndex(s => s.SuggestionId).IsUnique().HasFilter("\"SuggestionId\" IS NOT NULL");
        });

        modelBuilder.Entity<TradeFeedback>(feedback =>
        {
            feedback.Property(f => f.Comment).HasMaxLength(TradeFeedback.CommentMaxLength);
            feedback.Property(f => f.EmotionalState).HasMaxLength(TradeFeedback.EmotionalStateMaxLength);

            // Dies WITH its trade (mirrors Outcome.TradeId) -- removing the operator's account cascades the trade
            // away and its feedback with it, so an annotation never strands against a gone parent.
            feedback.HasOne<Trade>()
                .WithMany()
                .HasForeignKey(f => f.TradeId)
                .OnDelete(DeleteBehavior.Cascade);

            // The read pattern (TradeFeedbackReader): a trade's feedback, oldest first.
            feedback.HasIndex(f => new { f.TradeId, f.CreatedAt });

            feedback.ToTable(table =>
            {
                table.HasCheckConstraint("CK_TradeFeedback_Author_NotUnknown", "\"Author\" <> 0");

                // Refuse a no-op row -- a feedback entry with no comment, no tags and no emotional state records
                // nothing (the CK_SoftSignalFeedback_Kind_NotUnknown posture: never a placeholder sitting in the
                // store). cardinality() returns 0 (never NULL) for the NOT NULL, default-'{}' Tags column.
                table.HasCheckConstraint(
                    "CK_TradeFeedback_HasContent",
                    "\"Comment\" IS NOT NULL OR \"EmotionalState\" IS NOT NULL OR cardinality(\"Tags\") > 0");
            });
        });

        modelBuilder.Entity<RiskProfileRecord>(profile =>
        {
            profile.ToTable("RiskProfiles", table =>
            {
                // The DB half of the declaration invariants (defense-in-depth below RiskProfile.Declare /
                // TrailingDrawdown.Start / ManualCaps.Create): numbers that would size nothing -- or
                // everything -- cannot be stored, even by a direct write. NULL passes the nullable checks.
                table.HasCheckConstraint("CK_RiskProfiles_TrailingAmount_Positive", "\"TrailingAmount\" > 0");
                table.HasCheckConstraint("CK_RiskProfiles_StartingBalance_Positive", "\"StartingBalance\" > 0");
                table.HasCheckConstraint("CK_RiskProfiles_PerTradeRiskFraction_ZeroToOne", "\"PerTradeRiskFraction\" > 0 AND \"PerTradeRiskFraction\" <= 1");
                table.HasCheckConstraint("CK_RiskProfiles_TargetRewardRatio_Positive", "\"TargetRewardRatio\" > 0");
                table.HasCheckConstraint("CK_RiskProfiles_MaxDrawdownPerTrade_Positive", "\"MaxDrawdownPerTrade\" > 0");
                table.HasCheckConstraint("CK_RiskProfiles_DailyDrawdownGovernor_Positive", "\"DailyDrawdownGovernor\" > 0");
                table.HasCheckConstraint("CK_RiskProfiles_MaxContractsPerOrder_NotNegative", "\"MaxContractsPerOrder\" >= 0");
                table.HasCheckConstraint("CK_RiskProfiles_MaxBestDayFraction_ZeroToOne", "\"MaxBestDayFraction\" > 0 AND \"MaxBestDayFraction\" <= 1");
            });

            // One declaration per account -- redeclaration replaces, never accumulates.
            profile.HasIndex(p => p.AccountId).IsUnique();

            profile.HasOne<Account>()
                .WithOne()
                .HasForeignKey<RiskProfileRecord>(p => p.AccountId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<GateDecisionRecord>(decision =>
        {
            decision.ToTable("GateDecisions");
            decision.Property(d => d.Reason).HasMaxLength(512);
            decision.Property(d => d.Advisories).HasColumnType("jsonb");

            decision.HasOne<Account>()
                .WithMany()
                .HasForeignKey(d => d.AccountId)
                .OnDelete(DeleteBehavior.Cascade);

            // The audit outlives the order row's fate; deleting an order never deletes its decision.
            decision.HasOne<Order>()
                .WithMany()
                .HasForeignKey(d => d.OrderId)
                .OnDelete(DeleteBehavior.SetNull);

            // The audit reads chronologically per account.
            decision.HasIndex(d => new { d.AccountId, d.DecidedAt });
        });

        modelBuilder.Entity<StopPlanRecord>(plan =>
        {
            plan.ToTable("StopPlans", table =>
            {
                // The safety-beyond-actual invariant, below the domain (ADR-0007): the catastrophic floor must
                // rest FURTHER from entry than the working stop, or it triggers first and the declared
                // worst case is neither deterministic nor the one declared. Side-dependent, so a cross-column
                // CHECK expresses it -- OrderSide.Buy = 0, Sell = 1.
                table.HasCheckConstraint(
                    "CK_StopPlans_SafetyBeyondActual",
                    "(\"Side\" = 0 AND \"SafetyStopPrice\" < \"ActualStopPrice\" AND \"ActualStopPrice\" < \"EntryPrice\") "
                    + "OR (\"Side\" = 1 AND \"SafetyStopPrice\" > \"ActualStopPrice\" AND \"ActualStopPrice\" > \"EntryPrice\")");
                table.HasCheckConstraint("CK_StopPlans_Staging_NotUnknown", "\"Staging\" <> 0");
                table.HasCheckConstraint("CK_StopPlans_ProximityMetric_NotUnknown", "\"ProximityMetric\" <> 0");
                table.HasCheckConstraint("CK_StopPlans_ProximityValue_Positive", "\"ProximityValue\" > 0");
            });

            // One plan per order.
            plan.HasIndex(p => p.OrderId).IsUnique();

            plan.HasOne<Order>()
                .WithOne()
                .HasForeignKey<StopPlanRecord>(p => p.OrderId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ConditionalOrderRecord>(conditional =>
        {
            conditional.Property(c => c.Instrument).HasMaxLength(64);
            conditional.Property(c => c.Symbol).HasMaxLength(32);

            conditional.HasOne<Account>()
                .WithMany()
                .HasForeignKey(c => c.AccountId)
                .OnDelete(DeleteBehavior.Cascade);

            // The journal outlives its provenance: deleting a suggestion nulls the link, never the conditional.
            conditional.HasOne<Suggestion>()
                .WithMany()
                .HasForeignKey(c => c.SuggestionId)
                .OnDelete(DeleteBehavior.SetNull);

            // The fired order outlives — or is deleted independently of — its trigger; null the link if it goes.
            conditional.HasOne<Order>()
                .WithMany()
                .HasForeignKey(c => c.FiredOrderId)
                .OnDelete(DeleteBehavior.SetNull);

            // The firing watcher scans pending orders (a later increment); status leads the read.
            conditional.HasIndex(c => c.Status);

            conditional.ToTable("ConditionalOrders", table =>
            {
                table.HasCheckConstraint("CK_ConditionalOrders_Mode_NotUndeclared", "\"Mode\" <> 0");
                table.HasCheckConstraint("CK_ConditionalOrders_Status_NotUnknown", "\"Status\" <> 0");
                table.HasCheckConstraint("CK_ConditionalOrders_Size_Positive", "\"Size\" > 0");
                table.HasCheckConstraint("CK_ConditionalOrders_Direction_NotUnknown", "\"TriggerDirection\" <> 0");

                // The cancel band sits on the STALE side of the trigger, below the domain (ADR-0007, gh#176):
                // below it for a RisesTo (1) order, above it for a FallsTo (2) -- a near-side band would cancel
                // the instant price approached the trigger. NULL (no drift cancel) passes.
                table.HasCheckConstraint(
                    "CK_ConditionalOrders_CancelDrift_StaleSide",
                    "\"CancelDriftPrice\" IS NULL "
                    + "OR (\"TriggerDirection\" = 1 AND \"CancelDriftPrice\" < \"TriggerPrice\") "
                    + "OR (\"TriggerDirection\" = 2 AND \"CancelDriftPrice\" > \"TriggerPrice\")");
            });
        });

        modelBuilder.Entity<IndicatorValueRecord>(value =>
        {
            // Indicator and Period are in the key, not merely columns: an ATR(14) and an ATR(3) are different
            // numbers over the same bars, and this value sets where a live stop sits (gh#311). BucketStart is
            // present because a hypertable's unique constraints must include the time dimension.
            value.HasKey(v => new
            {
                v.Venue,
                v.Instrument,
                v.ResolutionMinutes,
                v.Indicator,
                v.Period,
                v.BucketStart,
            });
            value.Property(v => v.Venue).HasMaxLength(64);
            value.Property(v => v.Instrument).HasMaxLength(32);
            value.Property(v => v.Indicator).HasMaxLength(32);
            value.Property(v => v.Value).HasPrecision(18, 8);

            // The read the execution path makes: this series' latest value at or before a moment.
            value.HasIndex(v => new { v.Instrument, v.ResolutionMinutes, v.Indicator, v.Period, v.BucketStart });
        });

        modelBuilder.Entity<PriceLevel>(level =>
        {
            level.Property(l => l.Venue).HasMaxLength(64);
            level.Property(l => l.Instrument).HasMaxLength(32);
            level.Property(l => l.Top).HasPrecision(18, 8);
            level.Property(l => l.Bottom).HasPrecision(18, 8);
            level.Property(l => l.Significance).HasPrecision(18, 8);

            // The read gh#593's confluence and any chart overlay make: active levels for an instrument across a set
            // of timeframes. Active leads because the default read wants only live zones.
            level.HasIndex(l => new { l.Venue, l.Instrument, l.TimeframeMinutes, l.Active });

            level.ToTable(table =>
            {
                // A zone is an ordered band, so Top must sit strictly above Bottom, and prices are positive -- with
                // the ordering, Bottom > 0 makes Top > 0 too.
                table.HasCheckConstraint("CK_PriceLevels_ZoneOrdered", "\"Top\" > \"Bottom\"");
                table.HasCheckConstraint("CK_PriceLevels_Bottom_Positive", "\"Bottom\" > 0");

                // A level always has a side (the refusable-zero pattern) and belongs to a real timeframe.
                table.HasCheckConstraint("CK_PriceLevels_Kind_NotUnknown", "\"Kind\" <> 0");
                table.HasCheckConstraint("CK_PriceLevels_Timeframe_Positive", "\"TimeframeMinutes\" > 0");
            });
        });

        modelBuilder.Entity<TriggerRecord>(trigger =>
        {
            trigger.Property(t => t.Symbol).HasMaxLength(32);
            trigger.Property(t => t.Indicator).HasMaxLength(32);
            trigger.Property(t => t.Threshold).HasPrecision(18, 8);
            trigger.Property(t => t.Hysteresis).HasPrecision(18, 8);
            trigger.Property(t => t.LastEvaluatedValue).HasPrecision(18, 8);

            // The scan discovers CONFIRMED, ENABLED, MECHANICAL/AGENT-REVIEW triggers first; this composite leads that
            // read (gh#385, gh#470). Confirmation leads: an unconfirmed trigger is inert regardless of Enabled, so the
            // most-selective fail-closed predicate is the cheapest to satisfy first.
            trigger.HasIndex(t => new { t.Confirmation, t.Enabled, t.Route });

            // The AGENT-REVIEW route issues a sized suggestion against an account (R-14); a mechanical trigger has
            // neither. On account delete, CASCADE -- an agent-review trigger is meaningless without its account, and
            // this matches every other account FK (Order / Trade / Suggestion / RiskProfile). SetNull would be wrong
            // here: it cannot null an AccountId the AgentReview-route CHECK requires non-null (the delete would roll
            // back). A mechanical trigger keeps AccountId null, so it is untouched by any account delete.
            trigger.HasOne<Account>()
                .WithMany()
                .HasForeignKey(t => t.AccountId)
                .OnDelete(DeleteBehavior.Cascade);

            trigger.ToTable("Triggers", table =>
            {
                // The fail-closed zeros are refused outright (defense-in-depth below the endpoint validation and the
                // domain's Unknown-never-satisfies rule): a defaulted or corrupt row can never read as a real trigger.
                table.HasCheckConstraint("CK_Triggers_Comparison_NotUnknown", "\"Comparison\" <> 0");
                table.HasCheckConstraint("CK_Triggers_ConditionKind_NotUnknown", "\"ConditionKind\" <> 0");
                table.HasCheckConstraint("CK_Triggers_Route_NotUnknown", "\"Route\" <> 0");
                table.HasCheckConstraint("CK_Triggers_Period_Positive", "\"Period\" > 0");
                table.HasCheckConstraint("CK_Triggers_ResolutionMinutes_Positive", "\"ResolutionMinutes\" > 0");
                table.HasCheckConstraint("CK_Triggers_Hysteresis_PositiveOrNull", "\"Hysteresis\" IS NULL OR \"Hysteresis\" > 0");

                // Confirmation's zero (Unconfirmed) is a REAL fail-closed default, not a refused Unknown -- so this
                // pins the column to the known value set rather than refusing zero (gh#470). Defense-in-depth: a
                // corrupt write can never land a value the scan's Confirmed check would silently treat as not-confirmed.
                table.HasCheckConstraint("CK_Triggers_Confirmation_Known", "\"Confirmation\" IN (0, 1)");

                // The route pairs with account+size, below the endpoint validation (TriggerRoute.AgentReview = 2):
                // an agent-review trigger MUST carry an account and a positive size (it issues a sized suggestion on
                // fire), and a mechanical trigger must carry NEITHER (it only alerts). A defaulted or corrupt row can
                // never read as a sized suggestion with no account, nor a mechanical alert with a stray size.
                table.HasCheckConstraint(
                    "CK_Triggers_AgentReview_RequiresAccountAndSize",
                    "\"Route\" <> 2 OR (\"AccountId\" IS NOT NULL AND \"Size\" > 0)");
                table.HasCheckConstraint(
                    "CK_Triggers_Mechanical_NoAccount",
                    "\"Route\" = 2 OR (\"AccountId\" IS NULL AND \"Size\" IS NULL)");

                // The threshold must sit inside its indicator's meaningful range, or the debounce seeds straight to
                // Fired and holds there — ADR-0019's silent monitor reached from authoring (gh#1007). Below
                // TriggerThreshold's endpoint refusal so a NON-API writer (a script, a replayed request) cannot bypass
                // it. Per-indicator because the indicator's own semantics decide, not "reject zero": RSI is bounded
                // 0–100 (a value at/beyond a bound makes one inclusive direction always- or never-true); ATR is a
                // non-negative magnitude. The migration adds this NOT VALID, so it enforces every NEW write without
                // failing the deploy on a row authored before the gate existed.
                table.HasCheckConstraint(
                    "CK_Triggers_Threshold_InIndicatorRange",
                    "(\"Indicator\" = 'rsi' AND \"Threshold\" > 0 AND \"Threshold\" < 100) "
                    + "OR (\"Indicator\" = 'atr' AND \"Threshold\" > 0)");
            });
        });

        modelBuilder.Entity<TriggerFiringRecord>(firing =>
        {
            firing.Property(f => f.DedupKey).HasMaxLength(128);
            firing.Property(f => f.ObservedValue).HasPrecision(18, 8);
            firing.Property(f => f.Threshold).HasPrecision(18, 8);

            // The journal reads per trigger.
            firing.HasIndex(f => f.TriggerId);

            firing.ToTable("TriggerFirings");
        });

        modelBuilder.Entity<AiUsageRecord>(usage =>
        {
            usage.Property(u => u.Model).HasMaxLength(128);
            usage.Property(u => u.TraceId).HasMaxLength(64);
            usage.Property(u => u.EstimatedCostUsd).HasPrecision(18, 8);

            // Serves the per-operator chronological read (the gh#62 in-app spend meter). The gh#448 governor's
            // platform-wide windowed sum filters OccurredAt with NO UserId predicate (it crosses R-20 with
            // IgnoreQueryFilters), so it does not use this composite's leading key — a small scan at today's volume;
            // a dedicated (OccurredAt) index is deferred until the ledger is large enough to warrant it.
            usage.HasIndex(u => new { u.UserId, u.OccurredAt });

            // The per-suggestion cost read (gh#767) aggregates an owner's rows by the firing they served, so index the
            // owner+firing pair. Filtered to the non-null firings -- most rows (chat, embeds) carry none, and only the
            // firing-correlated ones are ever grouped by it.
            usage.HasIndex(u => new { u.UserId, u.TriggerFiringId }).HasFilter("\"TriggerFiringId\" IS NOT NULL");

            usage.ToTable("AiUsage", table =>
            {
                // Refusable-zero enums (gh#60): an unset feature or outcome is never stored, so a row can never read
                // as a spend event whose kind is unknown. Tier is nullable (embeds carry none) but never a zero.
                table.HasCheckConstraint("CK_AiUsage_Feature_NotUnknown", "\"Feature\" <> 0");
                table.HasCheckConstraint("CK_AiUsage_Outcome_NotUnknown", "\"Outcome\" <> 0");
                table.HasCheckConstraint("CK_AiUsage_Tier_NotUnknownOrNull", "\"Tier\" IS NULL OR \"Tier\" <> 0");

                // Tokens, cost and latency are >= 0 -- a Failed row's zeros are a real datapoint, not an absence.
                table.HasCheckConstraint("CK_AiUsage_InputTokens_NotNegative", "\"InputTokens\" >= 0");
                table.HasCheckConstraint("CK_AiUsage_OutputTokens_NotNegative", "\"OutputTokens\" >= 0");
                table.HasCheckConstraint("CK_AiUsage_EstimatedCostUsd_NotNegative", "\"EstimatedCostUsd\" >= 0");
                table.HasCheckConstraint("CK_AiUsage_LatencyMs_NotNegative", "\"LatencyMs\" >= 0");
            });
        });

        modelBuilder.Entity<BarRecord>(bar =>
        {
            // The composite key IS the idempotence guard, enforced by the database rather than by the writer
            // remembering to check: a re-poll of an overlapping window can only update the bucket it already
            // wrote. It includes BucketStart because a hypertable's unique constraints must contain the time
            // dimension, and ResolutionMinutes because a 1-minute and a 5-minute bar can open at the same
            // instant -- keyed on time alone they would silently overwrite each other.
            bar.HasKey(b => new { b.Venue, b.Instrument, b.ResolutionMinutes, b.BucketStart });
            bar.Property(b => b.Venue).HasMaxLength(64);
            bar.Property(b => b.Instrument).HasMaxLength(32);
            bar.Property(b => b.Open).HasPrecision(18, 8);
            bar.Property(b => b.High).HasPrecision(18, 8);
            bar.Property(b => b.Low).HasPrecision(18, 8);
            bar.Property(b => b.Close).HasPrecision(18, 8);

            // The read pattern indicators and replay will use: one instrument's series over a time range.
            bar.HasIndex(b => new { b.Instrument, b.ResolutionMinutes, b.BucketStart });
        });

        // The embedding store is RELATIONAL-ONLY (gh#109). `Vector` has no mapping on the in-memory provider that
        // every unit test uses, and adding it unconditionally breaks the model for all of them — not just the
        // embedding tests. Excluding it there is the honest shape: a vector column cannot exist without pgvector,
        // so this entity is exercisable only against real Postgres, and its coverage is integration-tier.
        //
        // Deliberately NOT solved with a float[] value converter: that would let the in-memory provider pretend
        // to store embeddings while the similarity operators — the entire point — silently do not exist.
        if (Database.IsNpgsql())
        {
            ConfigureEmbeddings(modelBuilder);
        }
        else
        {
            modelBuilder.Ignore<EmbeddingRecord>();
        }

        modelBuilder.Entity<NotificationOutboxRecord>(outbox =>
        {
            // A SURROGATE key, not the dedup key (gh#458). Idempotence still belongs in the database rather than
            // in the relay remembering to check -- but its scope is the OPEN incident, not all history. The dedup
            // key was the primary key, and since delivery is marked by stamping DeliveredAt and keeping the row, a
            // delivered row held its key forever: the second occurrence of any incident could never be inserted,
            // so a repeat auto-flatten failure went unreported BECAUSE the first one was reported.
            outbox.HasKey(o => o.Id);
            outbox.Property(o => o.DedupKey).HasMaxLength(256);
            outbox.Property(o => o.Title).HasMaxLength(256);
            outbox.Property(o => o.Body).HasMaxLength(4000);

            // The idempotence guard, scoped to what is still owed: re-raising an outstanding incident collides,
            // re-raising a closed one does not. A partial index, so delivered history costs nothing to keep.
            outbox.HasIndex(o => o.DedupKey).IsUnique().HasFilter("\"DeliveredAt\" IS NULL");

            // The relay reads exactly one shape: what is still owed, oldest first.
            outbox.HasIndex(o => new { o.DeliveredAt, o.CreatedAt });

            outbox.ToTable(table =>
                table.HasCheckConstraint("CK_NotificationOutbox_Severity_NotUnknown", "\"Severity\" <> 0"));
        });

        modelBuilder.Entity<NewsRecord>(news =>
        {
            // The DedupKey (a canonicalized URL, NewsDedupKey) IS the idempotence guard, DB-enforced rather than
            // trusted to the writer: the same story from Finnhub and Tiingo collapses to one row, and an
            // overlapping re-poll updates in place instead of duplicating -- exactly as the bar composite key
            // works, for news. Capped so the natural key stays inside the btree index bound; a canonical key
            // (host + path, tracking stripped, no scheme) is short in practice.
            news.HasKey(n => n.DedupKey);
            news.Property(n => n.DedupKey).HasMaxLength(512);
            news.Property(n => n.Type).HasMaxLength(32);
            news.Property(n => n.Url).HasMaxLength(2048);
            news.Property(n => n.Title).HasMaxLength(1024);

            // The read pattern relevance (gh#359) and the co-pilot will use: recent news by publication time.
            news.HasIndex(n => n.PublishedAt);

            // The relevance pass reads rows needing resolution: never-resolved (null) or below the current config
            // generation. Indexed on the version, which is what the predicate filters on (gh#418).
            news.HasIndex(n => n.RelevanceVersion);
        });

        modelBuilder.Entity<TickerInstrumentMap>(map =>
        {
            // The pair is the key -- a ticker maps to several instruments and several tickers to one.
            map.HasKey(m => new { m.Ticker, m.Instrument });
            map.Property(m => m.Ticker).HasMaxLength(32);
            map.Property(m => m.Instrument).HasMaxLength(32);
        });

        modelBuilder.Entity<NewsTopic>(topic =>
        {
            topic.HasKey(t => t.Id);
            topic.Property(t => t.Name).HasMaxLength(64);
            topic.Property(t => t.Instrument).HasMaxLength(32);
            topic.HasIndex(t => t.Name).IsUnique();

            topic.ToTable(table =>
            {
                // Refusable zero: a topic can never be stored with an unset scope, so it can't silently attach
                // news to an instrument.
                table.HasCheckConstraint("CK_NewsTopics_Scope_NotUnknown", "\"Scope\" <> 0");
            });
        });

        modelBuilder.Entity<RelevanceConfigState>(state =>
        {
            state.HasKey(s => s.Id);
        });

        modelBuilder.Entity<SoftSignalFeedback>(feedback =>
        {
            feedback.HasKey(f => f.Id);

            // Matches NewsRecord.DedupKey -- the item this feedback rates, referenced by key (no navigation) to keep
            // the per-user table decoupled from the global news store.
            feedback.Property(f => f.NewsDedupKey).HasMaxLength(512);

            // TWO independent axes, TWO filtered unique indexes (gh#762, correcting the gh#27 single index). Importance
            // (Star=1 / Mute=2) and direction (ThumbsUp=3 / ThumbsDown=4) each hold at MOST ONE row per (operator,
            // item) -- but INDEPENDENTLY, so an operator may hold an importance AND a direction row on the same item
            // (an important AND bearish story). A single (UserId, NewsDedupKey) index would force one axis to overwrite
            // the other. Each leads with UserId so it also serves the per-operator profile read (all of an owner's
            // feedback); the R-20 default-deny filter (TenantDbContext) already scopes every read to the owner. The
            // filters pin the integer kind values -- SoftSignalKind.Axis is the source of truth for the split.
            feedback.HasIndex(f => new { f.UserId, f.NewsDedupKey }, "UX_SoftSignalFeedback_Importance")
                .IsUnique()
                .HasFilter("\"Kind\" IN (1, 2)");
            feedback.HasIndex(f => new { f.UserId, f.NewsDedupKey }, "UX_SoftSignalFeedback_Direction")
                .IsUnique()
                .HasFilter("\"Kind\" IN (3, 4)");

            feedback.ToTable(table =>
            {
                // Refusable zero (gh#60 pattern): a feedback row can never be stored with an unset kind -- it must be
                // a real kind on one of the two axes (star/mute or 👍/👎), never a no-op sitting in the store.
                table.HasCheckConstraint("CK_SoftSignalFeedback_Kind_NotUnknown", "\"Kind\" <> 0");
            });
        });

        modelBuilder.Entity<Event>(evt =>
        {
            // Sequence is the LOGICAL key (identity, totally orders the log). Physically the AddEventBackbone
            // migration drops the PK before hypertable conversion -- a hypertable's unique constraints must
            // include the time dimension, and an append-only log EF never updates needs no DB-enforced key;
            // the identity generator supplies uniqueness.
            evt.HasKey(e => e.Sequence);
            evt.Property(e => e.Sequence).UseIdentityByDefaultColumn();
            evt.Property(e => e.Type).HasMaxLength(128);
            evt.Property(e => e.Source).HasMaxLength(128);
            evt.Property(e => e.Payload).HasColumnType("jsonb");
            evt.Property(e => e.TraceParent).HasMaxLength(64); // W3C traceparent is 55 chars
        });

        modelBuilder.Entity<EventCursor>(cursor =>
        {
            cursor.HasKey(c => c.ConsumerGroup);
            cursor.Property(c => c.ConsumerGroup).HasMaxLength(128);
        });

        modelBuilder.Entity<KillSwitchState>(kill =>
        {
            kill.HasKey(k => k.Id);
            kill.Property(k => k.Reason).HasMaxLength(512);
        });

        modelBuilder.Entity<AuditRecord>(audit =>
        {
            audit.Property(a => a.Before).HasMaxLength(32);
            audit.Property(a => a.After).HasMaxLength(32);
            audit.Property(a => a.Detail).HasMaxLength(512);

            // The audit reads chronologically per operator.
            audit.HasIndex(a => new { a.UserId, a.RecordedAt });

            // StopPlanId is a SOFT reference (no FK): the audit is immutable and must survive the stop it records,
            // so a downstream cascade never rewrites it -- unlike GateDecision's set-null order link.

            audit.ToTable(table =>
            {
                // Fail-closed zero, mirroring the other refusable enums (gh#60): an unset action or placement is
                // never stored, so a row can never read as an audited event that did not happen.
                table.HasCheckConstraint("CK_AuditRecords_Action_NotUnknown", "\"Action\" <> 0");
                table.HasCheckConstraint("CK_AuditRecords_Placement_NotUnknown", "\"Placement\" <> 0");

                // Source is nullable (the stop-plan actions carry none), but a PRESENT source is never the
                // refusable zero (gh#765) — same fail-closed convention, guarded for null.
                table.HasCheckConstraint("CK_AuditRecords_Source_NotUnknown", "\"Source\" IS NULL OR \"Source\" <> 0");

                // Source ↔ Action correlation, enforced below the model (gh#765 review — a core tenet): a safety
                // action (kill-switch engage / disengage = 5 / 6, auto-flatten = 7) MUST carry a trigger source, and
                // the stop-plan lifecycle actions (1–4) MUST leave it null. A future write site that forgets to stamp
                // the source on a kill row is refused here, not left recording a safety action with an unknowable
                // trigger — the exact defeat-the-column bug the unit tests (in-memory, no CHECK) cannot catch.
                table.HasCheckConstraint(
                    "CK_AuditRecords_Source_MatchesAction", "(\"Action\" IN (5, 6, 7)) = (\"Source\" IS NOT NULL)");
            });
        });

        modelBuilder.Entity<Conversation>(conversation =>
        {
            conversation.Property(c => c.Title).HasMaxLength(Conversation.TitleMaxLength);

            // The conversation-list read (gh#18): an operator's conversations, most-recent activity first.
            conversation.HasIndex(c => new { c.UserId, c.UpdatedAt });
        });

        modelBuilder.Entity<ChatMessage>(message =>
        {
            message.Property(m => m.Content).HasMaxLength(ChatMessage.ContentMaxLength);

            message.HasOne<Conversation>()
                .WithMany()
                .HasForeignKey(m => m.ConversationId)
                .OnDelete(DeleteBehavior.Cascade);

            // A message's position in the thread is unique -- two messages cannot share a sequence, and the
            // conversation reads in this order. The unique index is both the ordering-integrity guard and the read.
            message.HasIndex(m => new { m.ConversationId, m.Sequence })
                .IsUnique()
                .HasDatabaseName("IX_ChatMessages_ConversationId_Sequence");

            message.ToTable(table =>
            {
                // Fail-closed zero (gh#60): a message always has a real author, and a real position in the thread.
                table.HasCheckConstraint("CK_ChatMessages_Role_NotUnknown", "\"Role\" <> 0");
                table.HasCheckConstraint("CK_ChatMessages_Sequence_Positive", "\"Sequence\" > 0");
            });
        });
    }

    /// <summary>
    /// The embedding store (gh#109) — split out because it is applied <b>conditionally</b>: <c>Vector</c> has no
    /// mapping on the in-memory provider, so this runs only against a relational (Npgsql) model.
    /// </summary>
    /// <param name="modelBuilder">The model builder.</param>
    private static void ConfigureEmbeddings(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<EmbeddingRecord>(embedding =>
        {
            // Owner kind + id + model IS the key, so re-embedding an owner UPDATES rather than appends -- the
            // same DB-enforced idempotence the bar and news stores use. Model is in the key on purpose: vectors
            // from different models are not comparable, so they must coexist as separate rows rather than one
            // silently overwriting the other and leaving a mixed-model index nobody can trust.
            embedding.HasKey(e => new { e.OwnerKind, e.OwnerId, e.Model });
            embedding.Property(e => e.OwnerId).HasMaxLength(512);
            embedding.Property(e => e.Model).HasMaxLength(128);
            embedding.Property(e => e.ContentHash).HasMaxLength(64);

            // The width is fixed at the column because an ANN index requires it. Cohere's embed-v3 family is
            // 1024; a different width is a different column and therefore a migration, which is the honest
            // consequence of indexing vectors at all.
            embedding.Property(e => e.Embedding).HasColumnType($"vector({EmbeddingDimensions})");

            // Retrieval filters by owner kind before ranking -- "the nearest news item", not "the nearest
            // anything" -- so the filter column leads.
            embedding.HasIndex(e => new { e.OwnerKind, e.RecordedAt });
        });
    }
}
