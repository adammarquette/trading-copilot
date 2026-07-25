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
using MarqSpec.TradingCopilot.Api.Orders;
using MarqSpec.TradingCopilot.Api.Recovery;
using MarqSpec.TradingCopilot.Api.Risk;
using MarqSpec.TradingCopilot.Api.Venues;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Events;
using MarqSpec.TradingCopilot.Data.Tenancy;
using MarqSpec.TradingCopilot.Domain.Events;
using MarqSpec.TradingCopilot.Domain.Execution;
using MarqSpec.TradingCopilot.Domain.MarketData;
using MarqSpec.TradingCopilot.Domain.Venue;
using MarqSpec.TradingCopilot.Integration.ProjectX;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

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

// The redundant watchdog (R-13, gh#187, ADR-0013): the INDEPENDENT second tier above the primary scheduler -- a
// SEPARATE host on its own cadence, so the flatten still fires when the primary is degraded (hung host, per-pass
// give-up, transient fault). It shares FlattenOptions (schedule + grace) but has its own loop and its own
// trigger/close logic, so a bug in the primary cannot disable it. Always on, like the primary (R-13).
builder.Services.AddScoped<AutoFlattenWatchdogService>();
builder.Services.AddHostedService<AutoFlattenWatchdogHost>();

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
app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));

app.Run();

/// <summary>The WebApplication entry point for testing integration.</summary>
public partial class Program { }
