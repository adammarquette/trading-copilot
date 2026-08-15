using FakeItEasy;
using MarqSpec.TradingCopilot.Api.Ai;
using MarqSpec.TradingCopilot.Domain.Ai;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MarqSpec.TradingCopilot.UnitTests.Api.Ai;

/// <summary>
/// The semantic salience axis (gh#853, R-2, R-9): scores the feed's <b>window candidates</b> by max cosine similarity
/// to the operator's <b>starred</b> items' embeddings, as a <c>dedupKey → similarity</c> map. It reads both sets'
/// stored vectors over a <b>fake</b> seam (the real read is relational-only, integration-tier) and ranks them with the
/// pure <see cref="EmbeddingSimilarity"/> helper. The properties proven here: it scores the ACTUAL candidates (never a
/// truncated global nearest-N — the SF1 fix), it makes <b>no read</b> when there is nothing to rank, it excludes a
/// self-starred candidate, and it <b>degrades to an empty map rather than throwing</b> on any read fault while a
/// genuine caller cancellation still propagates.
/// </summary>
public class SemanticSalienceAxisTests
{
    private readonly IEmbeddingProvider _provider = A.Fake<IEmbeddingProvider>();
    private readonly INewsEmbeddingSimilarity _similarity = A.Fake<INewsEmbeddingSimilarity>();

    // A store of embeddings by owner id; the faked GetVectorsAsync returns those of the requested owners (mirroring
    // the real Where(ownerIds.Contains(OwnerId)) read), so a test just declares vectors and both reads serve from it.
    private readonly Dictionary<string, IReadOnlyList<float>> _store = new(StringComparer.Ordinal);

    private SemanticSalienceAxis Axis() => new(_provider, _similarity, NullLogger<SemanticSalienceAxis>.Instance);

    private void Embed(string ownerId, params float[] vector) => _store[ownerId] = vector;

    private void ProviderUp()
    {
        A.CallTo(() => _provider.IsAvailable).Returns(true);
        A.CallTo(() => _similarity.GetVectorsAsync(A<IReadOnlyCollection<string>>._, A<CancellationToken>._))
            .ReturnsLazily((IReadOnlyCollection<string> ids, CancellationToken _) =>
            {
                IReadOnlyList<StoredEmbedding> hits =
                    [.. ids.Where(_store.ContainsKey).Select(id => new StoredEmbedding(id, _store[id]))];
                return hits;
            });
    }

    [Fact]
    public async Task ForCandidatesAsync_ScoresEachCandidateByMaxSimilarityToAStar_WhenAvailable()
    {
        ProviderUp();
        Embed("star", 1f, 0f);
        Embed("near", 1f, 0f); // identical direction to the star -> similarity 1
        Embed("far", 0f, 1f);  // orthogonal -> similarity 0

        IReadOnlyDictionary<string, double> map =
            await Axis().ForCandidatesAsync(["near", "far"], ["star"], CancellationToken.None);

        map["near"].Should().BeApproximately(1.0, 1e-9);
        map["far"].Should().BeApproximately(0.0, 1e-9);
    }

    [Fact]
    public async Task ForCandidatesAsync_ScoresEveryWindowCandidate_NotAGlobalNearestN()
    {
        // SF1 regression: the axis scores the ACTUAL window candidates, so a candidate near a star is scored even amid
        // many other near-a-star items -- there is no nearest-N to saturate and starve it. Every candidate is scored.
        ProviderUp();
        Embed("star", 1f, 0f);
        Embed("old-1", 1f, 0f);
        Embed("old-2", 1f, 0f);
        Embed("old-3", 1f, 0f);
        Embed("recent", 1f, 0f); // just as near the star as the older ones

        IReadOnlyDictionary<string, double> map = await Axis().ForCandidatesAsync(
            ["old-1", "old-2", "old-3", "recent"], ["star"], CancellationToken.None);

        map.Should().HaveCount(4);
        map["recent"].Should().BeApproximately(1.0, 1e-9); // scored, not dropped by any nearest-N cap
    }

    [Fact]
    public async Task ForCandidatesAsync_ExcludesACandidateThatIsItselfStarred()
    {
        // A starred item already carries its star; it must not self-match at similarity 1 on the semantic axis.
        ProviderUp();
        Embed("star", 1f, 0f);
        Embed("other", 1f, 0f);

        IReadOnlyDictionary<string, double> map =
            await Axis().ForCandidatesAsync(["star", "other"], ["star"], CancellationToken.None);

        map.Should().NotContainKey("star");
        map.Should().ContainKey("other");
    }

    [Fact]
    public async Task ForCandidatesAsync_ReturnsEmptyWithoutReadingTheSeam_WhenThereAreNoStars()
    {
        A.CallTo(() => _provider.IsAvailable).Returns(true);

        IReadOnlyDictionary<string, double> map =
            await Axis().ForCandidatesAsync(["candidate"], [], CancellationToken.None);

        map.Should().BeEmpty();
        A.CallTo(() => _similarity.GetVectorsAsync(A<IReadOnlyCollection<string>>._, A<CancellationToken>._))
            .MustNotHaveHappened();
    }

    [Fact]
    public async Task ForCandidatesAsync_ReturnsEmptyWithoutReadingTheSeam_WhenThereAreNoCandidates()
    {
        A.CallTo(() => _provider.IsAvailable).Returns(true);

        IReadOnlyDictionary<string, double> map =
            await Axis().ForCandidatesAsync([], ["star"], CancellationToken.None);

        map.Should().BeEmpty();
        A.CallTo(() => _similarity.GetVectorsAsync(A<IReadOnlyCollection<string>>._, A<CancellationToken>._))
            .MustNotHaveHappened();
    }

    [Fact]
    public async Task ForCandidatesAsync_ReturnsEmptyWithoutReadingTheSeam_WhenTheProviderIsUnavailable()
    {
        // The degrade decision (gh#109): with no embedding provider the semantic axis is simply off -- no seam read.
        A.CallTo(() => _provider.IsAvailable).Returns(false);

        IReadOnlyDictionary<string, double> map =
            await Axis().ForCandidatesAsync(["candidate"], ["star"], CancellationToken.None);

        map.Should().BeEmpty();
        A.CallTo(() => _similarity.GetVectorsAsync(A<IReadOnlyCollection<string>>._, A<CancellationToken>._))
            .MustNotHaveHappened();
    }

    [Fact]
    public async Task ForCandidatesAsync_ReturnsEmpty_WhenNoStarredItemIsEmbedded()
    {
        // The provider is up and there are stars, but none of their vectors are stored yet -> no reference set.
        ProviderUp();
        Embed("candidate", 1f, 0f); // only the candidate is embedded

        IReadOnlyDictionary<string, double> map =
            await Axis().ForCandidatesAsync(["candidate"], ["star"], CancellationToken.None);

        map.Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(SeamFaults))]
    public async Task ForCandidatesAsync_DegradesToEmpty_WhenTheSeamFaults(Exception fault)
    {
        // The read is off the trading path. A pgvector outage (or any read fault) must degrade the feed to its
        // categorical axes, never surface as an error to the operator's news feed (mirrors NewsSemanticSearch).
        A.CallTo(() => _provider.IsAvailable).Returns(true);
        A.CallTo(() => _similarity.GetVectorsAsync(A<IReadOnlyCollection<string>>._, A<CancellationToken>._))
            .Throws(fault);

        IReadOnlyDictionary<string, double> map =
            await Axis().ForCandidatesAsync(["candidate"], ["star"], CancellationToken.None);

        map.Should().BeEmpty("a semantic-read fault degrades the feed to its categorical axes; it must never error");
    }

    public static IEnumerable<object[]> SeamFaults() =>
    [
        [new InvalidOperationException("vector read failed")],
        [new DbUpdateException("pgvector is unavailable")],
    ];

    [Fact]
    public async Task ForCandidatesAsync_Rethrows_WhenTheCallerCancels()
    {
        // Host shutdown is not a read fault to swallow: a cancellation on the caller's own token propagates.
        using CancellationTokenSource cts = new();
        await cts.CancelAsync();
        A.CallTo(() => _provider.IsAvailable).Returns(true);
        A.CallTo(() => _similarity.GetVectorsAsync(A<IReadOnlyCollection<string>>._, A<CancellationToken>._))
            .Throws(new OperationCanceledException(cts.Token));

        Func<Task> act = () => Axis().ForCandidatesAsync(["candidate"], ["star"], cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>(
            "a genuine caller cancellation is host shutdown, not a fault to swallow");
    }

    [Fact]
    public async Task ForCandidatesAsync_DegradesToEmpty_WhenTheSeamTimesOutOnAnInternalToken()
    {
        // gh#589: a downstream timeout surfaces as an OperationCanceledException carrying an INTERNAL cancelled token
        // while the CALLER's own token stays healthy. That is a read fault to degrade, not host shutdown to propagate
        // -- the `when (cancellationToken.IsCancellationRequested)` filter (checking the CALLER's token) discriminates.
        using CancellationTokenSource internalTimeout = new();
        await internalTimeout.CancelAsync();
        A.CallTo(() => _provider.IsAvailable).Returns(true);
        A.CallTo(() => _similarity.GetVectorsAsync(A<IReadOnlyCollection<string>>._, A<CancellationToken>._))
            .Throws(new OperationCanceledException(internalTimeout.Token));

        IReadOnlyDictionary<string, double> map =
            await Axis().ForCandidatesAsync(["candidate"], ["star"], CancellationToken.None);

        map.Should().BeEmpty(
            "a timeout OCE on an internal token degrades the feed; only the caller's own cancellation propagates");
    }
}
