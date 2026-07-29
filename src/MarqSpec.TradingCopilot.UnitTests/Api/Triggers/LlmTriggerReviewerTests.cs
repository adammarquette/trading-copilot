using FakeItEasy;
using MarqSpec.TradingCopilot.Api.Ai;
using MarqSpec.TradingCopilot.Api.Triggers;
using MarqSpec.TradingCopilot.Domain;
using MarqSpec.TradingCopilot.Domain.Ai;
using MarqSpec.TradingCopilot.Domain.Triggers;
using MarqSpec.TradingCopilot.Domain.Venue;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MarqSpec.TradingCopilot.UnitTests.Api.Triggers;

/// <summary>
/// The LLM-backed reviewer (gh#402, ADR-0008). The design-defining behaviour is FAIL-CLOSED: a well-formed
/// <i>suggest</i> maps to a <see cref="ReviewOutcome.Suggest"/> with the right side + prices, and <b>everything
/// else</b> — a decline, a refusal / truncation, unparseable JSON, an unknown decision or direction, a missing
/// price — becomes a <see cref="ReviewOutcome.Suppress"/>, never a suggestion. It also asks for the cheap triage tier
/// and a JSON-schema-constrained response. The review now rides on an <see cref="AgentReview"/> — the outcome the
/// route acts on plus the <see cref="AiCallCost"/> the owner-scoped caller ledgers (gh#431).
/// </summary>
public class LlmTriggerReviewerTests
{
    private readonly ILlmProvider _llm = A.Fake<ILlmProvider>();

    private LlmTriggerReviewer Reviewer() =>
        new(_llm, Options.Create(new LlmOptions()), NullLogger<LlmTriggerReviewer>.Instance);

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

        ReviewOutcome.Suggest suggest = (await Reviewer().ReviewAsync(Context(), CancellationToken.None)).Outcome
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

        (await Reviewer().ReviewAsync(Context(), CancellationToken.None)).Outcome
            .Should().BeOfType<ReviewOutcome.Suggest>().Which.Side.Should().Be(OrderSide.Sell);
    }

    [Fact]
    public async Task ReviewAsync_ShouldSuppressNotWorthSurfacing_WhenTheModelDeclines()
    {
        ProviderReturns("{\"decision\":\"suppress\",\"reason\":\"chop\"}");

        ReviewOutcome.Suppress suppress = (await Reviewer().ReviewAsync(Context(), CancellationToken.None)).Outcome
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

        (await Reviewer().ReviewAsync(Context(), CancellationToken.None)).Outcome
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

        (await Reviewer().ReviewAsync(Context(), CancellationToken.None)).Outcome
            .Should().BeOfType<ReviewOutcome.Suppress>().Which.Reason.Should().Be(SuppressReason.MalformedOutput);
    }

    [Theory]
    [InlineData(null)]  // a genuine null Text -- would throw ArgumentNullException out of the deserializer if unguarded
    [InlineData("   ")] // whitespace-only, on an otherwise-clean stop
    public async Task ReviewAsync_ShouldSuppressMalformed_WhenTheCompletionTextIsEmpty(string? body)
    {
        ProviderReturns(body!);

        (await Reviewer().ReviewAsync(Context(), CancellationToken.None)).Outcome
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

        (await Reviewer().ReviewAsync(Context(), CancellationToken.None)).Outcome
            .Should().BeOfType<ReviewOutcome.Suppress>().Which.Reason.Should().Be(SuppressReason.ReviewerUnavailable);
    }

    [Fact]
    public async Task ReviewAsync_ShouldRecordAFailedZeroTokenCost_WhenTheProviderThrows()
    {
        // A provider fault is billable LATENCY the governor must see, even though it produced no tokens: the fail-closed
        // throw path still carries a non-null Cost, outcome Failed, zero tokens -- the exact failure regime a
        // success-only ledger would silently miss (gh#431). The owner is NOT the reviewer's to stamp, so Cost has none.
        A.CallTo(() => _llm.CompleteAsync(A<LlmRequest>._, A<CancellationToken>._))
            .Throws(new InvalidOperationException("provider 503"));

        AiCallCost? cost = (await Reviewer().ReviewAsync(Context(), CancellationToken.None)).Cost;

        cost.Should().NotBeNull();
        cost!.Feature.Should().Be(AiUsageFeature.Triage);
        cost.Tier.Should().Be(LlmModelTier.Triage);
        cost.Model.Should().Be("claude-haiku-4-5"); // the default triage model, resolved via LlmOptions.ModelFor
        cost.Outcome.Should().Be(AiUsageOutcome.Failed);
        cost.InputTokens.Should().Be(0);
        cost.OutputTokens.Should().Be(0);
        cost.EstimatedCostUsd.Should().Be(0m);
    }

    [Theory]
    // The SPEND outcome is orthogonal to the REVIEW outcome: a Completed call the reviewer then suppresses (a decline,
    // malformed JSON) still Succeeded (it was billed), a refusal is Refused, a truncation Truncated (gh#431).
    [InlineData("{\"decision\":\"suppress\",\"reason\":\"chop\"}", LlmStopReason.Completed, AiUsageOutcome.Succeeded)]
    [InlineData("{\"decision\":\"suggest\",\"direction\":\"long\",\"entry\":1,\"stop\":0.5,\"target\":2}", LlmStopReason.Refusal, AiUsageOutcome.Refused)]
    [InlineData("{\"decision\":\"suggest\",\"direction\":\"long\",\"entry\":1,\"stop\":0.5,\"target\":2}", LlmStopReason.MaxTokens, AiUsageOutcome.Truncated)]
    public async Task ReviewAsync_ShouldMapTheSpendOutcome_FromTheStopReason(
        string body, LlmStopReason stop, AiUsageOutcome expected)
    {
        ProviderReturns(body, stop);

        AiCallCost? cost = (await Reviewer().ReviewAsync(Context(), CancellationToken.None)).Cost;

        cost.Should().NotBeNull();
        cost!.Outcome.Should().Be(expected);
        cost.Feature.Should().Be(AiUsageFeature.Triage);
    }

    [Fact]
    public async Task ReviewAsync_ShouldPriceTheCallFromUsage_OnACompletedCall()
    {
        // The cost is computed ONCE from the completion's usage, at the triage rates ($1/$5 per million by default), so
        // every suggest/suppress return path carries a real spend row (gh#431).
        A.CallTo(() => _llm.CompleteAsync(A<LlmRequest>._, A<CancellationToken>._))
            .Returns(new LlmCompletion(
                "{\"decision\":\"suppress\",\"reason\":\"chop\"}", LlmStopReason.Completed, new LlmUsage(1_000_000, 1_000_000)));

        AiCallCost? cost = (await Reviewer().ReviewAsync(Context(), CancellationToken.None)).Cost;

        cost.Should().NotBeNull();
        cost!.InputTokens.Should().Be(1_000_000);
        cost.OutputTokens.Should().Be(1_000_000);
        cost.EstimatedCostUsd.Should().Be(6.00m); // 1M * $1/M input + 1M * $5/M output
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
