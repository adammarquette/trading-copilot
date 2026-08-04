using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MarqSpec.TradingCopilot.Api.Auth;
using MarqSpec.TradingCopilot.Api.Firms;
using MarqSpec.TradingCopilot.Api.MarketData;
using MarqSpec.TradingCopilot.Api.Venues;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Domain.Events;
using MarqSpec.TradingCopilot.Domain.MarketData;
using MarqSpec.TradingCopilot.Domain.Suggestions;
using MarqSpec.TradingCopilot.Domain.Venue;
using MarqSpec.TradingCopilot.IntegrationTests.TestHost;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MarqSpec.TradingCopilot.IntegrationTests.Api;

/// <summary>
/// Independent QA coverage for the suggestion-drift consumer's <b>lifecycle</b> (gh#632 ⇒ gh#546's own DoD, R-4 /
/// R-12, ADR-0013) — the half <see cref="SuggestionDriftIntegrationTests"/> cannot reach because it deliberately
/// suppresses every hosted service. Runs the <b>live</b> <see cref="SuggestionDriftHost"/> against real PostgreSQL
/// (<see cref="SuggestionDriftTestPostgresFactory"/>, which — like <c>RetentionConsumerTestPostgresFactory</c> for
/// the sibling quote consumers — keeps hosted services running and captures logs) to prove the wiring end to end:
/// a real <c>market.quote</c> event on the backbone reaches the suggestion through the host, the
/// <c>suggestion-drift</c> cursor advances and commits per batch, and the host exits cleanly on teardown rather
/// than joining the gh#169 <c>ObjectDisposedException</c> cascade a missing catch would leave behind.
/// </summary>
public class SuggestionDriftConsumerLifecycleIntegrationTests : IClassFixture<SuggestionDriftTestPostgresFactory>
{
    private readonly SuggestionDriftTestPostgresFactory _factory;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    private sealed record LoginTokenResponse(string Token);

    public SuggestionDriftConsumerLifecycleIntegrationTests(SuggestionDriftTestPostgresFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Host_ShouldConsumeARealMarketQuoteEvent_AdvanceTheCursorPerBatch_AndMarkTheDriftedSuggestionStale()
    {
        // NQ (gh#541 default: tick 0.25) so the band is a real, config-resolved 8 * 0.25 = 2.00 — not a caller-
        // supplied constant as in the guarded-update suite. The adversarial venue resolves "NQ" -> "NQM25"
        // (AdversarialTestTradingVenue.ResolveContractAsync), the contract key the ingester's quote shape carries.
        (Guid accountId, Guid operatorId) = await FreshAccountAsync();
        Guid suggestionId = await SeedSuggestionAsync(accountId, operatorId, entry: 18_000.00m, instrument: "NQ");

        long? cursorBeforeAnyQuote = await GetCursorAsync();

        // Pass 1: a WITHIN-tolerance quote. The suggestion must stay Active, but the cursor must still advance and
        // commit — the batch is committed once per pass regardless of whether anything transitioned (gh#546: "a
        // re-seen quote re-marks nothing already Stale" is about idempotence, not about withholding the commit).
        EventEnvelope withinToleranceQuote = await AppendQuoteEventAsync("NQM25", bid: 18_001.00m, ask: 18_001.00m);
        await WaitUntilAsync(
            async () => await GetCursorAsync() >= withinToleranceQuote.Sequence,
            "the suggestion-drift cursor to advance past the first (within-tolerance) batch");
        (await StateOfAsync(suggestionId)).Should().Be(
            SuggestionState.Active, "a within-tolerance quote commits the cursor but marks nothing");

        // Pass 2: a quote that drifts past the band. The live host must decode it, resolve NQ -> NQM25, mark the
        // suggestion Stale through the guarded update, and commit the cursor past this second batch too.
        EventEnvelope driftingQuote = await AppendQuoteEventAsync("NQM25", bid: 18_010.00m, ask: 18_010.00m);
        SuggestionState final = await WaitForStateAsync(suggestionId, SuggestionState.Stale, TimeSpan.FromSeconds(30));

        final.Should().Be(SuggestionState.Stale, "the live host consumed the drifting quote end to end");
        (await GetCursorAsync()).Should().BeGreaterThanOrEqualTo(
            driftingQuote.Sequence, "the cursor commits past the second batch too — each pass commits its own batch");
        (await GetCursorAsync()).Should().BeGreaterThan(
            cursorBeforeAnyQuote ?? 0, "two real passes ran, each advancing the committed cursor forward");
    }

    // =============================================================================================================
    // Helpers.
    // =============================================================================================================

    private async Task<EventEnvelope> AppendQuoteEventAsync(string contractKey, decimal bid, decimal ask)
    {
        string payload = JsonSerializer.Serialize(new { contract = $"projectx:{contractKey}", bid, ask });
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        IEventLog log = scope.ServiceProvider.GetRequiredService<IEventLog>();
        return await log.AppendAsync(
            new EventDraft(QuoteIngestionService.QuoteEventType, "projectx", DateTimeOffset.UtcNow, payload), CancellationToken.None);
    }

    private async Task<long?> GetCursorAsync()
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        IEventLog log = scope.ServiceProvider.GetRequiredService<IEventLog>();
        return await log.GetCursorAsync(SuggestionDriftHost.ConsumerGroup, CancellationToken.None);
    }

    private Task<SuggestionState> StateOfAsync(Guid suggestionId) => QueryDbAsync(db => db.Suggestions
        .IgnoreQueryFilters().AsNoTracking().Where(s => s.Id == suggestionId).Select(s => s.State).SingleAsync());

    /// <summary>Polls until the suggestion reaches <paramref name="target"/> or the timeout elapses (the live host
    /// is asynchronous — the same idiom <c>ConditionalFiringIntegrationTests.WaitForStatusAsync</c> uses).</summary>
    private async Task<SuggestionState> WaitForStateAsync(Guid suggestionId, SuggestionState target, TimeSpan timeout)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;
        SuggestionState state = SuggestionState.Unknown;
        while (DateTimeOffset.UtcNow < deadline)
        {
            state = await StateOfAsync(suggestionId);
            if (state == target)
            {
                return state;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250));
        }

        state.Should().Be(target, $"the live suggestion-drift host should have reached {target} within {timeout.TotalSeconds:0}s");
        return state;
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, string because, int attempts = 150, int delayMs = 200)
    {
        for (int attempt = 0; attempt < attempts; attempt++)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(delayMs);
        }

        throw new TimeoutException($"Timed out waiting for {because}.");
    }

    private async Task<Guid> SeedSuggestionAsync(Guid accountId, Guid operatorId, decimal entry, string instrument)
    {
        Guid id = Guid.NewGuid();
        await ExecuteDbAsync(async db =>
        {
            db.Suggestions.Add(new Suggestion
            {
                Id = id,
                UserId = operatorId,
                AccountId = accountId,
                Instrument = instrument,
                Side = OrderSide.Buy,
                Size = 1,
                EntryPrice = entry,
                StopPrice = entry - 10m,
                TargetPrice = entry + 20m,
                Mode = TradingMode.Practice,
                State = SuggestionState.Active,
                CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
                Rationale = "seeded for the drift consumer lifecycle suite (gh#632)",
                CitedIndicator = "rsi",
                CitedPeriod = 14,
                CitedResolutionMinutes = 5,
                Confidence = 60,
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(2),
            });
            await db.SaveChangesAsync();
        });
        return id;
    }

    /// <summary>Firm → conventions (no capital at risk ⇒ Practice) → connection → discovered tradeable account, and
    /// wipes the suggestion journal first — the same shape the guarded-update suite uses.</summary>
    private async Task<(Guid AccountId, Guid OperatorId)> FreshAccountAsync()
    {
        await ExecuteDbAsync(async db =>
        {
            await db.SuggestionDispositions.IgnoreQueryFilters().ExecuteDeleteAsync();
            await db.Suggestions.IgnoreQueryFilters().ExecuteDeleteAsync();
        });

        HttpClient client = await AuthenticatedClientAsync();
        using HttpResponseMessage createFirm = await client.PostAsJsonAsync(
            "/firms", new CreateFirmRequest($"Topstep-{Guid.NewGuid():N}", FirmType.PropFirm));
        FirmResponse? firm = await createFirm.Content.ReadFromJsonAsync<FirmResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(firm);

        using HttpResponseMessage conventions = await client.PutAsJsonAsync(
            $"/firms/{firm.Id}/conventions",
            new DeclareConventionsRequest([new StageConventionDto(AccountStage.Practice, CapitalAtRisk: false)]));
        conventions.EnsureSuccessStatusCode();

        using HttpResponseMessage createConnection = await client.PostAsJsonAsync(
            "/connections", new CreateConnectionRequest(firm.Id, "projectx", "topstep-main"));
        ConnectionResponse? connection = await createConnection.Content.ReadFromJsonAsync<ConnectionResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(connection);

        using HttpResponseMessage discover = await client.PostAsync($"/connections/{connection.Id}/accounts/discover", null);
        discover.EnsureSuccessStatusCode();
        List<AccountResponse>? accounts = await discover.Content.ReadFromJsonAsync<List<AccountResponse>>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(accounts);

        AccountResponse account = accounts.First(a => a.CanTrade);
        Guid operatorId = await QueryDbAsync(db => db.Users.AsNoTracking().Where(u => u.Email == PostgresApiFactory.OperatorEmail).Select(u => u.Id).SingleAsync());
        return (account.Id, operatorId);
    }

    private async Task<HttpClient> AuthenticatedClientAsync()
    {
        HttpClient client = _factory.CreateClient();
        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/auth/login", new LoginRequest(PostgresApiFactory.OperatorEmail, PostgresApiFactory.OperatorPassword));
        LoginTokenResponse? auth = await response.Content.ReadFromJsonAsync<LoginTokenResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(auth);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.Token);
        return client;
    }

    private async Task ExecuteDbAsync(Func<TradingCopilotDbContext, Task> action)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        TradingCopilotDbContext db = scope.ServiceProvider.GetRequiredService<TradingCopilotDbContext>();
        await action(db);
    }

    private async Task<T> QueryDbAsync<T>(Func<TradingCopilotDbContext, Task<T>> query)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        TradingCopilotDbContext db = scope.ServiceProvider.GetRequiredService<TradingCopilotDbContext>();
        return await query(db);
    }
}
