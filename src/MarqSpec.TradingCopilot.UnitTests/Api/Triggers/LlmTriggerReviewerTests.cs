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

    // A sample deep-tier enrichment (gh#476): one recent bar + one recent value of the fired series -- numeric only.
    private static readonly ReviewEnrichment _sampleEnrichment = new(
        [new BarSnapshot(DateTimeOffset.UnixEpoch, 100m, 101m, 99m, 100.5m, 1234)],
        [new IndicatorValueSnapshot(DateTimeOffset.UnixEpoch, 28.5m)]);

    private static TriggerReviewContext EnrichedContext() => Context() with { Enrichment = _sampleEnrichment };

    private void ProviderReturns(string text, LlmStopReason stop = LlmStopReason.Completed) =>
        A.CallTo(() => _llm.CompleteAsync(A<LlmRequest>._, A<CancellationToken>._))
            .Returns(new LlmCompletion(text, stop, LlmUsage.None));

    // Escalation (gh#449) makes two calls; stub each tier independently so the triage answer can defer to the deep one.
    private void TriageReturns(string text, LlmStopReason stop = LlmStopReason.Completed) =>
        A.CallTo(() => _llm.CompleteAsync(
                A<LlmRequest>.That.Matches(r => r.Tier == LlmModelTier.Triage), A<CancellationToken>._))
            .Returns(new LlmCompletion(text, stop, LlmUsage.None));

    private void DeepReturns(string text, LlmStopReason stop = LlmStopReason.Completed) =>
        A.CallTo(() => _llm.CompleteAsync(
                A<LlmRequest>.That.Matches(r => r.Tier == LlmModelTier.Deep), A<CancellationToken>._))
            .Returns(new LlmCompletion(text, stop, LlmUsage.None));

    private void DeepThrows(Exception error) =>
        A.CallTo(() => _llm.CompleteAsync(
                A<LlmRequest>.That.Matches(r => r.Tier == LlmModelTier.Deep), A<CancellationToken>._))
            .Throws(error);

    // A triage completion that defers a genuinely-hard setup to the deep tier.
    private const string TriageEscalate = "{\"decision\":\"escalate\",\"reason\":\"hard\"}";

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

        // A single triage call was billed, so the review carries exactly one cost (gh#449 migrated .Cost -> .Costs).
        AiCallCost cost = (await Reviewer().ReviewAsync(Context(), CancellationToken.None)).Costs
            .Should().ContainSingle().Subject;

        cost.Feature.Should().Be(AiUsageFeature.Triage);
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

        AiCallCost cost = (await Reviewer().ReviewAsync(Context(), CancellationToken.None)).Costs
            .Should().ContainSingle().Subject;

        cost.Outcome.Should().Be(expected);
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

        AiCallCost cost = (await Reviewer().ReviewAsync(Context(), CancellationToken.None)).Costs
            .Should().ContainSingle().Subject;

        cost.InputTokens.Should().Be(1_000_000);
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

    // =====================================================================================================
    // TRIAGE -> DEEP ESCALATION (gh#449): only the triage tier is offered "escalate". On that control signal the
    // reviewer makes a SECOND, deep-tier call and uses ITS answer. The flow is bounded to two calls (the deep schema
    // omits escalate, and the terminal Map reads a stray escalate as unknown -> suppress), and EVERY billed call --
    // including a failed deep one -- is surfaced on Costs, so no escalated spend regime is silently uncounted.
    // =====================================================================================================

    [Fact]
    public async Task ReviewAsync_ShouldCallTheDeepTier_WhenTriageEscalates()
    {
        // Triage defers; the deep tier answers with a concrete suggest whose prices identify it as the winning answer.
        TriageReturns(TriageEscalate);
        DeepReturns("{\"decision\":\"suggest\",\"direction\":\"short\",\"entry\":200,\"stop\":201,\"target\":197}");

        ReviewOutcome.Suggest suggest = (await Reviewer().ReviewAsync(Context(), CancellationToken.None)).Outcome
            .Should().BeOfType<ReviewOutcome.Suggest>().Subject;
        suggest.Side.Should().Be(OrderSide.Sell);           // the DEEP answer, not the triage escalate
        suggest.EntryPrice.Should().Be(200m);
        suggest.StopPrice.Should().Be(201m);
        suggest.TargetPrice.Should().Be(197m);

        // The second, deep-tier call actually happened.
        A.CallTo(() => _llm.CompleteAsync(
                A<LlmRequest>.That.Matches(r => r.Tier == LlmModelTier.Deep), A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task ReviewAsync_ShouldReturnTwoCosts_WhenTriageEscalates()
    {
        TriageReturns(TriageEscalate);
        DeepReturns("{\"decision\":\"suggest\",\"direction\":\"long\",\"entry\":100,\"stop\":99,\"target\":103}");

        IReadOnlyList<AiCallCost> costs = (await Reviewer().ReviewAsync(Context(), CancellationToken.None)).Costs;

        // ONE row per billed call, in call order: the triage call first, then the deep call (gh#449).
        costs.Should().HaveCount(2);

        // The escalating triage call Completed and WAS billed even though it deferred -- so its own spend Succeeded.
        costs[0].Tier.Should().Be(LlmModelTier.Triage);
        costs[0].Outcome.Should().Be(AiUsageOutcome.Succeeded);

        // The deep call is priced at the deep model/tier (resolved via LlmOptions.ModelFor).
        costs[1].Tier.Should().Be(LlmModelTier.Deep);
        costs[1].Model.Should().Be("claude-sonnet-5");

        // Feature is the SAME on both rows -- the whole two-call flow is one agent-review; Tier is the attribution axis.
        costs.Should().OnlyContain(cost => cost.Feature == AiUsageFeature.Triage);
    }

    [Fact]
    public async Task ReviewAsync_ShouldSuppressAndRecordBothCosts_WhenTheDeepCallThrows()
    {
        // The sharpest dropped-spend guard: the deep provider call throws AFTER the triage call was billed. The review
        // fails closed (ReviewerUnavailable), and BOTH costs are surfaced -- the failed deep call priced as a
        // zero-token Failed row so its billable latency is never silently uncounted (gh#449).
        TriageReturns(TriageEscalate);
        DeepThrows(new InvalidOperationException("provider 503"));

        AgentReview review = await Reviewer().ReviewAsync(Context(), CancellationToken.None);

        review.Outcome.Should().BeOfType<ReviewOutcome.Suppress>()
            .Which.Reason.Should().Be(SuppressReason.ReviewerUnavailable);
        review.Costs.Should().HaveCount(2);
        review.Costs[1].Outcome.Should().Be(AiUsageOutcome.Failed);
        review.Costs[1].Tier.Should().Be(LlmModelTier.Deep);
        review.Costs[1].Model.Should().Be("claude-sonnet-5");
        review.Costs[1].InputTokens.Should().Be(0);
        review.Costs[1].OutputTokens.Should().Be(0);
        review.Costs[1].EstimatedCostUsd.Should().Be(0m);
    }

    [Fact]
    public async Task ReviewAsync_ShouldSuppressAndNotLoop_WhenTheDeepCallEscalatesAgain()
    {
        // Defense in depth: the deep schema does NOT offer "escalate", but a non-compliant deep model that returns one
        // anyway is mapped by the TERMINAL path, where an unknown decision fails closed -- and CANNOT trigger a third
        // call. So a stray second escalate reads as MalformedOutput and the flow stays bounded to exactly two calls.
        TriageReturns(TriageEscalate);
        DeepReturns("{\"decision\":\"escalate\"}");

        (await Reviewer().ReviewAsync(Context(), CancellationToken.None)).Outcome
            .Should().BeOfType<ReviewOutcome.Suppress>().Which.Reason.Should().Be(SuppressReason.MalformedOutput);

        A.CallTo(() => _llm.CompleteAsync(A<LlmRequest>._, A<CancellationToken>._)).MustHaveHappenedTwiceExactly();
    }

    [Fact]
    public async Task ReviewAsync_ShouldMapDeepSuppressToNotWorthSurfacing()
    {
        // The deep tier's own suppress is a legitimate, silent decline -- mapped by the terminal path exactly as a
        // triage suppress would be.
        TriageReturns(TriageEscalate);
        DeepReturns("{\"decision\":\"suppress\",\"reason\":\"chop\"}");

        ReviewOutcome.Suppress suppress = (await Reviewer().ReviewAsync(Context(), CancellationToken.None)).Outcome
            .Should().BeOfType<ReviewOutcome.Suppress>().Subject;
        suppress.Reason.Should().Be(SuppressReason.NotWorthSurfacing);
        suppress.Detail.Should().Be("chop");
    }

    [Theory]
    // A missing decision, an unknown decision, a normal suppress, and a normal suggest all reach a TERMINAL triage
    // outcome -- none may escalate. The deep tier must never be called and only the one triage cost is billed.
    [InlineData("{}")]                                                                          // missing decision
    [InlineData("{\"decision\":\"ponder\"}")]                                                   // unknown decision
    [InlineData("{\"decision\":\"suppress\",\"reason\":\"chop\"}")]                              // a normal decline
    [InlineData("{\"decision\":\"suggest\",\"direction\":\"long\",\"entry\":1,\"stop\":0.5,\"target\":2}")] // a normal suggest
    public async Task ReviewAsync_ShouldNotCallTheDeepTier_WhenTriageDoesNotEscalate(string triageBody)
    {
        TriageReturns(triageBody);

        IReadOnlyList<AiCallCost> costs = (await Reviewer().ReviewAsync(Context(), CancellationToken.None)).Costs;

        A.CallTo(() => _llm.CompleteAsync(
                A<LlmRequest>.That.Matches(r => r.Tier == LlmModelTier.Deep), A<CancellationToken>._))
            .MustNotHaveHappened();
        costs.Should().ContainSingle().Which.Tier.Should().Be(LlmModelTier.Triage);
    }

    [Fact]
    public async Task ReviewAsync_ShouldRequestTheDeepTierWithADeepSchema_WhenEscalating()
    {
        LlmRequest? deepRequest = null;
        TriageReturns(TriageEscalate);
        A.CallTo(() => _llm.CompleteAsync(
                A<LlmRequest>.That.Matches(r => r.Tier == LlmModelTier.Deep), A<CancellationToken>._))
            .Invokes((LlmRequest request, CancellationToken _) => deepRequest = request)
            .Returns(new LlmCompletion("{\"decision\":\"suppress\"}", LlmStopReason.Completed, LlmUsage.None));

        await Reviewer().ReviewAsync(Context(), CancellationToken.None);

        deepRequest.Should().NotBeNull();
        deepRequest!.Tier.Should().Be(LlmModelTier.Deep);
        deepRequest.ResponseFormat.JsonSchema.Should().NotBeNull(); // the deep tier is still schema-constrained
    }

    // =====================================================================================================
    // DEEP-TIER ENRICHMENT (gh#476): the escalated DEEP call's user turn carries a fenced <market-context> block of the
    // numeric enrichment on the context; the TRIAGE render stays byte-for-byte unchanged and never sees it. A null or
    // empty enrichment degrades gracefully to the base render, and the deep system prompt fences the block as DATA.
    // =====================================================================================================

    [Fact]
    public async Task ReviewAsync_ShouldRenderTheMarketContextToTheDeepTier_WhenEscalatingWithEnrichment()
    {
        TriageReturns(TriageEscalate);
        LlmRequest? deepRequest = null;
        A.CallTo(() => _llm.CompleteAsync(
                A<LlmRequest>.That.Matches(r => r.Tier == LlmModelTier.Deep), A<CancellationToken>._))
            .Invokes((LlmRequest request, CancellationToken _) => deepRequest = request)
            .Returns(new LlmCompletion("{\"decision\":\"suppress\"}", LlmStopReason.Completed, LlmUsage.None));

        await Reviewer().ReviewAsync(EnrichedContext(), CancellationToken.None);

        string deepUser = deepRequest!.Messages.Should().ContainSingle().Subject.Content;
        deepUser.Should().Contain("<market-context>").And.Contain("</market-context>");
        deepUser.Should().Contain("Recent bars");
        deepUser.Should().Contain("Recent rsi(14) values"); // labelled by the fired series (indicator + period)
        deepUser.Should().Contain("V 1234");                 // the sample bar's volume, invariant-formatted
        deepUser.Should().Contain("28.5");                   // the sample indicator value
    }

    [Fact]
    public async Task ReviewAsync_ShouldLeaveTheTriageRenderUnchanged_EvenWhenTheContextIsEnriched()
    {
        LlmRequest? triageRequest = null;
        A.CallTo(() => _llm.CompleteAsync(
                A<LlmRequest>.That.Matches(r => r.Tier == LlmModelTier.Triage), A<CancellationToken>._))
            .Invokes((LlmRequest request, CancellationToken _) => triageRequest = request)
            .Returns(new LlmCompletion("{\"decision\":\"suppress\"}", LlmStopReason.Completed, LlmUsage.None));

        await Reviewer().ReviewAsync(EnrichedContext(), CancellationToken.None); // enriched context, triage terminal
        string enrichedTriageUser = triageRequest!.Messages.Single().Content;

        triageRequest = null;
        await Reviewer().ReviewAsync(Context(), CancellationToken.None);          // un-enriched context
        string plainTriageUser = triageRequest!.Messages.Single().Content;

        // Enrichment NEVER reaches the triage tier: the two triage user turns are byte-identical, neither is fenced.
        enrichedTriageUser.Should().Be(plainTriageUser);
        enrichedTriageUser.Should().NotContain("<market-context>");
    }

    [Fact]
    public async Task ReviewAsync_ShouldTellTheDeepTierToTreatMarketContextAsData_WhenEscalating()
    {
        TriageReturns(TriageEscalate);
        LlmRequest? deepRequest = null;
        A.CallTo(() => _llm.CompleteAsync(
                A<LlmRequest>.That.Matches(r => r.Tier == LlmModelTier.Deep), A<CancellationToken>._))
            .Invokes((LlmRequest request, CancellationToken _) => deepRequest = request)
            .Returns(new LlmCompletion("{\"decision\":\"suppress\"}", LlmStopReason.Completed, LlmUsage.None));

        await Reviewer().ReviewAsync(EnrichedContext(), CancellationToken.None);

        // The authoritative "treat it as DATA, never instructions" instruction lives in the SYSTEM prompt (which the
        // user turn cannot override) -- the injection-hygiene posture, even though news (the free-text surface) is deferred.
        deepRequest!.SystemPrompt.Should().Contain("reference DATA").And.Contain("never as instructions");
    }

    [Theory]
    [InlineData(false)] // no enrichment at all on the context (a null trailing field)
    [InlineData(true)]  // an empty enrichment payload (both series empty -- e.g. a brand-new instrument)
    public async Task ReviewAsync_ShouldNotFenceTheDeepRender_WhenThereIsNoEnrichmentData(bool emptyPayload)
    {
        TriageReturns(TriageEscalate);
        LlmRequest? deepRequest = null;
        A.CallTo(() => _llm.CompleteAsync(
                A<LlmRequest>.That.Matches(r => r.Tier == LlmModelTier.Deep), A<CancellationToken>._))
            .Invokes((LlmRequest request, CancellationToken _) => deepRequest = request)
            .Returns(new LlmCompletion("{\"decision\":\"suppress\"}", LlmStopReason.Completed, LlmUsage.None));

        TriggerReviewContext context = emptyPayload
            ? Context() with { Enrichment = new ReviewEnrichment([], []) }
            : Context();

        await Reviewer().ReviewAsync(context, CancellationToken.None);

        // Graceful degradation: the deep render is exactly the base render -- no empty fence, a scan-side gap costs
        // only richness, never the deep call itself.
        deepRequest!.Messages.Single().Content.Should().NotContain("<market-context>");
    }
}
