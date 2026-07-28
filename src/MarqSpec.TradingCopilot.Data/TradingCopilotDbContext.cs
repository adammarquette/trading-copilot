using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Data.Tenancy;
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

    /// <summary>Journaled orders — the journal spine (gh#7). Operator-owned; mode-guarded (R-14).</summary>
    public DbSet<Order> Orders => Set<Order>();

    /// <summary>Native executions of orders. Operator-owned; mode inherited through the order.</summary>
    public DbSet<Fill> Fills => Set<Fill>();

    /// <summary>Journaled trades — round-trip outcomes. Operator-owned.</summary>
    public DbSet<Trade> Trades => Set<Trade>();

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
    /// The raw news / soft-signal store of record (gh#358, R-2) — deduped across sources, the reference template
    /// for non-market feeds. Shared / global reference data (R-20) like the bars; the per-user salience that
    /// reweights it (gh#27) is a separate operator-owned entity, not this row.
    /// </summary>
    public DbSet<NewsRecord> News => Set<NewsRecord>();

    /// <summary>Per-consumer-group replay cursors over the event log (ADR-0001). System plumbing.</summary>
    public DbSet<EventCursor> EventCursors => Set<EventCursor>();

    /// <summary>The durable kill-switch state (gh#189) — one row, rehydrated at startup so the lock survives a restart.</summary>
    public DbSet<KillSwitchState> KillSwitchStates => Set<KillSwitchState>();

    /// <summary>The append-only audit trail (ADR-0007, gh#220) — safety-relevant transitions, immutable. Operator-owned.</summary>
    public DbSet<AuditRecord> AuditRecords => Set<AuditRecord>();

    /// <summary>Operator-authored deterministic indicator triggers (gh#385, A1). Operator-owned; the periodic scan evaluates them.</summary>
    public DbSet<Trigger> Triggers => Set<Trigger>();

    /// <summary>The append-only journal of trigger firings (gh#385) — one row per crossing. Operator-owned.</summary>
    public DbSet<TriggerFiringRecord> TriggerFirings => Set<TriggerFiringRecord>();

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

            // The R-14 persistence guard, half one: Undeclared (0) and an unset state are refused outright --
            // nothing is ever suggested on an undeclared account. Half two (mode must equal the account's
            // persisted mode) is a cross-table rule a single-row CHECK cannot express; it lives in the
            // enforce_mode_matches_account constraint trigger added by the AddExecutionJournal migration.
            suggestion.ToTable(table =>
            {
                table.HasCheckConstraint("CK_Suggestions_Mode_NotUndeclared", "\"Mode\" <> 0");
                table.HasCheckConstraint("CK_Suggestions_State_NotUnknown", "\"State\" <> 0");
                table.HasCheckConstraint("CK_Suggestions_Size_Positive", "\"Size\" > 0");
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

            // Mode is a journal fact (practice results never blend into live results) -- check-constrained,
            // but NOT trigger-guarded: a trade closes after placement, when the declaration may legitimately
            // have moved on. The placement-time guard lives on Orders and Suggestions.
            trade.ToTable(table =>
            {
                table.HasCheckConstraint("CK_Trades_Mode_NotUndeclared", "\"Mode\" <> 0");
                table.HasCheckConstraint("CK_Trades_Size_Positive", "\"Size\" > 0");
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
            });
        });

        modelBuilder.Entity<Trigger>(trigger =>
        {
            trigger.Property(t => t.Instrument).HasMaxLength(32);
            trigger.Property(t => t.Indicator).HasMaxLength(32);
            trigger.Property(t => t.Threshold).HasPrecision(18, 8);

            // The scan reads one operator's enabled triggers at a time (per-owner, R-20).
            trigger.HasIndex(t => new { t.UserId, t.Enabled });

            trigger.ToTable(table =>
            {
                // Refusable zeros (the gh#60 / gh#176 pattern): a comparison or route can never be stored unset, so
                // a trigger can never silently satisfy on a default, nor route nowhere. ArmState.Unseeded (0) IS a
                // valid state, so it is deliberately not constrained. Period/resolution mirror the domain guard.
                table.HasCheckConstraint("CK_Triggers_Comparison_NotUnknown", "\"Comparison\" <> 0");
                table.HasCheckConstraint("CK_Triggers_Route_NotUnknown", "\"Route\" <> 0");
                table.HasCheckConstraint("CK_Triggers_Period_Positive", "\"Period\" > 0");
                table.HasCheckConstraint("CK_Triggers_Resolution_Positive", "\"ResolutionMinutes\" > 0");
            });
        });

        modelBuilder.Entity<TriggerFiringRecord>(firing =>
        {
            firing.Property(f => f.Instrument).HasMaxLength(32);
            firing.Property(f => f.Indicator).HasMaxLength(32);
            firing.Property(f => f.Threshold).HasPrecision(18, 8);
            firing.Property(f => f.Value).HasPrecision(18, 8);

            // The journal reads chronologically per operator.
            firing.HasIndex(f => new { f.UserId, f.FiredAt });

            // The incident is unique per (trigger, cycle) -- the durable dedup ledger behind the scan's
            // commit-then-notify: two committed firings for one crossing are impossible by construction, and the
            // DB enforces it so a retry or a race can never journal (or alert) the same incident twice.
            firing.HasIndex(f => new { f.TriggerId, f.ArmCycle }).IsUnique();

            // TriggerId is a SOFT reference (no FK): the firing is append-only and must survive the trigger it
            // records being edited or deleted -- the AuditRecord pattern.

            firing.ToTable(table =>
            {
                table.HasCheckConstraint("CK_TriggerFirings_Comparison_NotUnknown", "\"Comparison\" <> 0");
            });
        });
    }
}
