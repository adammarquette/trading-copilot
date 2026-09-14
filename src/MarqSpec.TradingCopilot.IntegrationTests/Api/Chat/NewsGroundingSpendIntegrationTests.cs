using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MarqSpec.TradingCopilot.Api.Auth;
using MarqSpec.TradingCopilot.Api.Chat;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Domain.Ai;
using MarqSpec.TradingCopilot.IntegrationTests.TestHost;
using Microsoft.EntityFrameworkCore;
using Pgvector;

namespace MarqSpec.TradingCopilot.IntegrationTests.Api.Chat;

/// <summary>
/// Pre-merge integration coverage for how always-on news grounding (gh#996, verifying gh#995 — R-6, ADR-0027)
/// interacts with the platform-level <b>AI-spend governor</b> (ADR-0008) against real Postgres, driven through the
/// shipped endpoint <c>POST /conversations/{id}/turns</c> on a budget-configured host
/// (<see cref="NewsGroundingSpendTestPostgresFactory"/>). Written independently of the implementation, from ADR-0027
/// clause 4/5 and the tracking issue.
/// </summary>
/// <remarks>
/// <b>One budget per factory (gh#479's constraint) — cases vary the seeded spend, never the config.</b> The budget is
/// <see cref="NewsGroundingSpendTestPostgresFactory.DailyBudgetUsd"/> (10.00) at the default 80% alert threshold
/// (8.00). <c>ChatEndpoints.TurnAsync</c> reads the window with the real, unfaked query — the point of this suite is
/// that grounding respects the SAME gate the chat call itself is already metered against, never a second one.
/// </remarks>
public sealed class NewsGroundingSpendIntegrationTests : IClassFixture<NewsGroundingSpendTestPostgresFactory>
{
    private static readonly DateTimeOffset _publishedAt = new(2026, 8, 20, 14, 0, 0, TimeSpan.Zero);

    private readonly NewsGroundingSpendTestPostgresFactory _factory;
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    private sealed record LoginTokenResponse(string Token);

    public NewsGroundingSpendIntegrationTests(NewsGroundingSpendTestPostgresFactory factory)
    {
        _factory = factory;
    }

    // =============================================================================================================
    // AC4a — grounding's OWN spend (embed + rerank) is ledgered, stamped to the operator, distinct from the chat call.
    // =============================================================================================================

    [Fact]
    public async Task Turn_ShouldLedgerEmbedAndRerankRows_StampedToTheOperator_SeparatelyFromTheChatCompletion()
    {
        await ResetAsync();
        HttpClient client = await AuthenticatedOperatorClientAsync();
        Guid ownerId = await OperatorUserIdAsync();
        Guid conversationId = await StartConversationAsync(client);
        await SeedNewsAsync(
            "https://news.test/gh996-spend-ground", "Gold rallies on haven demand", "Bullion climbed as investors sought safety.");

        _factory.Llm.Script(request => ScriptedChatLlmProvider.Answer("Echo: " + request.Messages[^1].Content));

        using HttpResponseMessage response = await TakeTurnAsync(client, conversationId, "Any moves in gold?");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        IReadOnlyList<AiUsageRecord> rows = await LedgerRowsAsync();
        rows.Should().OnlyContain(row => row.UserId == ownerId, "grounding's OWN spend is stamped to the conversation's owner, not a system id");

        AiUsageRecord embed = rows.Should().ContainSingle(row => row.Feature == AiUsageFeature.Embed)
            .Which;
        embed.Tier.Should().BeNull("an embed call has no model TIER (ADR-0008)");

        // Rerank rides Chat with Tier == null (ADR-0008); the chat COMPLETION also rides Chat but at the Deep tier —
        // so distinguishing them by Tier is the only way the two rows can't be confused with one aggregated row.
        IReadOnlyList<AiUsageRecord> chatFeatureRows = [.. rows.Where(row => row.Feature == AiUsageFeature.Chat)];
        chatFeatureRows.Should().HaveCount(2, "the rerank call and the turn's own chat completion are TWO distinct billed calls");
        chatFeatureRows.Should().ContainSingle(row => row.Tier == null, "the rerank row carries no model tier");
        chatFeatureRows.Should().ContainSingle(row => row.Tier == LlmModelTier.Deep, "the chat completion runs at the deep tier");

        rows.Should().HaveCount(3, "exactly the embed, the rerank, and the one chat completion — no extra, no drop");
    }

    // =============================================================================================================
    // AC4b — grounding is the first cost shed: once spend crosses the pre-alert threshold, retrieval never runs.
    // =============================================================================================================

    [Fact]
    public async Task Turn_ShouldSkipGrounding_AndBookNoGroundingSpend_WhenSpendHasCrossedTheAlertThreshold()
    {
        await ResetAsync();
        HttpClient client = await AuthenticatedOperatorClientAsync();
        Guid conversationId = await StartConversationAsync(client);
        await SeedNewsAsync(
            "https://news.test/gh996-threshold", "Wheat futures jump on export demand", "Export orders lifted grain prices sharply.");

        // Spend already AT the 80% pre-alert threshold (8.00 of the 10.00 budget) — under the hard cap, so the chat
        // call itself still runs, but ADR-0027 clause 5 says grounding is the first thing shed as the cap nears.
        await SeedSpendAsync(NewsGroundingSpendTestPostgresFactory.DailyBudgetUsd * 0.8m);
        const string prompt = "What's moving in grains?";
        _factory.Llm.Script(request => ScriptedChatLlmProvider.Answer("Echo: " + request.Messages[^1].Content));

        using HttpResponseMessage response = await TakeTurnAsync(client, conversationId, prompt);

        response.StatusCode.Should().Be(HttpStatusCode.OK, "the threshold suppresses GROUNDING, never the chat call itself");
        RecordedLlmCall call = _factory.Llm.Calls.Should().ContainSingle().Which;
        call.Request.Messages[^1].Content.Should().Be(
            prompt, "retrieval never ran, so the message is the un-grounded, byte-identical shape — even though matching news exists");

        IReadOnlyList<AiUsageRecord> rows = await LedgerRowsAsync();
        rows.Should().NotContain(row => row.Feature == AiUsageFeature.Embed, "grounding's embed call never ran, so it books nothing");
        rows.Should().HaveCount(2, "the pre-seeded threshold row, plus the turn's own chat completion — nothing else");
    }

    // =============================================================================================================
    // AC4c — a governor-blocked turn (429) writes no grounding-spend rows: retrieval never ran, nothing persisted.
    // =============================================================================================================

    [Fact]
    public async Task Turn_ShouldBeBlocked_AndBookNoAiUsageRowsAtAll_WhenTheDailyBudgetIsReached()
    {
        await ResetAsync();
        HttpClient client = await AuthenticatedOperatorClientAsync();
        Guid conversationId = await StartConversationAsync(client);
        await SeedNewsAsync(
            "https://news.test/gh996-blocked", "Natural gas spikes on cold snap", "A forecast cold snap drove gas prices sharply higher.");

        // Spend AT the hard cap.
        await SeedSpendAsync(NewsGroundingSpendTestPostgresFactory.DailyBudgetUsd);
        _factory.Llm.Script(request => ScriptedChatLlmProvider.Answer("Echo: " + request.Messages[^1].Content));

        using HttpResponseMessage response = await TakeTurnAsync(client, conversationId, "What's up with gas?");

        response.StatusCode.Should().Be(HttpStatusCode.TooManyRequests, "the daily AI-spend budget is reached");
        _factory.Llm.CallCount.Should().Be(0, "a governor-blocked turn never reaches the model");

        IReadOnlyList<AiUsageRecord> rows = await LedgerRowsAsync();
        rows.Should().HaveCount(1, "only the ONE pre-seeded blocking row — the blocked turn itself wrote nothing, grounding included");

        ConversationDetailResponse thread = await ReadConversationAsync(client, conversationId);
        thread.Messages.Should().BeEmpty("a blocked turn persists nothing — not even the operator's own message");
    }

    // =============================================================================================================
    // Fixtures.
    // =============================================================================================================

    private async Task ResetAsync()
    {
        _factory.Llm.Reset();
        _factory.EmbeddingProvider.Reset();
        await _factory.WithDatabaseAsync(async database =>
        {
            await database.ChatMessages.IgnoreQueryFilters().ExecuteDeleteAsync();
            await database.Conversations.IgnoreQueryFilters().ExecuteDeleteAsync();
            await database.AiUsage.IgnoreQueryFilters().ExecuteDeleteAsync();
            await database.Embeddings.ExecuteDeleteAsync();
            await database.News.ExecuteDeleteAsync();
            return true;
        });
    }

    private Task SeedNewsAsync(string dedupKey, string title, string summary) => _factory.WithDatabaseAsync(async database =>
    {
        database.News.Add(new NewsRecord
        {
            DedupKey = dedupKey,
            Type = "news",
            Url = dedupKey,
            Title = title,
            Summary = summary,
            PublishedAt = _publishedAt,
            Tickers = [],
            SourceFeeds = ["finnhub"],
            RecordedAt = _publishedAt,
        });

        EmbeddingResult embedded = await _factory.EmbeddingProvider.EmbedAsync($"news:{dedupKey}", CancellationToken.None);
        database.Embeddings.Add(new EmbeddingRecord
        {
            OwnerKind = EmbeddingOwnerKind.SoftSignal,
            OwnerId = dedupKey,
            Model = _factory.EmbeddingProvider.Model,
            Dimensions = _factory.EmbeddingProvider.Dimensions,
            Embedding = new Vector(embedded.Vector!.ToArray()),
            ContentHash = $"gh996-{dedupKey}",
            RecordedAt = _publishedAt,
        });

        await database.SaveChangesAsync();
        return true;
    });

    // Seeds one deployment-wide ledger row of `costUsd`, stamped `now` — the same shared-account spend the
    // governor's read sums over EVERY owner (ADR-0008), so the seeding owner is deliberately a stranger.
    // ChatEndpoints.TurnAsync reads DateTimeOffset.UtcNow at request time (not an injectable clock), so the row is
    // stamped to the real current instant rather than a fixed historical date.
    private Task SeedSpendAsync(decimal costUsd) => _factory.WithDatabaseAsync(async database =>
    {
        database.AiUsage.Add(new AiUsageRecord
        {
            Id = Guid.NewGuid(),
            UserId = Guid.NewGuid(),
            Feature = AiUsageFeature.Chat,
            Model = "seed",
            Tier = LlmModelTier.Deep,
            Outcome = AiUsageOutcome.Succeeded,
            InputTokens = 0,
            OutputTokens = 0,
            EstimatedCostUsd = costUsd,
            LatencyMs = 0,
            TraceId = null,
            OccurredAt = DateTimeOffset.UtcNow,
        });
        await database.SaveChangesAsync();
        return true;
    });

    private Task<IReadOnlyList<AiUsageRecord>> LedgerRowsAsync() => _factory.WithDatabaseAsync(
        async database => (IReadOnlyList<AiUsageRecord>)await database.AiUsage.IgnoreQueryFilters().AsNoTracking().ToListAsync());

    private async Task<Guid> StartConversationAsync(HttpClient client)
    {
        using HttpResponseMessage created = await client.PostAsJsonAsync(
            "/conversations", new CreateConversationRequest($"gh996-spend-{Guid.NewGuid():N}"));
        created.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await ReadAsync<ConversationResponse>(created)).Id;
    }

    private Task<HttpResponseMessage> TakeTurnAsync(HttpClient client, Guid conversationId, string content) =>
        client.PostAsJsonAsync($"/conversations/{conversationId}/turns", new ChatTurnRequest(content));

    private async Task<ConversationDetailResponse> ReadConversationAsync(HttpClient client, Guid conversationId)
    {
        using HttpResponseMessage response = await client.GetAsync(new Uri($"/conversations/{conversationId}", UriKind.Relative));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return await ReadAsync<ConversationDetailResponse>(response);
    }

    private async Task<HttpClient> AuthenticatedOperatorClientAsync()
    {
        HttpClient client = _factory.CreateClient();
        using HttpResponseMessage login = await client.PostAsJsonAsync(
            "/auth/login", new LoginRequest(PostgresApiFactory.OperatorEmail, PostgresApiFactory.OperatorPassword));
        login.StatusCode.Should().Be(HttpStatusCode.OK);
        LoginTokenResponse auth = await ReadAsync<LoginTokenResponse>(login);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.Token);
        return client;
    }

    private Task<Guid> OperatorUserIdAsync() => _factory.WithDatabaseAsync(database => database.Users
        .IgnoreQueryFilters()
        .Where(user => user.Email == PostgresApiFactory.OperatorEmail)
        .Select(user => user.Id)
        .SingleAsync());

    private async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        T? value = await response.Content.ReadFromJsonAsync<T>(_json);
        ArgumentNullException.ThrowIfNull(value);
        return value;
    }
}
