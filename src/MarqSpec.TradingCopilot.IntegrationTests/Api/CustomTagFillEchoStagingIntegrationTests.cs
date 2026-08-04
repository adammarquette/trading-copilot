using MarqSpec.TradingCopilot.Api.Orders;
using MarqSpec.TradingCopilot.Domain.Venue;
using MarqSpec.TradingCopilot.IntegrationTests.TestHost.Staging;
using Xunit;
using ClientModels = MarqSpec.Client.ProjectX.Api.Models;

namespace MarqSpec.TradingCopilot.IntegrationTests.Api;

/// <summary>
/// <b>Post-merge staging gate (gh#643 ⇒ gh#631).</b> #631's fill-level reconcile stops a stranded one-shot
/// conditional from being re-armed after its entry already executed, by asking the venue whether an order stamped
/// with a given <c>customTag</c> ever <b>filled</b>. That rests on an assumption no unit test can certify — every
/// unit test necessarily fakes the gateway — that ProjectX <b>echoes <c>customTag</c> on a TERMINAL (filled) order
/// record</b>. The echo is proven for the placement acknowledgement and for resting orders; it is proven
/// <b>nowhere</b> for a filled, round-tripped order — the only record #631's mitigation ever reads. If the echo
/// does not happen, every answer becomes <c>NoFillFound</c>, the veto silently never fires, and the gh#622 hazard
/// is exactly as open as before, with the unit suite green throughout.
/// </summary>
/// <remarks>
/// <para>
/// Two gates, deliberately kept separate, over the <b>same</b> place → fill → flatten round trip
/// (<see cref="PlaceFillAndFlattenTaggedOrderAsync"/>): <see cref="FillHistory_ShouldEchoTheCustomTag_OnATerminalRoundTrippedOrder"/>
/// reads <b>venue truth directly from the gateway</b> (<see cref="StagingProjectXGateway.ExecutedOrderWithTag"/>) —
/// the same pattern as the bracket gates (gh#269, gh#293, PR #374) — using a match predicate written independently
/// of, and never delegating to, the production matching. <see cref="FindFilledOrderByTagAsync_ShouldReportTheFillAsAVeto_OnATerminalRoundTrippedOrder"/>
/// then calls the production seam itself, <c>IOrderExecutor.FindFilledOrderByTagAsync</c> on a real
/// <see cref="MarqSpec.TradingCopilot.Integration.ProjectX.ProjectXVenue"/> (<see cref="StagingProjectXGateway.FindFilledOrderByTagAsync"/>).
/// </para>
/// <para>
/// <b>Why both, and what each failure means.</b> gh#643's question is "is the veto inert?", and a gate that only
/// ever proves the venue characteristic — without ever executing the production path — cannot answer that: if the
/// echo works but the production method has its own bug (a wrong search window, a case-sensitive comparison, a
/// mapping slip), the venue-truth gate stays green while the veto stays dead. Run together, the two gates are a
/// diagnostic: the independent gate red = the venue does not echo the tag (the gh#643 assumption is false, and the
/// PR #637 schema-mismatch question — <c>SearchOrderRequest</c> serialises <c>startTime</c>/<c>endTime</c>; the
/// gateway may expect <c>startTimestamp</c>/<c>endTimestamp</c> — is the first thing to check). The production-seam
/// gate red <b>while the independent gate is green</b> = the venue is fine and the implementation is wrong. Both
/// red together = the venue characteristic is false and nothing downstream of it can be trusted either.
/// </para>
/// <para>
/// It SKIPS pre-merge via <see cref="StagingGatewayFactAttribute"/> and is serialized onto the reserved practice
/// account.
/// </para>
/// </remarks>
[Trait("Category", "Staging")]
[Collection(StagingExecutionCollection.Name)]
public sealed class CustomTagFillEchoStagingIntegrationTests : StagingVenueExecutionSuite
{
    [StagingVenueFact]
    public async Task PracticeAccount_ShouldResolveThroughTheDeployedApi_ForTheCustomTagEchoGate()
    {
        // Precondition for the gates below: the reserved practice account is discoverable through the deployed API.
        // If this fails, neither gate can run — so it is asserted first and on its own.
        using HttpClient client = await AuthenticatedClientAsync();
        Guid accountId = await ResolvePracticeAccountAsync(client);

        accountId.Should().NotBe(Guid.Empty, "the reserved practice account resolved on staging");
    }

    /// <summary>
    /// The independent venue-truth half of the gh#643 gate. Reads three venue-truth surfaces after the round trip:
    /// the order-history read must still show the executed order under its tag, while the resting-orders read and
    /// the positions read — the two surfaces gh#631's reconcile would otherwise be limited to — see nothing at all.
    /// </summary>
    /// <remarks>
    /// <b>That contrast is the gate.</b> It is exactly the round-tripped case the resting-orders and positions
    /// reads structurally cannot see (a filled order is no longer working, and a closed position reads flat), and
    /// it is the only case gh#631's mitigation exists to cover. Prove-the-red: if ProjectX does not echo
    /// <c>customTag</c> onto the terminal (filled) order record, <see cref="StagingProjectXGateway.ExecutedOrderWithTag"/>
    /// returns <see langword="null"/> below — the same "no rows" answer a genuine no-fill would produce, which is
    /// precisely the silent-inert failure mode this gate exists to catch before #631's veto is relied on for a
    /// live account. This test's own match predicate never calls the production method — see the class remarks for
    /// what distinguishes a red here from a red in <see cref="FindFilledOrderByTagAsync_ShouldReportTheFillAsAVeto_OnATerminalRoundTrippedOrder"/>.
    /// </remarks>
    [StagingGatewayFact]
    public async Task FillHistory_ShouldEchoTheCustomTag_OnATerminalRoundTrippedOrder()
    {
        const int size = 1;
        (StagingProjectXGateway gateway, int gatewayAccountId, string customTag, DateTime since) =
            await PlaceFillAndFlattenTaggedOrderAsync(size);
        await using (gateway)
        {
            string symbol = StagingConfig.ExecutionInstrument!;

            // The two reads gh#631's reconcile would otherwise be limited to — by construction they see NOTHING
            // once the account is flat again, because neither surface retains a terminal record.
            IReadOnlyList<ClientModels.Order> resting = await gateway.OpenOrdersAsync(gatewayAccountId);
            resting.Should().NotContain(order => order.CustomTag == customTag,
                "the entry and its bracket left the book on fill/flatten — nothing rests under this tag any more");
            bool stillOpen = await gateway.HasOpenPositionAsync(gatewayAccountId, symbol);
            stillOpen.Should().BeFalse("the round-trip flattened the account back to flat");

            // THE GATE: the order-history read — the only surface gh#631's mitigation actually reads — must still
            // show the executed order under its tag, with a nonzero executed quantity. If ProjectX does not echo
            // `customTag` on the terminal record, this returns null and the assertion below is what goes red.
            IReadOnlyList<ClientModels.Order> history = await gateway.GetOrdersAsync(gatewayAccountId, since);
            ClientModels.Order? executed = StagingProjectXGateway.ExecutedOrderWithTag(history, customTag);
            executed.Should().NotBeNull(
                "the venue's order history must echo customTag on the terminal (filled) record — the gh#643 assumption");
            executed!.FillVolume.Should().Be(size, "the executed quantity matches what was placed and filled");
        }
    }

    /// <summary>
    /// The production-seam half of the gh#643 gate. Calls <c>IOrderExecutor.FindFilledOrderByTagAsync</c> directly
    /// (via <see cref="StagingProjectXGateway.FindFilledOrderByTagAsync"/>, which builds a real
    /// <see cref="MarqSpec.TradingCopilot.Integration.ProjectX.ProjectXVenue"/>) over the same round trip as
    /// <see cref="FillHistory_ShouldEchoTheCustomTag_OnATerminalRoundTrippedOrder"/>, and asserts the
    /// <see cref="TaggedFillEvidence"/> contract gh#631's veto actually reads: <see cref="TaggedFillStatus.Filled"/>,
    /// <see cref="TaggedFillEvidence.VetoesRelease"/>, and a <see cref="TaggedFillEvidence.FilledSize"/> matching
    /// what executed.
    /// </summary>
    /// <remarks>
    /// This is the one exercise of the actual production code path in this suite — everything else in this class
    /// deliberately avoids it (QA independence: proving the assumption, not re-running the implementation and
    /// calling that proof). Prove-the-red: if the venue echo is intact but the production matching has a bug of
    /// its own — the wrong search window, a case-sensitive tag comparison the venue does not preserve, a mapping
    /// slip onto <see cref="TaggedFillEvidence"/> — this test goes red <b>while</b>
    /// <see cref="FillHistory_ShouldEchoTheCustomTag_OnATerminalRoundTrippedOrder"/> stays green, which is exactly
    /// how the two gates together tell "venue problem" apart from "our code is wrong" (class remarks).
    /// </remarks>
    [StagingGatewayFact]
    public async Task FindFilledOrderByTagAsync_ShouldReportTheFillAsAVeto_OnATerminalRoundTrippedOrder()
    {
        const int size = 1;
        (StagingProjectXGateway gateway, int gatewayAccountId, string customTag, DateTime since) =
            await PlaceFillAndFlattenTaggedOrderAsync(size);
        await using (gateway)
        {
            TaggedFillEvidence evidence = await gateway.FindFilledOrderByTagAsync(
                gatewayAccountId, customTag, new DateTimeOffset(since));

            // THE GATE: the production seam itself must report the round-tripped fill — not NoFillFound, not
            // Unavailable, not Unsupported. If this is anything other than Filled while the independent read above
            // still finds the tag, the defect is in our matching, not the venue's echo (class remarks).
            evidence.Status.Should().Be(TaggedFillStatus.Filled,
                "the production seam must report the round-tripped fill as Filled, not NoFillFound — the gh#631 "
                + "veto is dead otherwise, even though the venue itself echoed the tag");
            evidence.VetoesRelease.Should().BeTrue(
                "a Filled status always vetoes a release/re-arm — TaggedFillEvidence's own invariant");
            evidence.FilledSize.Should().Be(size, "the reported executed quantity matches what was placed and filled");
        }
    }

    /// <summary>
    /// The shared gh#643 round trip both gates above read: resolves the practice account and a real
    /// production-backed gateway, places a bracketed resting long carrying a known <c>customTag</c> — the app's own
    /// journaled order id (<c>OrderEndpoints</c> matches resting orders on <c>CustomTag == id.ToString()</c>), so
    /// the placed order's id IS the tag both gates look for — reprices it marketable to force a fill, then flattens
    /// so the position and its bracket round-trip and the account goes flat again. This is the exact state gh#631's
    /// reconcile observes for a stranded conditional whose entry already executed and was cleaned up elsewhere.
    /// Ownership of the returned <see cref="StagingProjectXGateway"/> passes to the caller, which must dispose it.
    /// </summary>
    private static async Task<(StagingProjectXGateway Gateway, int GatewayAccountId, string CustomTag, DateTime Since)>
        PlaceFillAndFlattenTaggedOrderAsync(int size)
    {
        string symbol = StagingConfig.ExecutionInstrument!;

        using HttpClient app = await AuthenticatedClientAsync();
        Guid accountId = await ResolvePracticeAccountAsync(app);
        StagingProjectXGateway gateway = StagingProjectXGateway.Create();
        int gatewayAccountId = await gateway.ResolveAccountIdAsync();
        ClientModels.Contract contract = await gateway.ResolveContractAsync(symbol);
        decimal pointValue = contract.TickSize == 0m ? contract.TickValue : contract.TickValue / contract.TickSize;

        // Start flat — the collection serializes, but a prior crash could have left residue on the shared account.
        await gateway.FlattenAsync(gatewayAccountId, contract.Id);
        await DeclareRiskAsync(app, accountId, maxContractsPerOrder: size);
        decimal market = await gateway.ReferencePriceAsync(contract.Id);

        // A few minutes' margin before the transmit, covering clock skew between this process and the gateway —
        // mirroring how the production caller derives "since" from the row's own durable transmit instant.
        DateTime since = DateTime.UtcNow.AddMinutes(-5);

        // 1. Place a resting long (Working, below the market) with its always-native safety bracket.
        SendOrderResponse placed = await PlaceRestingLongAsync(
            app, accountId, symbol, contract.TickSize, pointValue, size, market);
        placed.OrderId.Should().NotBeNull("the bracketed entry was accepted and journaled as Working");
        string customTag = placed.OrderId!.Value.ToString();

        // 2. Realize the fill — reprice the resting entry to marketable. The app may answer Conflict if the fill
        //    lands mid-modify (benign); venue truth is the oracle, not the app's response.
        decimal marketable = market + (40m * contract.TickSize);
        using HttpResponseMessage _ = await ModifyAsync(
            app, placed.OrderId!.Value,
            new ModifyWorkingOrderRequest(EntryPrice: marketable, ReferencePrice: marketable));
        bool filled = await WaitUntilAsync(
            () => gateway.HasOpenPositionAsync(gatewayAccountId, symbol), TimeSpan.FromSeconds(30));
        filled.Should().BeTrue("the repriced-to-marketable entry filled on the practice account");

        // 3. Round-trip: flatten so the position closes and the account goes flat again — the exact state gh#631's
        //    reconcile sees for a stranded conditional whose entry already executed and was cleaned up elsewhere.
        await gateway.FlattenAsync(gatewayAccountId, contract.Id);
        bool wentFlat = await WaitUntilAsync(
            async () => !await gateway.HasOpenPositionAsync(gatewayAccountId, symbol), TimeSpan.FromSeconds(30));
        wentFlat.Should().BeTrue("the flatten closed the round-tripped position");

        return (gateway, gatewayAccountId, customTag, since);
    }

    /// <summary>
    /// The cheap negative (gh#643): a tag that was <b>never placed</b> must read as "no match" rather than an
    /// error or an accidental match — so a genuine no-fill (the everyday case for every tag the reconcile ever
    /// asks about) is distinguishable from a broken read. Guards against an order-history lookup that throws,
    /// times out, or — worse — matches a record it should not.
    /// </summary>
    [StagingGatewayFact]
    public async Task FillHistory_ShouldFindNoMatch_ForATagThatWasNeverPlaced()
    {
        string neverPlacedTag = Guid.NewGuid().ToString();
        DateTime since = DateTime.UtcNow.AddMinutes(-5);

        await using StagingProjectXGateway gateway = StagingProjectXGateway.Create();
        int gatewayAccountId = await gateway.ResolveAccountIdAsync();

        IReadOnlyList<ClientModels.Order> history = await gateway.GetOrdersAsync(gatewayAccountId, since);
        ClientModels.Order? executed = StagingProjectXGateway.ExecutedOrderWithTag(history, neverPlacedTag);

        executed.Should().BeNull(
            "a tag that was never placed must read as no-match, not an error — distinguishing a genuine no-fill "
            + "from a broken read (the counterpart to the gh#643 echo assertions above)");
    }
}
