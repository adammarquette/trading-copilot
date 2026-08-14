using FakeItEasy;
using MarqSpec.TradingCopilot.Api.Ai;
using MarqSpec.TradingCopilot.Domain.Ai;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MarqSpec.TradingCopilot.UnitTests.Api.Ai;

/// <summary>
/// The news-embedding semantic search (gh#852, R-2): embed a retrieval query, then read the similarity seam — and
/// carry the whole graceful-degrade decision so a news feed gets a semantic axis when one is available and falls
/// back to its non-semantic ranking when one is not. The seam itself (the real pgvector <c>CosineDistance</c> read)
/// is relational-only and integration-tier (QA #855); here it is a <b>fake</b>, so what is proven is the decision
/// logic around it.
/// </summary>
/// <remarks>
/// Two properties it must never get wrong: it embeds a query as a <b>query</b> (never the document write path), and
/// it <b>degrades to empty rather than throwing</b> on any trouble — an unavailable provider, a query that will not
/// embed, or a read that faults — while a genuine caller cancellation still propagates.
/// </remarks>
public class NewsSemanticSearchTests
{
    private readonly IEmbeddingProvider _provider = A.Fake<IEmbeddingProvider>();
    private readonly INewsEmbeddingSimilarity _similarity = A.Fake<INewsEmbeddingSimilarity>();

    private NewsSemanticSearch Search() =>
        new(_provider, _similarity, NullLogger<NewsSemanticSearch>.Instance);

    private static EmbeddingResult Embedded(params float[] vector) =>
        new(vector, EmbeddingOutcome.Embedded, vector.Length, 0m);

    [Fact]
    public async Task NearestNewsForQueryAsync_ShouldReturnTheSeamsNeighborsInOrder_WhenAvailableAndTheQueryEmbeds()
    {
        A.CallTo(() => _provider.IsAvailable).Returns(true);
        A.CallTo(() => _provider.EmbedQueryAsync("Fed decision", A<CancellationToken>._))
            .Returns(Embedded(0.1f, 0.2f, 0.3f));
        IReadOnlyList<SemanticNeighbor> neighbors = [new("news-near", 0.05), new("news-far", 0.40)];
        A.CallTo(() => _similarity.NearestNewsAsync(A<IReadOnlyList<float>>._, 5, A<CancellationToken>._))
            .Returns(neighbors);

        IReadOnlyList<SemanticNeighbor> result =
            await Search().NearestNewsForQueryAsync("Fed decision", 5, CancellationToken.None);

        // The seam already ranks by cosine distance; the search is a pass-through that must preserve that order.
        result.Should().Equal(neighbors);
    }

    [Fact]
    public async Task NearestNewsForQueryAsync_ShouldEmbedWithTheQueryPath_NotTheDocumentPath()
    {
        // A retrieval query embeds as search_query; search_document is the write-side input type, and embedding a
        // query as a document silently degrades match quality. This pins the search to the query method (gh#852).
        A.CallTo(() => _provider.IsAvailable).Returns(true);
        A.CallTo(() => _provider.EmbedQueryAsync(A<string>._, A<CancellationToken>._)).Returns(Embedded(0.1f));

        await Search().NearestNewsForQueryAsync("query", 5, CancellationToken.None);

        A.CallTo(() => _provider.EmbedQueryAsync("query", A<CancellationToken>._)).MustHaveHappenedOnceExactly();
        A.CallTo(() => _provider.EmbedAsync(A<string>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task NearestNewsForQueryAsync_ShouldReturnEmptyWithoutSpending_WhenTheProviderIsUnavailable()
    {
        // The degrade decision (gh#109): with no provider the semantic axis is simply skipped — no embed (no paid
        // call) and no seam read. The caller falls back to its non-semantic path; it never errors.
        A.CallTo(() => _provider.IsAvailable).Returns(false);

        IReadOnlyList<SemanticNeighbor> result =
            await Search().NearestNewsForQueryAsync("query", 5, CancellationToken.None);

        result.Should().BeEmpty();
        A.CallTo(() => _provider.EmbedQueryAsync(A<string>._, A<CancellationToken>._)).MustNotHaveHappened();
        A.CallTo(() => _provider.EmbedAsync(A<string>._, A<CancellationToken>._)).MustNotHaveHappened();
        A.CallTo(() => _similarity.NearestNewsAsync(A<IReadOnlyList<float>>._, A<int>._, A<CancellationToken>._))
            .MustNotHaveHappened();
    }

    [Fact]
    public async Task NearestNewsForQueryAsync_ShouldReturnEmptyAndNotCallTheSeam_WhenTheQueryDoesNotEmbed()
    {
        // A null vector is the provider's honest "unavailable/failed" answer (never an empty or zero vector).
        // Searching with it would rank every row identically, so the search stops here rather than returning noise.
        A.CallTo(() => _provider.IsAvailable).Returns(true);
        A.CallTo(() => _provider.EmbedQueryAsync(A<string>._, A<CancellationToken>._))
            .Returns(new EmbeddingResult(null, EmbeddingOutcome.RateLimited, 0, 0m));

        IReadOnlyList<SemanticNeighbor> result =
            await Search().NearestNewsForQueryAsync("query", 5, CancellationToken.None);

        result.Should().BeEmpty();
        A.CallTo(() => _similarity.NearestNewsAsync(A<IReadOnlyList<float>>._, A<int>._, A<CancellationToken>._))
            .MustNotHaveHappened();
    }

    [Theory]
    [MemberData(nameof(SeamFaults))]
    public async Task NearestNewsForQueryAsync_ShouldReturnEmpty_WhenTheSeamFaults(Exception fault)
    {
        // The read is off the trading path. A pgvector outage (or any query fault) must degrade the feed to its
        // non-semantic axis, never surface as an error to the operator's news feed.
        A.CallTo(() => _provider.IsAvailable).Returns(true);
        A.CallTo(() => _provider.EmbedQueryAsync(A<string>._, A<CancellationToken>._)).Returns(Embedded(0.1f));
        A.CallTo(() => _similarity.NearestNewsAsync(A<IReadOnlyList<float>>._, A<int>._, A<CancellationToken>._))
            .Throws(fault);

        IReadOnlyList<SemanticNeighbor> result =
            await Search().NearestNewsForQueryAsync("query", 5, CancellationToken.None);

        result.Should().BeEmpty("a semantic-read fault degrades the feed; it must never error");
    }

    public static IEnumerable<object[]> SeamFaults() =>
    [
        [new InvalidOperationException("similarity query failed")],
        [new DbUpdateException("pgvector is unavailable")],
    ];

    [Fact]
    public async Task NearestNewsForQueryAsync_ShouldRethrow_WhenTheCallerCancels()
    {
        // Host shutdown is not a read fault to swallow: a cancellation on the caller's own token propagates, exactly
        // as the embedding provider and the trigger reviewer treat their own cancellation.
        using CancellationTokenSource cts = new();
        await cts.CancelAsync();
        A.CallTo(() => _provider.IsAvailable).Returns(true);
        A.CallTo(() => _provider.EmbedQueryAsync(A<string>._, A<CancellationToken>._)).Returns(Embedded(0.1f));
        A.CallTo(() => _similarity.NearestNewsAsync(A<IReadOnlyList<float>>._, A<int>._, A<CancellationToken>._))
            .Throws(new OperationCanceledException(cts.Token));

        Func<Task> act = () => Search().NearestNewsForQueryAsync("query", 5, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>(
            "a genuine caller cancellation is host shutdown, not a fault to swallow");
    }

    [Fact]
    public async Task NearestNewsForQueryAsync_ShouldDegradeToEmpty_WhenTheSeamTimesOutOnAnInternalToken()
    {
        // gh#589: a downstream TIMEOUT surfaces as an OperationCanceledException carrying an INTERNAL cancelled token
        // while the CALLER's own token stays healthy. That is a read fault to degrade, not host shutdown to
        // propagate — and the `when (cancellationToken.IsCancellationRequested)` filter is the discriminator (it
        // checks the CALLER's token, never the exception's). This is the untested half of the rethrow-vs-swallow
        // split: dropping the filter (rethrowing every OCE) throws a pgvector timeout into the operator's feed and
        // fails here.
        using CancellationTokenSource internalTimeout = new();
        await internalTimeout.CancelAsync();
        A.CallTo(() => _provider.IsAvailable).Returns(true);
        A.CallTo(() => _provider.EmbedQueryAsync(A<string>._, A<CancellationToken>._)).Returns(Embedded(0.1f));
        A.CallTo(() => _similarity.NearestNewsAsync(A<IReadOnlyList<float>>._, A<int>._, A<CancellationToken>._))
            .Throws(new OperationCanceledException(internalTimeout.Token));

        // The caller's own token is never cancelled — this is a downstream timeout, not caller shutdown.
        IReadOnlyList<SemanticNeighbor> result =
            await Search().NearestNewsForQueryAsync("query", 5, CancellationToken.None);

        result.Should().BeEmpty(
            "a timeout OCE on an internal token degrades the feed; only the caller's own cancellation propagates");
    }
}
