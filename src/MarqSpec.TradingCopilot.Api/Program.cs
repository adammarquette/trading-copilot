using System.Text;
using MarqSpec.Client.ProjectX.DependencyInjection;
using MarqSpec.TradingCopilot.Api;
using MarqSpec.TradingCopilot.Api.Accounts;
using MarqSpec.TradingCopilot.Api.Audit;
using MarqSpec.TradingCopilot.Api.Auth;
using MarqSpec.TradingCopilot.Api.Firms;
using MarqSpec.TradingCopilot.Api.Flatten;
using MarqSpec.TradingCopilot.Api.Kill;
using MarqSpec.TradingCopilot.Api.MarketData;
using MarqSpec.TradingCopilot.Api.Notifications;
using MarqSpec.TradingCopilot.Api.Observability;
using MarqSpec.TradingCopilot.Api.Orders;
using MarqSpec.TradingCopilot.Api.Recovery;
using MarqSpec.TradingCopilot.Api.Risk;
using MarqSpec.TradingCopilot.Api.Venues;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Events;
using MarqSpec.TradingCopilot.Data.Tenancy;
using MarqSpec.TradingCopilot.Domain.Events;
using MarqSpec.TradingCopilot.Domain.Execution;
using MarqSpec.TradingCopilot.Domain.Flatten;
using MarqSpec.TradingCopilot.Domain.MarketData;
using MarqSpec.TradingCopilot.Domain.Notifications;
using MarqSpec.TradingCopilot.Domain.Venue;
using MarqSpec.TradingCopilot.Integration.ProjectX;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Npgsql;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// Observability first (gh#230, ADR-0002): wire the SDK before anything else registers, so every signal from
// startup onward is captured. With no exporter configured this is a no-op -- instrumentation must never be able
// to break trading (engineering §9).
builder.AddTradingCopilotTelemetry();

builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection(JwtOptions.SectionName));
JwtOptions jwt = builder.Configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>() ?? new JwtOptions();
if (string.IsNullOrWhiteSpace(jwt.SigningKey))
{
    throw new InvalidOperationException(
        $"Configure '{JwtOptions.SectionName}:SigningKey' via env / user-secrets — it must never live in source.");
}

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUser, HttpContextCurrentUser>();
builder.Services.AddSingleton<IPasswordHasher, PasswordHasher>();
builder.Services.AddSingleton<ITokenIssuer, JwtTokenIssuer>();
builder.Services.AddTradingCopilotData(builder.Configuration.GetConnectionString("Default") ?? string.Empty);

// The ProjectX client (credentials from the "ProjectX" section -- env, never source). One credential set per
// process: the client's websocket is a singleton (ADR-0015); ProjectX:CredentialKey names whose it is, and the
// discovery endpoint refuses a connection whose key this process does not hold.
builder.Services.AddProjectXApiClient(builder.Configuration);
builder.Services.Configure<ProjectXConnectionOptions>(
    builder.Configuration.GetSection(ProjectXConnectionOptions.SectionName));
builder.Services.AddScoped<IProjectXVenueFactory, ProjectXVenueFactory>();

// Venue connection liveness (R-17, gh#209): a process-wide singleton over the venue's websocket client, so the
// orphan guard can watch for a drop. One credential set per process (ADR-0015) -> one connection.
builder.Services.AddSingleton<IVenueConnection, ProjectXConnection>();

// The account-event streaming seam (R-17, gh#219): a process-wide singleton over the venue's user hub, carrying
// order / position / fill events. One credential set per process (ADR-0015) -> one user hub, so a singleton like
// the connection seam above, kept off the scoped ITradingVenue.
builder.Services.AddSingleton<IAccountEventStream, ProjectXAccountEventStream>();

// The event backbone (ADR-0001) behind its seam: today a Timescale hypertable; a future bus is an adapter
// change, not a rewrite. Producers/consumers arrive with market-data ingestion (R-1).
builder.Services.AddScoped<IEventLog, TimescaleEventLog>();

// The backbone's first producer (R-1, gh#13): normalises a venue quote stream into the append-only log, and
// the hosted service that drives it -- a supervised subscription per configured contract. Opt-in: with no
// Ingestion:Symbols configured, the host does nothing, so a run that wants no live feed simply omits them.
builder.Services.AddScoped<QuoteIngestionService>();
builder.Services.Configure<IngestionOptions>(builder.Configuration.GetSection(IngestionOptions.SectionName));
builder.Services.AddHostedService<MarketDataIngestionHost>();

// The event log's first consumer (ADR-0007, gh#153): the stop-promotion watcher reads market.quote events and
// promotes hidden actual stops as price comes within their band. Harmless with no staged stops, so it always runs.
builder.Services.AddScoped<StopPromotionService>();
builder.Services.AddHostedService<StopPromotionHost>();

// The event log's second consumer (ADR-0007, gh#198): the conditional-order firing watcher reads market.quote
// events and fires / cancels / expires pending conditional entries on their trigger. Harmless with none.
builder.Services.AddScoped<ConditionalFiringService>();
builder.Services.AddHostedService<ConditionalOrderHost>();

// The immutable audit trail (engineering §9, ADR-0007, gh#220): a secondary, failure-tolerant write the orphan
// guard uses to record each synthetic-stop transition with its synthetic_risk flag. Scoped alongside the guard.
builder.Services.AddScoped<IAuditLog, AuditLog>();

// Connection-loss orphan handling (ADR-0007, ADR-0013, gh#209): the monitor watches the venue connection and,
// on a drop, orphans the hidden synthetic stops (the native safety stop stays the floor); on reconnect it
// re-arms them. Harmless with no hidden stops, so it always runs.
builder.Services.AddScoped<OrphanGuardService>();
builder.Services.AddHostedService<VenueConnectionMonitorHost>();

// The account-event consumer (R-17, R-11, gh#219): reads order / fill events off the user-hub seam and turns
// venue truth into journal state -- writing Fill rows (the entity's first producer) and advancing an order to
// Filled / PartiallyFilled / Rejected. Before this an order stopped at Working, blind to what the venue did next.
// Harmless with no accounts / no events, so it always runs; a fresh scope per event holds no scoped dep across
// the stream, and the capability is Require'd through the seam at the call (R-17).
builder.Services.AddScoped<AccountEventIngestionService>();

// OCO-cancel-on-exit (R-11, ADR-0007, gh#183): the account-event seam's first real consumer. When the stream
// reports a position flat, it retires the synthetic stop plans and cancels the dangling native protective legs
// (safety bracket, promoted stop, take-profit) -- a dangling safety stop is a live resting order with no position
// behind it. Every exit route (manual flatten, the promoted stop firing, auto-flatten, kill-switch flatten-all)
// reaches it the same way. Resolved per-event by AccountEventStreamHost.
builder.Services.AddScoped<OcoExitService>();
builder.Services.AddHostedService<AccountEventStreamHost>();

// Settlement-boundary position reconcile (R-13, ADR-0013, gh#193): reports positions from venue truth tagged
// with their mark basis (live / settlement re-mark / declared-unknown), so a settlement re-mark is never read
// as live and an unreachable venue is not shown as a stale live view.
builder.Services.AddScoped<PositionReconciliationService>();

// Auto-flatten (R-13, gh#185, ADR-0013): the PRIMARY scheduler that closes open positions at each instrument's
// per-market deadline on the DST-aware market clock. On by default and cannot be silently disabled -- so it
// ALWAYS runs; a market is turned off per-instrument in the Flatten config, not by omitting the host. The
// redundant / independent watchdog above it is gh#187. Validate the schedule at startup so a malformed deadline
// fails fast rather than mid-session, the same fail-fast stance as the Jwt signing key above.
builder.Services.Configure<FlattenOptions>(builder.Configuration.GetSection(FlattenOptions.SectionName));
_ = (builder.Configuration.GetSection(FlattenOptions.SectionName).Get<FlattenOptions>() ?? new FlattenOptions()).ToSchedules();
builder.Services.AddScoped<AutoFlattenService>();
builder.Services.AddHostedService<AutoFlattenHost>();

// ...and REPORT that resolved schedule once the host exists (gh#255): which markets are armed, at what deadline,
// and -- the load-bearing part -- whether each came from configuration or the built-in default. Validating alone
// left the safety path silent about its own configuration, so a DROPPED override was indistinguishable from an
// intended default; that is precisely how gh#236 hid. Singleton: it reads fixed configuration and holds no state.
builder.Services.AddSingleton<FlattenScheduleReporter>();

// The redundant watchdog (R-13, gh#187, ADR-0013): the INDEPENDENT second tier above the primary scheduler -- a
// SEPARATE host on its own cadence, so the flatten still fires when the primary is degraded (hung host, per-pass
// give-up, transient fault). It shares FlattenOptions (schedule + grace) but has its own loop and its own
// trigger/close logic, so a bug in the primary cannot disable it. Always on, like the primary (R-13).
builder.Services.AddScoped<AutoFlattenWatchdogService>();
builder.Services.AddHostedService<AutoFlattenWatchdogHost>();

// The notification channel (gh#243, ADR-0019): LAYER 1 of alerting -- the push the app sends itself, because it
// knows first. Routing a P1 through a metrics scrape and a rule evaluation would cost tens of seconds on a path
// where CL and GC have ~15 minutes of margin in total. Pushover because an on-call-of-one needs a PAGER: its
// Emergency priority is the only one that repeats until ACKNOWLEDGED and bypasses Do Not Disturb.
//
// Registered as a SINGLETON: the dedup decorator holds the open-incident set, and a scoped one would forget it
// every pass -- turning "one page per incident" into one page per 15-second poll. Unconfigured falls back to a
// channel that logs what it would have sent, so a fork or a fresh deployment still boots (loudly, never silently).
builder.Services.Configure<PushoverOptions>(builder.Configuration.GetSection(PushoverOptions.SectionName));
builder.Services.AddHttpClient<PushoverNotificationChannel>(
    client => { client.Timeout = TimeSpan.FromSeconds(10); });
builder.Services.AddSingleton<INotificationChannel>(provider =>
{
    PushoverOptions pushover = provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<PushoverOptions>>().Value;
    INotificationChannel inner = pushover.IsConfigured
        ? provider.GetRequiredService<PushoverNotificationChannel>()
        : provider.GetRequiredService<NullNotificationChannel>();

    return new DedupingNotificationChannel(inner, provider.GetRequiredService<ILogger<DedupingNotificationChannel>>());
});
builder.Services.AddSingleton<NullNotificationChannel>();

// The dead-man's switch (R-13, gh#244, ADR-0019): the THIRD tier, and the only one that lives OUTSIDE this
// process. Both tiers above die with the host -- so if it dies before a deadline, the flatten never fires and
// nothing alerts. This inverts that: the app reports flat to an external monitor, which pages when the report
// FAILS TO ARRIVE. Silence becomes the alarm. Validate the check URLs at startup so a malformed one fails fast
// rather than at 14:35; an UNCONFIGURED switch is allowed but warns loudly from the host (never silent).
builder.Services.Configure<CheckInOptions>(builder.Configuration.GetSection(CheckInOptions.SectionName));
CheckInOptions checkIn = builder.Configuration.GetSection(CheckInOptions.SectionName).Get<CheckInOptions>() ?? new CheckInOptions();
_ = checkIn.HeartbeatUri;
foreach (FlattenSchedule schedule in (builder.Configuration.GetSection(FlattenOptions.SectionName).Get<FlattenOptions>() ?? new FlattenOptions()).ToSchedules())
{
    _ = checkIn.UrlFor(schedule.Instrument);
}

// A short timeout by design: a hung monitor must never become a hung safety path.
builder.Services.AddHttpClient<IDeadMansSwitch, HttpDeadMansSwitch>(
    client => { client.Timeout = TimeSpan.FromSeconds(10); });
builder.Services.AddScoped<FlattenCheckInService>();
builder.Services.AddHostedService<DeadMansSwitchHost>();

// The R-14 environment, mapped ONCE at the composition root from the host (gh#9): practice anywhere, live only
// in production, undeclared nowhere -- and an unrecognised environment name fails closed to Development
// (practice-only). Wrapped so endpoints bind it from services, never from a request.
// The R-16 execution sanity caps -- conservative defaults, overridable via the Execution config section.
builder.Services.Configure<ExecutionOptions>(builder.Configuration.GetSection(ExecutionOptions.SectionName));

builder.Services.AddSingleton(new HostTradingEnvironment(
    DeploymentEnvironmentMapping.From(builder.Environment.EnvironmentName)));

// The kill switch (R-11, ADR-0007, gh#189): a process-wide flag the enforcing send path reads to refuse every
// outbound order while engaged. A singleton (the mutable runtime state) exposed through its domain reader
// interface, so OrderExecutionService stays pure. The persisted KillSwitchState row rehydrates it at startup, so
// the operator's lock survives a restart -- nothing silently re-enables trading (ADR-0013).
builder.Services.AddSingleton<KillSwitch>();
builder.Services.AddSingleton<IKillSwitch>(services => services.GetRequiredService<KillSwitch>());
builder.Services.AddScoped<KillSwitchService>();

// Decision-state rehydration (R-20, R-12, ADR-0013, gh#221): an explicit startup pass that reads the decision
// surface back inertly (nothing resumes) and, on an IMPOSSIBLE combination a crash left, fails safe to no-new-
// orders (the kill switch, HaltOnly) and loud -- never silently repairing. Scoped: it runs in the startup scope
// alongside migrate + bootstrap.
builder.Services.AddScoped<DecisionStateRehydrator>();

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwt.Issuer,
            ValidateAudience = true,
            ValidAudience = jwt.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.SigningKey)),
            ValidateLifetime = true,
        };
    });
builder.Services.AddAuthorization();

WebApplication app = builder.Build();

// Report the armed flatten schedule FIRST (gh#255, R-13) -- before migration, so the operator still sees which
// deadlines are configured even on a start that later fails to reach the database.
app.Services.GetRequiredService<FlattenScheduleReporter>().Report(DateTimeOffset.UtcNow);

await StartupTasks.MigrateAndBootstrapAsync(app);

app.UseAuthentication();
app.UseAuthorization();
app.MapAuthEndpoints();
app.MapFirmEndpoints();
app.MapConnectionEndpoints();
app.MapAccountEndpoints();
app.MapRiskEndpoints();
app.MapOrderEndpoints();
app.MapKillSwitchEndpoints();
app.MapPositionEndpoints();
// Liveness: answers from the process alone and touches NO dependency (§7). A liveness probe that queries the
// database restarts a healthy app during a database blip -- taking the auto-flatten scheduler down with it.
app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));

// Readiness: the opposite contract -- it MUST touch the database, because "ready" means ready to serve. A failure
// takes this instance out of rotation without killing it, which is the right response to an unreachable database.
app.MapGet("/ready", async (TradingCopilotDbContext database, CancellationToken cancellationToken) =>
{
    try
    {
        return await database.Database.CanConnectAsync(cancellationToken)
            ? Results.Ok(new { status = "ready" })
            : Results.Json(new { status = "not-ready", reason = "database unreachable" }, statusCode: 503);
    }
    catch (Exception error) when (error is InvalidOperationException or NpgsqlException or TimeoutException)
    {
        return Results.Json(new { status = "not-ready", reason = "database unreachable" }, statusCode: 503);
    }
});

app.Run();

/// <summary>The WebApplication entry point for testing integration.</summary>
public partial class Program { }
