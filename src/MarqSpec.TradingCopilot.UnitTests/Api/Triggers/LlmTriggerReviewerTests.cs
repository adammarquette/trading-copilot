using FakeItEasy;
using MarqSpec.TradingCopilot.Api.Triggers;
using MarqSpec.TradingCopilot.Domain;
using MarqSpec.TradingCopilot.Domain.Ai;
using MarqSpec.TradingCopilot.Domain.Triggers;
using MarqSpec.TradingCopilot.Domain.Venue;
using Microsoft.Extensions.Logging.Abstractions;

namespace MarqSpec.TradingCopilot.UnitTests.Api.Triggers;

/// <summary>
/// The LLM-backed reviewer (gh#402, ADR-0008). The design-defining behaviour is FAIL-CLOSED: a well-formed
/// <i>suggest</i> maps to a <see cref="ReviewOutcome.Suggest"/> with the right side + prices, and <b>everything
/// else</b> — a decline, a refusal / truncation, unparseable JSON, an unknown decision or direction, a missing
/// price — becomes a <see cref="ReviewOutcome.Suppress"/>, never a suggestion. It also asks for the cheap triage tier
/// and a JSON-schema-constrained response.
/// </summary>
public class LlmTriggerReviewerTests
{
    private readonly ILlmProvider _llm = A.Fake<ILlmProvider>();

    private LlmTriggerReviewer Reviewer() => new(_llm, NullLogger<LlmTriggerReviewer>.Instance);

    private static TriggerReviewContext Context() => new(
        Guid.NewGuid(), InstrumentId.Parse("ES"), "rsi", 14, 1,
        IndicatorComparison.Below, 30m, 25m, DateTimeOffset.UnixEpoch);

    private void ProviderReturns(string text, LlmStopReason stop = LlmStopReason.Completed) =>
        A.CallTo(() => _llm.CompleteAsync(A<LlmRequest>._, A<CancellationToken>._))
            .Returns(new LlmCompletion(text, stop, LlmUsage.None));

    [Fact]
    public async Task ReviewAsync_ShouldSuggestWithMappedSideAndPrices_WhenTheModelProposesALong()
    {
        ProviderReturns(
            "{\"decision\":\"suggest\",\"direction\":\"long\",\"entry\":100.5,\"stop\":99,\"target\":103,\"rationale\":\"oversold bounce\"}");

        ReviewOutcome.Suggest suggest = (await Reviewer().ReviewAsync(Context(), CancellationToken.None))
            .Should().BeOfType<ReviewOutcome.Suggest>().Subject;
        suggest.Side.Should().Be(OrderSide.Buy);
        suggest.EntryPrice.Should().Be(100.5m);
        suggest.StopPrice.Should().Be(99m);
        suggest.TargetPrice.Should().Be(103m);
        suggest.Rationale.Should().Be("oversold bounce");
    }

    [Fact]
    public async Task ReviewAsync_ShouldMapShortToSell()
    {
        ProviderReturns("{\"decision\":\"suggest\",\"direction\":\"short\",\"entry\":100,\"stop\":101,\"target\":97}");

        (await Reviewer().ReviewAsync(Context(), CancellationToken.None))
            .Should().BeOfType<ReviewOutcome.Suggest>().Which.Side.Should().Be(OrderSide.Sell);
    }

    [Fact]
    public async Task ReviewAsync_ShouldSuppressNotWorthSurfacing_WhenTheModelDeclines()
    {
        ProviderReturns("{\"decision\":\"suppress\",\"reason\":\"chop\"}");

        ReviewOutcome.Suppress suppress = (await Reviewer().ReviewAsync(Context(), CancellationToken.None))
            .Should().BeOfType<ReviewOutcome.Suppress>().Subject;
        suppress.Reason.Should().Be(SuppressReason.NotWorthSurfacing);
        suppress.Detail.Should().Be("chop");
    }

    [Fact]
    public async Task ReviewAsync_ShouldSuppressMalformed_WhenTheModelRefusedOrTruncated()
    {
        // A syntactically-perfect suggest that arrived under a Refusal stop is NOT a proposal -- fail closed.
        ProviderReturns(
            "{\"decision\":\"suggest\",\"direction\":\"long\",\"entry\":1,\"stop\":0.5,\"target\":2}",
            LlmStopReason.Refusal);

        (await Reviewer().ReviewAsync(Context(), CancellationToken.None))
            .Should().BeOfType<ReviewOutcome.Suppress>().Which.Reason.Should().Be(SuppressReason.MalformedOutput);
    }

    [Theory]
    [InlineData("")]                                                                            // empty
    [InlineData("this is not json")]                                                            // unparseable
    [InlineData("{\"decision\":\"ponder\"}")]                                                   // unknown decision
    [InlineData("{\"decision\":\"suggest\",\"direction\":\"sideways\",\"entry\":1,\"stop\":0.5,\"target\":2}")] // unknown direction
    [InlineData("{\"decision\":\"suggest\",\"direction\":\"long\",\"stop\":0.5,\"target\":2}")] // missing entry
    public async Task ReviewAsync_ShouldSuppressMalformed_ForUnusableOutput(string body)
    {
        ProviderReturns(body);

        (await Reviewer().ReviewAsync(Context(), CancellationToken.None))
            .Should().BeOfType<ReviewOutcome.Suppress>().Which.Reason.Should().Be(SuppressReason.MalformedOutput);
    }

    [Theory]
    [InlineData(null)]  // a genuine null Text -- would throw ArgumentNullException out of the deserializer if unguarded
    [InlineData("   ")] // whitespace-only, on an otherwise-clean stop
    public async Task ReviewAsync_ShouldSuppressMalformed_WhenTheCompletionTextIsEmpty(string? body)
    {
        ProviderReturns(body!);

        (await Reviewer().ReviewAsync(Context(), CancellationToken.None))
            .Should().BeOfType<ReviewOutcome.Suppress>().Which.Reason.Should().Be(SuppressReason.MalformedOutput);
    }

    [Fact]
    public async Task ReviewAsync_ShouldSuppressReviewerUnavailable_WhenTheProviderThrows()
    {
        // The seam is provider-neutral; a real HTTP-backed provider throws on a network error / timeout / 429 / 5xx.
        // The reviewer's fail-closed contract is TOTAL: a throw becomes a suppression, never an escape and never a
        // suggestion -- distinctly ReviewerUnavailable (a configured reviewer was tried and failed).
        A.CallTo(() => _llm.CompleteAsync(A<LlmRequest>._, A<CancellationToken>._))
            .Throws(new InvalidOperationException("provider 503"));

        (await Reviewer().ReviewAsync(Context(), CancellationToken.None))
            .Should().BeOfType<ReviewOutcome.Suppress>().Which.Reason.Should().Be(SuppressReason.ReviewerUnavailable);
    }

    [Fact]
    public async Task ReviewAsync_ShouldPropagate_WhenTheCallerCancels()
    {
        // Our own shutdown is NOT a review outcome: a cancellation on the caller's token propagates, never suppresses.
        using CancellationTokenSource cts = new();
        await cts.CancelAsync();
        A.CallTo(() => _llm.CompleteAsync(A<LlmRequest>._, A<CancellationToken>._))
            .Throws(new OperationCanceledException(cts.Token));

        Func<Task> act = () => Reviewer().ReviewAsync(Context(), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task ReviewAsync_ShouldRequestTheTriageTierAndAJsonSchema()
    {
        LlmRequest? captured = null;
        A.CallTo(() => _llm.CompleteAsync(A<LlmRequest>._, A<CancellationToken>._))
            .Invokes((LlmRequest request, CancellationToken _) => captured = request)
            .Returns(new LlmCompletion("{\"decision\":\"suppress\"}", LlmStopReason.Completed, LlmUsage.None));

        await Reviewer().ReviewAsync(Context(), CancellationToken.None);

        captured.Should().NotBeNull();
        captured!.Tier.Should().Be(LlmModelTier.Triage);
        captured.ResponseFormat.JsonSchema.Should().NotBeNull();
        captured.Messages.Should().ContainSingle();
    }
}
