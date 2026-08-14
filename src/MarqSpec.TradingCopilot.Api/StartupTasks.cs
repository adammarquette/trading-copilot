using MarqSpec.TradingCopilot.Api.Ai;
using MarqSpec.TradingCopilot.Api.Auth;
using MarqSpec.TradingCopilot.Api.Kill;
using MarqSpec.TradingCopilot.Api.Recovery;
using MarqSpec.TradingCopilot.Api.Suggestions;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace MarqSpec.TradingCopilot.Api;

/// <summary>Startup work run once when the host boots: apply EF migrations and bootstrap the operator.</summary>
public static class StartupTasks
{
    /// <summary>
    /// Applies pending migrations, then bootstraps the operator from <c>Bootstrap:Email</c> /
    /// <c>Bootstrap:Password</c>: seeds them when absent, and — only behind the explicit
    /// <c>Bootstrap:ResetPassword</c> flag — resets an existing operator's password from the environment
    /// (the recovery path; R-18, ADR-0017 operator lifecycle).
    /// </summary>
    /// <param name="app">The web application.</param>
    /// <returns>A task that completes when startup work is done.</returns>
    public static async Task MigrateAndBootstrapAsync(WebApplication app)
    {
        using IServiceScope scope = app.Services.CreateScope();
        TradingCopilotDbContext database = scope.ServiceProvider.GetRequiredService<TradingCopilotDbContext>();
        if (database.Database.IsRelational())
        {
            await database.Database.MigrateAsync();
        }
        else
        {
            await database.Database.EnsureCreatedAsync();
        }

        // Probe for pgvector (gh#474), AFTER the migration that would have installed it. AddEmbeddingStore is
        // deliberately conditional -- on a Postgres without the extension it skips the table and starts anyway,
        // because an unavailable retrieval feature must never keep the safety-critical auto-flatten from running
        // (R-13). Its remarks promised the absence would be DECLARED; this is the half that was missing, so a
        // keyed deployment there stops embedding through Cohere on every poll and faulting at the upsert.
        //
        // Non-relational (the InMemory test hosts) reports absent, which is the honest answer: EmbeddingRecord is
        // Ignore()d in that model, so there is genuinely nowhere to store a vector.
        bool pgVectorPresent = database.Database.IsRelational() && await HasPgVectorAsync(database);

        // gh#858: MigrateAsync above opened this process's FIRST connection on its single (EF-owned) NpgsqlDataSource
        // to run AddEmbeddingStore's CREATE EXTENSION vector -- so Npgsql cached its per-data-source type catalog from
        // BEFORE 'vector' existed, and nothing reloaded it. Every later Vector-valued parameter (NewsEmbeddingService's
        // upsert gh#377, the gh#852 similarity read) would then bind against that stale cache and throw "Cannot resolve
        // 'vector'" for the process's life -- but only on a genuinely fresh database where the extension is created
        // in-process (a long-lived DB the extension predates is fine, which is why it went unnoticed). Flush the catalog
        // now; the reload is on this connection's connection string, so pooled connections re-opened downstream pick up
        // the just-created type. Gated on presence, so the gh#109 absent-extension degrade is untouched -- and a
        // non-relational InMemory host has no NpgsqlConnection to reload.
        if (pgVectorPresent)
        {
            try
            {
                await database.Database.OpenConnectionAsync();
                try
                {
                    await ((NpgsqlConnection)database.Database.GetDbConnection()).ReloadTypesAsync();
                }
                finally
                {
                    await database.Database.CloseConnectionAsync();
                }
            }
            catch (Exception error)
            {
                // gh#109 / R-13: this reload is the ONE throw-capable retrieval step in startup, and a retrieval fault
                // must NEVER keep the safety-critical auto-flatten from running. If the catalog cannot be reloaded,
                // degrade to "pgvector unavailable" -- IsAvailable goes false so the embed pass skips cleanly rather
                // than faulting per poll (the state the data dictionary already promises) -- and let the host come up.
                app.Logger.LogError(
                    error,
                    "pgvector type-catalog reload failed after the in-process CREATE EXTENSION; degrading retrieval to "
                        + "unavailable so startup is not blocked and auto-flatten still runs (gh#858, gh#109, R-13).");
                pgVectorPresent = false;
            }
        }

        // Record the FINAL retrieval state (gh#109): present + catalog reloaded, or degraded-off when the reload could
        // not run -- so an embedding-path fault can never take the safety-critical auto-flatten down with it.
        app.Services.GetRequiredService<VectorStore>().Record(pgVectorPresent);

        // Rehydrate the kill switch (gh#189): if it was engaged when the process last stopped, come up engaged --
        // the operator's lock persists across a restart, so a crash or redeploy never silently re-enables trading
        // (ADR-0013, "nothing silently resumes").
        KillSwitchState? killState = await database.KillSwitchStates.FirstOrDefaultAsync();
        if (killState is { Engaged: true })
        {
            app.Services.GetRequiredService<KillSwitch>()
                .Engage(killState.Mode, killState.EngagedAt ?? DateTimeOffset.UtcNow, killState.Reason);
        }

        await BootstrapOperatorAsync(
            database,
            scope.ServiceProvider.GetRequiredService<IPasswordHasher>(),
            app.Configuration["Bootstrap:Email"],
            app.Configuration["Bootstrap:Password"],
            resetPassword: bool.TryParse(app.Configuration["Bootstrap:ResetPassword"], out bool reset) && reset);

        // Recovery expiry (gh#545, ADR-0013): run the SAME expire transition the steady-state sweep runs, BEFORE the
        // rehydrator counts -- so a suggestion whose validity window passed while the process was down is voided, not
        // reported as active. The first pass after any restart IS the recovery catch-up; there is no separate
        // resurrection code (ADR-0013's "expire + re-validate, never auto-resume"). No at-risk state is touched, and
        // an expired suggestion never trips the consistency fail-safe (a suggestion carries no risk).
        DateTimeOffset now = DateTimeOffset.UtcNow;
        // ExpireDueAsync is a guarded ExecuteUpdate, which only a RELATIONAL provider runs -- the same IsRelational
        // split the migrate / create branch above already makes. On a non-relational host (an in-memory test) there
        // is nothing to sweep, so the recovery count is simply zero; the rehydration below still runs (its reads are
        // provider-agnostic). This keeps the ExecuteUpdate off any in-memory path structurally, not by assumption.
        // The recovery pass needs only the COUNT (carried into the rehydrator's report). It does not push a realtime
        // signal: no operator connection exists during startup, and the card surface loads current state over REST on
        // connect (gh#718's push serves the STEADY-STATE sweep + drift, where a client is already listening).
        int expiredOnRecovery = database.Database.IsRelational()
            ? (await scope.ServiceProvider.GetRequiredService<ISuggestionExpiry>().ExpireDueAsync(now, CancellationToken.None)).Count
            : 0;

        // Rehydrate the wider decision surface (gh#221): bring it back visibly and INERTLY -- observe what returned
        // (staged orders, pending conditionals, hidden stops, active suggestions) without resuming any of it, and if
        // a crash left an IMPOSSIBLE combination, fail safe to no-new-orders (the kill switch, HaltOnly) and loud,
        // never silently repairing (ADR-0013). Runs after the kill-switch rehydration above, so an operator lock
        // already in force is seen and preserved rather than overwritten; the recovery-expire count is carried in.
        await scope.ServiceProvider.GetRequiredService<DecisionStateRehydrator>()
            .RehydrateAsync(now, expiredOnRecovery, CancellationToken.None);
    }

    /// <summary>
    /// Seeds the operator when no user with <paramref name="email"/> exists; with
    /// <paramref name="resetPassword"/> set, re-hashes <paramref name="password"/> onto the <b>existing</b> user
    /// instead.
    /// </summary>
    /// <param name="database">The application database.</param>
    /// <param name="passwordHasher">The credential hasher.</param>
    /// <param name="email">The bootstrap email; nothing happens when absent.</param>
    /// <param name="password">The bootstrap password; nothing happens when absent.</param>
    /// <param name="resetPassword">
    /// The explicit recovery opt-in. The operator controls the deployment, so the environment may deliberately
    /// reset the credential — but never silently: without this flag a stale env password cannot overwrite the
    /// stored one. The reset keeps the <b>same user row and id</b>, because a reseeded user with a new id would
    /// strand every R-20-scoped row in the workspace behind the default-deny filter.
    /// </param>
    /// <returns>A task that completes when the operator is bootstrapped.</returns>
    internal static async Task BootstrapOperatorAsync(
        TradingCopilotDbContext database,
        IPasswordHasher passwordHasher,
        string? email,
        string? password,
        bool resetPassword)
    {
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        {
            return;
        }

        User? existing = await database.Users.FirstOrDefaultAsync(user => user.Email == email);

        if (existing is not null)
        {
            if (resetPassword)
            {
                existing.PasswordHash = passwordHasher.Hash(password);
                await database.SaveChangesAsync();
            }

            return;
        }

        database.Users.Add(new User
        {
            Id = Guid.NewGuid(),
            Email = email,
            PasswordHash = passwordHasher.Hash(password),
            DisplayName = "Operator",
            Status = UserStatus.Active,
            CreatedUtc = DateTimeOffset.UtcNow,
            // The bootstrap account is THE primary operator (ADR-0017) -- the only account that may issue
            // invitations (gh#128). Declared here, never derived from creation order.
            IsPrimaryOperator = true,
        });
        await database.SaveChangesAsync();
    }

    /// <summary>Whether the <c>pgvector</c> extension is installed on this database (gh#474).</summary>
    /// <remarks>
    /// Reads <c>pg_extension</c> — installed, not merely <i>installable</i>. <c>AddEmbeddingStore</c> gates on
    /// <c>pg_available_extensions</c> and then runs <c>CREATE EXTENSION</c>, so post-migration the two agree; but
    /// available-and-not-installed is exactly the state where the table was skipped, and that must read as absent.
    /// <b>Fails closed:</b> a probe that cannot answer reports absent rather than assuming the happy case.
    /// </remarks>
    private static async Task<bool> HasPgVectorAsync(TradingCopilotDbContext database)
    {
        try
        {
            return await database.Database
                .SqlQuery<int>($"SELECT 1 AS \"Value\" FROM pg_extension WHERE extname = 'vector'")
                .AnyAsync();
        }
        catch (Exception)
        {
            // A provider that cannot answer does not have the extension for our purposes. The caller only ever
            // SPENDS on a true, so the safe direction is unambiguous.
            return false;
        }
    }
}
