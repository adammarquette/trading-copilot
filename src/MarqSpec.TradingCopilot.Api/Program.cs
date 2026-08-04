using System.Text;
using MarqSpec.Client.Finnhub;
using MarqSpec.Client.ProjectX.DependencyInjection;
using MarqSpec.Client.Tiingo;
using MarqSpec.TradingCopilot.Api;
using MarqSpec.TradingCopilot.Api.Accounts;
using MarqSpec.TradingCopilot.Api.Ai;
using MarqSpec.TradingCopilot.Api.Audit;
using MarqSpec.TradingCopilot.Api.Auth;
using MarqSpec.TradingCopilot.Api.Documentation;
using MarqSpec.TradingCopilot.Api.Firms;
using MarqSpec.TradingCopilot.Api.Flatten;
using MarqSpec.TradingCopilot.Api.Kill;
using MarqSpec.TradingCopilot.Api.MarketData;
using MarqSpec.TradingCopilot.Api.Notifications;
using MarqSpec.TradingCopilot.Api.Observability;
using MarqSpec.TradingCopilot.Api.Orders;
using MarqSpec.TradingCopilot.Api.Recovery;
using MarqSpec.TradingCopilot.Api.Relevance;
using MarqSpec.TradingCopilot.Api.Risk;
using MarqSpec.TradingCopilot.Api.Signals;
using MarqSpec.TradingCopilot.Api.Suggestions;
using MarqSpec.TradingCopilot.Api.Triggers;
using MarqSpec.TradingCopilot.Api.Venues;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Events;
using MarqSpec.TradingCopilot.Data.Tenancy;
using MarqSpec.TradingCopilot.Domain;
using MarqSpec.TradingCopilot.Domain.Ai;
using MarqSpec.TradingCopilot.Domain.Events;
using MarqSpec.TradingCopilot.Domain.Execution;
using MarqSpec.TradingCopilot.Domain.Flatten;
using MarqSpec.TradingCopilot.Domain.MarketData;
using MarqSpec.TradingCopilot.Domain.Notifications;
using MarqSpec.TradingCopilot.Domain.Triggers;
using MarqSpec.TradingCopilot.Domain.Venue;
using MarqSpec.TradingCopilot.Integration.Finnhub;
using MarqSpec.TradingCopilot.Integration.ProjectX;
using MarqSpec.TradingCopilot.Integration.Tiingo;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
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

// Cross-asset CONTEXT prices (gh#496, of gh#411, R-1): equities/indices last-trade prints for correlation --
// SPY against ES, QQQ against NQ. Deliberately its OWN event type and its OWN host: a context source publishes no
// book, so it must never reach `market.quote` (the stream the stop-promotion and conditional-firing watchers act
// on), and an outage on this optional feed must never disturb tradeable ingestion. Opt-in via ContextIngestion:Symbols.
builder.Services.Configure<ContextIngestionOptions>(
    builder.Configuration.GetSection(ContextIngestionOptions.SectionName));
builder.Services.AddScoped<ContextTradeIngestionService>();
builder.Services.AddHostedService<ContextIngestionHost>();

// R-1's SECOND market-data path (gh#302): the clean-historical bar store, filled by periodic REST backfill.
// Kept deliberately apart from the live stream above -- R-1 says they are "stored and treated separately", and
// the historical series, not the live feed, is "the system of record for bars used in journaling and replay".
// It is also what ADR-0001 means by a full indicator rebuild reprocessing the clean historical store rather than
// the 24-hour event log. Opt-in and configured independently of Ingestion:Symbols: an operator may want history
// without a live subscription, or the reverse.
builder.Services.Configure<BarBackfillOptions>(builder.Configuration.GetSection(BarBackfillOptions.SectionName));
builder.Services.AddScoped<BarBackfillService>();
builder.Services.AddHostedService<BarBackfillHost>();

// R-2's news / soft-signal ingestion (gh#358): every registered INewsSource polled into the deduped NewsRecord
// store of record -- the news analogue of the bar store above, collapsed across sources by the dedup key. News is
// deliberately multi-source (Finnhub + Tiingo) where price data is single-source. Opt-in via News:Enabled.
builder.Services.Configure<NewsIngestionOptions>(builder.Configuration.GetSection(NewsIngestionOptions.SectionName));
builder.Services.AddScoped<NewsIngestionService>();
builder.Services.AddHostedService<NewsIngestionHost>();

// The news sources (of gh#383), each registered ONLY when its key is configured -- the same graceful-absence
// pattern as the Cohere provider. An enabled poller with no key for a source simply does not have it; with both
// keys set it fans in Finnhub + Tiingo and dedups across them (R-2). Keys are never in source; they come from
// Finnhub__ApiKey / Tiingo__ApiKey (config/env).
FinnhubOptions finnhubOptions = builder.Configuration.GetSection("Finnhub").Get<FinnhubOptions>() ?? new FinnhubOptions();
if (!string.IsNullOrWhiteSpace(finnhubOptions.ApiKey))
{
    builder.Services.AddSingleton(finnhubOptions);
    builder.Services.AddHttpClient<IFinnhubNewsClient, FinnhubNewsClient>();
    builder.Services.AddScoped<INewsSource, FinnhubNewsSource>();

    // The cross-asset CONTEXT price surface (gh#496, of gh#411) — the same key, a different feed. One multiplexed
    // websocket carries every subscribed symbol and holds the free-tier cap, so the stream is registered once and
    // the adapter subscribes each configured symbol on it.
    builder.Services.AddScoped<IFinnhubQuoteStream>(_ =>
        new FinnhubQuoteStream(() => new ClientWebSocketTransport(), finnhubOptions));
    builder.Services.AddScoped<IContextMarketDataSource, FinnhubMarketDataSource>();
}

TiingoOptions tiingoOptions = builder.Configuration.GetSection("Tiingo").Get<TiingoOptions>() ?? new TiingoOptions();
if (!string.IsNullOrWhiteSpace(tiingoOptions.ApiKey))
{
    builder.Services.AddSingleton(tiingoOptions);
    builder.Services.AddHttpClient<ITiingoNewsClient, TiingoNewsClient>();
    builder.Services.AddScoped<INewsSource, TiingoNewsSource>();
}

// R-2's news relevance resolution (gh#359): materializes matched instruments/topics onto ingested news via the
// deployment's GLOBAL ticker<->instrument maps + topics. Always on like the indicator projection; its work is
// whatever news needs resolving (unresolved, or stale since a config change), so a config edit re-resolves the
// affected news predictably. It maps; the per-user salience over these matches is a separate concern (gh#27).
builder.Services.Configure<NewsRelevanceOptions>(builder.Configuration.GetSection(NewsRelevanceOptions.SectionName));
builder.Services.AddScoped<NewsRelevanceService>();
builder.Services.AddHostedService<NewsRelevanceHost>();

// R-2's per-operator salience over those matches (gh#27, ADR-0014): a star raises, a mute lowers, the salience of
// SIMILAR future news, decayed by recency. Options only -- the personalized feed is computed on read by the
// /api/news endpoints (no host). A soft weight that never reaches the risk gate or order sizing (ADR-0007).
builder.Services.AddOptions<SalienceOptions>()
    .Bind(builder.Configuration.GetSection(SalienceOptions.SectionName))
    // Fail fast on a misconfiguration rather than throwing from Math.Clamp on every feed read. The floor must sit at
    // or below the neutral 1.0 and the cap at or above it, so a cold-start item (raw 1.0) is never clamped off base.
    .Validate(
        options => options.MultiplierFloor > 0 && options.MultiplierFloor <= 1.0 && options.MultiplierCap >= 1.0
            && options.MaxFeedLimit >= 1 && options.DefaultFeedLimit >= 1 && options.DefaultFeedLimit <= options.MaxFeedLimit,
        "Salience: require 0 < MultiplierFloor <= 1 <= MultiplierCap, MaxFeedLimit >= 1, and DefaultFeedLimit in [1, MaxFeedLimit].")
    .ValidateOnStart();

// Per-instrument contract facts (gh#541, R-4/R-16, ADR-0007): tick size, point value and the catastrophic
// safety-stop distance a SERVER-originated proposal needs to become an order ticket. A manual ticket carries these
// on the request because a human authored it; a suggestion has no author to ask, and letting the browser supply them
// would feed client-controlled numbers into the sizing ladder and the fat-finger band. Built-in defaults for the
// products the flatten schedule governs, overridable per instrument; validated on start, because a zero tick size
// would silently mis-size every risk calculation rather than fail.
builder.Services.AddOptions<InstrumentSpecOptions>()
    .Bind(builder.Configuration.GetSection(InstrumentSpecOptions.SectionName))
    .Validate(
        options => options.Validate(),
        "InstrumentSpecs: every configured instrument needs a symbol and a positive TickSize, PointValue and SafetyStopTicks.")
    .ValidateOnStart();
builder.Services.AddSingleton<IInstrumentSpecSource, InstrumentSpecSource>();
// The session-deadline read seam (gh#544): one source of truth for a market's deadline, so the agent-review path
// can RESPECT it without depending on the flatten machinery that ACTS on it (the gh#402 constructor-graph guard).
builder.Services.AddSingleton<ISessionDeadlineSource, SessionDeadlineSource>();

// The suggestion read model's limits (gh#540, R-4). A cap rather than an unbounded list: the suggestion table is an
// append-only journal, so an uncapped page would let one call pull the whole history. Validated on start so a bad
// configuration fails the host once rather than throwing from Math.Clamp on every read (the SalienceOptions idiom).
builder.Services.AddOptions<SuggestionOptions>()
    .Bind(builder.Configuration.GetSection(SuggestionOptions.SectionName))
    .Validate(
        options => options.MaxPageSize >= 1 && options.DefaultPageSize >= 1 && options.DefaultPageSize <= options.MaxPageSize
            // A non-positive validity would emit a suggestion the CK_Suggestions_ExpiresAfterCreated CHECK refuses,
            // so it is caught once at startup rather than on every fire (gh#544).
            && options.ValidityMinutes >= 1
            // A non-positive drift band would collapse the take-time re-check to "any move refuses" (or worse, a
            // negative band that never refuses); caught once at startup rather than at take time (gh#548).
            && options.DriftToleranceTicks >= 1,
        "Suggestions: require MaxPageSize >= 1, DefaultPageSize in [1, MaxPageSize], ValidityMinutes >= 1, and DriftToleranceTicks >= 1.")
    .ValidateOnStart();

// Indicator projections over that store (gh#310, R-1, ADR-0001: "indicators are projections… rebuild = replay").
// ALWAYS runs and needs no symbol list of its own -- its work is whatever the bar store holds, so bars can never
// exist without their indicators. IIndicatorSource is the read seam the promotion watcher will consult (gh#311)
// once the band is resolved by the caller rather than inside StopPlan, which stays pure.
builder.Services.Configure<IndicatorOptions>(builder.Configuration.GetSection(IndicatorOptions.SectionName));
// The bar-derived indicator set (R-22): ATR at the safety band's period + RSI. Built from options in one place
// (IndicatorSet), so the safety band's producer cannot be configured away. A third indicator is one line there.
builder.Services.AddSingleton<IReadOnlyList<IIndicator>>(sp =>
    IndicatorSet.FromOptions(sp.GetRequiredService<IOptions<IndicatorOptions>>().Value));
builder.Services.AddScoped<IndicatorProjectionService>();
builder.Services.AddScoped<IIndicatorSource, StoredIndicatorSource>();
builder.Services.AddHostedService<IndicatorProjectionHost>();

// The read seam over persisted key-level zones (gh#596) that confluence (gh#593) and any chart overlay consult;
// the detector that writes them is gh#597.
builder.Services.AddScoped<IPriceLevelSource, StoredPriceLevelSource>();

// The AI seam for the agent-review route (gh#402, R-4, ADR-0008): the provider-neutral ILlmProvider (a no-I/O stub
// this increment) and the ALWAYS-bound ITriggerReviewer -- the real LlmTriggerReviewer when an Llm:ApiKey is
// configured, else the honest inert NullTriggerReviewer. Enforcement lives below the model: nothing bound here can
// place or size an order. Registered before the trigger scan below, which now depends on ITriggerReviewer.
builder.Services.AddTradingCopilotAi(builder.Configuration);

// The deterministic trigger layer (gh#385, gh#402, R-4 / R-7, ADR-0008): a standing-alert scan over the projected
// indicators above. Each pass evaluates every enabled MECHANICAL and AGENT-REVIEW trigger and fires the crossing
// edges -- a mechanical fire alerts through the notification channel; an agent-review fire wakes the reviewer once,
// stages a Suggestion (never an order), and advises. Reads indicators globally (derived market data), reads/writes
// triggers per-owner (R-20). Harmless with no triggers, so it always runs.
builder.Services.Configure<TriggerOptions>(builder.Configuration.GetSection(TriggerOptions.SectionName));
builder.Services.AddScoped<TriggerEvaluationService>();
builder.Services.AddHostedService<TriggerScanHost>();

// The event log's first consumer (ADR-0007, gh#153): the stop-promotion watcher reads market.quote events and
// promotes hidden actual stops as price comes within their band. Harmless with no staged stops, so it always runs.
builder.Services.AddScoped<StopPromotionService>();

// The recovery path for an event-log retention gap (gh#306): what a blind window is still recoverable from, read
// from the clean-historical bar store rather than the log, which by then no longer has it.
builder.Services.AddScoped<GapBackfillService>();
builder.Services.AddHostedService<StopPromotionHost>();

// The event log's second consumer (ADR-0007, gh#198): the conditional-order firing watcher reads market.quote
// events and fires / cancels / expires pending conditional entries on their trigger. Harmless with none.
builder.Services.AddScoped<ConditionalFiringService>();
builder.Services.AddHostedService<ConditionalOrderHost>();

// The suggestion expire sweep (gh#545, ADR-0013): a time-driven pass that voids every live suggestion past its
// validity window, so a dead suggestion drops off the actionable surface without an operator act. The guarded
// one-way State transition (scoped) is shared with the drift (gh#546) and supersede (gh#550) writers; the startup
// path (StartupTasks) runs the same transition before the rehydrator counts, so recovery and steady state cannot diverge.
builder.Services.AddScoped<ISuggestionExpiry, SuggestionExpiry>();
builder.Services.AddHostedService<SuggestionExpiryHost>();

// The event log's third market.quote consumer (gh#546, R-4 / R-12): the suggestion-drift watcher marks an Active
// suggestion Stale once price drifts past the entry tolerance, so a scratched setup greys out BEFORE execution
// (the take-time re-check, gh#548, is the synchronous backstop). Its guarded Active→Stale update is the sibling of
// the expire sweep's writer above; unlike the other consumers it resolves each Active symbol → contract per pass,
// so it takes a venue. Harmless with no Active suggestions.
builder.Services.AddScoped<ISuggestionDrift, SuggestionDrift>();
builder.Services.AddScoped<SuggestionDriftService>();
builder.Services.AddHostedService<SuggestionDriftHost>();

// The immutable audit trail (engineering §9, ADR-0007, gh#220): a secondary, failure-tolerant write the orphan
// guard uses to record each synthetic-stop transition with its synthetic_risk flag. Scoped alongside the guard.
builder.Services.AddScoped<IAuditLog, AuditLog>();

// Connection-loss orphan handling (ADR-0007, ADR-0013, gh#209): the monitor watches the venue connection and,
// on a drop, orphans the hidden synthetic stops (the native safety stop stays the floor); on reconnect it
// re-arms them. Harmless with no hidden stops, so it always runs.
builder.Services.AddScoped<OrphanGuardService>();
builder.Services.AddHostedService<VenueConnectionMonitorHost>();

// The protection census (gh#370, ADR-0019): periodically reconciles venue POSITIONS against venue WORKING ORDERS
// and publishes how many live positions have no protective stop resting at the exchange. ADR-0019 makes that a
// P1, and until now nothing measured it -- so the rule gh#245 wanted could not be written. Measurement only: it
// reports on the protection the execution path is responsible for, and never places an order itself.
// The embedding seam (gh#109, engineering §2). Cohere when a key is configured (gh#403), the KEYLESS default
// otherwise -- the substrate stays usable, and every test runs, without an API key or any spend.
builder.Services.Configure<CohereOptions>(builder.Configuration.GetSection(CohereOptions.SectionName));
builder.Services.AddHttpClient(CohereEmbeddingProvider.HttpClientName);
builder.Services.AddSingleton<EmbeddingMetrics>();
builder.Services.AddSingleton<IEmbeddingMetrics>(provider => provider.GetRequiredService<EmbeddingMetrics>());
// The LLM-spend meter (gh#477) rides the SAME MarqSpec.TradingCopilot.Ai meter, so it exports with no exporter
// change; singleton like the embed meter (a Meter is a long-lived process-wide object). Required, never optional --
// an unmetered call is invisible spend (the gh#403 posture).
builder.Services.AddSingleton<LlmMetrics>();
builder.Services.AddSingleton<ILlmMetrics>(provider => provider.GetRequiredService<LlmMetrics>());
// The governor's configured ceiling, published as a gauge (gh#506) so the dashboard computes headroom from
// Prometheus alone rather than a hand-copied Grafana constant that drifts the moment Governor__DailyBudgetUsd
// changes. Resolved eagerly below so the observable callback is live even before anything else touches it.
// Observability only -- enforcement stays on the AIUsage ledger floor, a meter being export-only (gh#448).
builder.Services.AddSingleton<GovernorMetrics>();

builder.Services.AddSingleton<UnavailableEmbeddingProvider>();

// Probed once at startup (gh#474). A key is only half of "available": the AddEmbeddingStore migration skips the
// table entirely on a Postgres without pgvector, so a keyed deployment there embedded on every poll -- real spend
// -- and faulted at the upsert every time. Defaults to NOT present, so a caller racing the probe declines.
builder.Services.AddSingleton<VectorStore>();
builder.Services.AddSingleton<CohereEmbeddingProvider>();
builder.Services.AddSingleton<IEmbeddingProvider>(provider =>
    provider.GetRequiredService<IOptions<CohereOptions>>().Value.IsConfigured
        ? provider.GetRequiredService<CohereEmbeddingProvider>()
        : provider.GetRequiredService<UnavailableEmbeddingProvider>());

// The news-embedding pass (gh#377, R-2): the first production consumer of the seam above, populating the pgvector
// embedding behind each ingested NewsRecord. Always on, mirroring the relevance pass -- with no provider configured
// (or no news needing it) the pass is a cheap no-op, so there is nothing to opt into. IAiUsageLedger / IAiSpendGovernor
// / GovernorOptions are already bound by AddTradingCopilotAi above; this is their first embed-side consumer (gh#436).
builder.Services.Configure<NewsEmbeddingOptions>(builder.Configuration.GetSection(NewsEmbeddingOptions.SectionName));
builder.Services.AddScoped<NewsEmbeddingService>();
builder.Services.AddHostedService<NewsEmbeddingHost>();

builder.Services.AddScoped<ProtectionMonitorService>();
builder.Services.AddHostedService<ProtectionMonitorHost>();

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

// The resting-orders sibling of the positions read (gh#381): venue truth for the working orders standing on an
// account, including the attached protective bracket and its SIZE. Read-only -- the gate is untouched.
builder.Services.AddScoped<WorkingOrderReconciliationService>();

// The FILL-HISTORY sibling of the two reads above (gh#631). Neither of them can see an order that placed, filled
// and round-tripped -- a fill is not a working order, and its bracket leaves the account flat again -- so through
// those two alone that outcome is indistinguishable from an attempt that never reached the market. Only this read
// separates them, which is what stops a stranded one-shot conditional being re-armed after it already executed.
// Scoped like its siblings: it takes the request-scoped DbContext, so the R-20 filter applies.
builder.Services.AddScoped<FillReconciliationService>();

// Auto-flatten (R-13, gh#185, ADR-0013): the PRIMARY scheduler that closes open positions at each instrument's
// per-market deadline on the DST-aware market clock. On by default and cannot be silently disabled -- so it
// ALWAYS runs; a market is turned off per-instrument in the Flatten config, not by omitting the host. The
// redundant / independent watchdog above it is gh#187. Validate the schedule at startup so a malformed deadline
// fails fast rather than mid-session, the same fail-fast stance as the Jwt signing key above.
builder.Services.Configure<FlattenOptions>(builder.Configuration.GetSection(FlattenOptions.SectionName));
_ = (builder.Configuration.GetSection(FlattenOptions.SectionName).Get<FlattenOptions>() ?? new FlattenOptions()).ToSchedules();
builder.Services.AddScoped<IStagedOrderClaim, StagedOrderClaim>();
builder.Services.AddScoped<IAccountEntryGuard, AccountEntryGuard>();
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

// The operator-notification chain, queue -> dedup -> transport (gh#243, gh#289, ADR-0019). Extracted to
// NotificationRegistration (gh#320) so the SHAPE of the binding is assertable: the auto-flatten calls
// INotificationChannel on the R-13 hot path, and a regression that bound a blocking transport there would leave
// every existing test green.
builder.AddTradingCopilotNotifications();

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

// The self-documenting API surface (R-10, gh#604): an OpenAPI document generated from the minimal-API routes
// below, and a Scalar reference UI over it. The spec is the source of truth the README links to (#605); a
// generated spec cannot silently drop an endpoint the way the hand-kept table could.
builder.Services.AddTradingCopilotOpenApi();

WebApplication app = builder.Build();

// Resolve the governor gauge EAGERLY (gh#506). An ObservableGauge only exists once its owner is constructed,
// and this singleton has no injected consumer -- lazily registered it would never be built, the callback would
// never be attached, and the series would simply never appear. Nothing would fail; the panel would read empty,
// which on a cost view is indistinguishable from "no spend". Touching it here is what makes it real.
_ = app.Services.GetRequiredService<GovernorMetrics>();

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
app.MapTriggerEndpoints();
app.MapRelevanceEndpoints();
app.MapNewsEndpoints();
app.MapSuggestionEndpoints();
app.MapOrderEndpoints();
app.MapKillSwitchEndpoints();
app.MapPositionEndpoints();
app.MapWorkingOrderEndpoints();

// The generated spec (/openapi/v1.json, everywhere) and the Scalar reference UI (/scalar/v1, disabled in
// production). Mapped after the endpoint groups so the document reflects every route above (gh#604).
app.MapTradingCopilotApiReference();

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
