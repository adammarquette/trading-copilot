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
    }
}
