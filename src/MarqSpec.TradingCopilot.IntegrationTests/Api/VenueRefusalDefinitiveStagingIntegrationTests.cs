using System.Globalization;
using MarqSpec.Client.ProjectX.Exceptions;
using MarqSpec.TradingCopilot.Api.Orders;
using MarqSpec.TradingCopilot.Domain.Venue;
using MarqSpec.TradingCopilot.IntegrationTests.TestHost.Staging;
using Xunit;
using Xunit.Abstractions;
using ClientModels = MarqSpec.Client.ProjectX.Api.Models;
using Price = MarqSpec.TradingCopilot.Domain.Price;

namespace MarqSpec.TradingCopilot.IntegrationTests.Api;

/// <summary>
/// <b>Post-merge staging gate (gh#668 ⇒ gh#629).</b> gh#629 classifies a venue refusal <b>at the throw site</b>:
/// <c>ProjectXVenue.PlaceOrderAsync</c> answers <c>!response.Success</c> with
/// <see cref="VenueRefusalKind.Definitive"/>, so a catch site never has to infer a kind from a message. That rests on
/// an assumption no test below this tier can make — <b>that a genuinely-invalid order comes back from the real
/// gateway as a well-formed response with <c>success == false</c></b>. Every unit test necessarily fakes the client
/// (<c>ProjectXVenueTests</c> asserts the <i>mapping</i>, <i>given</i> a <c>!success</c> response), and gh#663's
/// container-tier suite (<see cref="VenueRefusalOutcomeIntegrationTests"/>) arms an adversarial venue to throw the
/// exception directly. Neither ever asks ProjectX.
/// </summary>
/// <remarks>
/// <para>
/// <b>It fails in both directions, which is why it is a gate and not a note.</b> If ProjectX signals invalid orders
/// some other way — an HTTP 4xx, a thrown transport error, <c>success: true</c> carrying an error payload — then
/// <c>!response.Success</c> is never true, the <see cref="VenueRefusalKind.Definitive"/> arm is <b>never reached in
/// production</b>, and every venue rejection strands as <c>Taking</c> / <c>Firing</c> waiting for a human: the
/// auto-resolve gh#629 exists to provide would be <b>inert while every test is green</b> (the same shape as gh#631's
/// customTag echo, gated by gh#643). If instead ProjectX can answer <c>!success</c> for an order it nevertheless
/// placed, the classification is wrong in the <i>dangerous</i> direction — a maybe-live order read as definitive,
/// auto-released to <c>Staged</c> / <c>Pending</c>, and the next take / send / fire stacks a second live order on it
/// (the gh#530 / gh#589 hazard). The wider posture makes the second unlikely by construction
/// (<see cref="VenueRefusalKind.Indeterminate"/> is the zero value and only the adapter may assert definitive) — but
/// it is exactly the assertion a real venue can settle and a fake cannot.
/// </para>
/// <para>
/// <b>Why this transmits through the venue seam rather than the app.</b> The app's risk / execution gate refuses a
/// deliberately-invalid ticket <b>before</b> transmit — correctly — so routing the probe through the deployed API
/// would prove only that the app's pre-flight works, and <c>ProjectXVenue.PlaceOrderAsync</c> would never run. The
/// probe therefore goes through <see cref="StagingProjectXGateway.PlaceOrderAsync"/>, which is the production
/// adapter over the reserved <b>practice</b> credentials (R-14). The <i>control</i> order below still goes through
/// the app, because a valid entry has no reason to bypass the gate.
/// </para>
/// <para>
/// <b>Guard discipline — the "nothing rests" reads are not allowed to be vacuous.</b> Asserting that the account's
/// working orders contain nothing under the refused order's tag would pass just as happily if the read were blind.
/// So each gate first places a <b>valid</b> resting long through the app, 200 ticks below the market where it cannot
/// fill, and requires the <i>same</i> <see cref="StagingProjectXGateway.OpenOrdersAsync"/> call that later witnesses
/// the absence of the refused tag to witness the <i>presence</i> of the control's tag — before and after the probe.
/// One read must discriminate between the two tags; a read that sees nothing reddens on the control.
/// </para>
/// <para>
/// <b>The observed shape (acceptance criterion 4).</b> What ProjectX actually returns for an invalid order is
/// recorded by the run itself: every probe writes a single <c>gh#668 OBSERVED:</c> line to the test output carrying
/// the exception type, and — per type — <see cref="VenueRefusalException.Kind"/> and
/// <see cref="VenueRefusalException.ErrorCode"/> plus the gateway's own <c>errorMessage</c> (the adapter interpolates
/// it into the message), or a <see cref="ProjectXApiException"/>'s HTTP status, or the bare fact that the venue
/// accepted the ticket. <b>Not yet pinned here:</b> this suite is authored without staging credentials, so the
/// literal status / body / <c>errorCode</c> belong to the first post-merge staging run and must be transcribed into
/// this block from that run's output rather than guessed at now. The <c>!success</c> path carries no HTTP status at
/// all — it is a <c>200</c> whose body says no — so a recorded status here means the gateway took the other route,
/// and gh#629's mapping is the thing to re-open.
/// </para>
/// <para>
/// It SKIPS pre-merge via <see cref="StagingGatewayFactAttribute"/> and is serialized onto the reserved practice
/// account. Traces: R-11 · R-12 · R-14 · ADR-0007 · ADR-0013 §9 · gh#629 · gh#663 · gh#643 · gh#530 · gh#589 · gh#622.
/// </para>
/// </remarks>
[Trait("Category", "Staging")]
[Collection(StagingExecutionCollection.Name)]
public sealed class VenueRefusalDefinitiveStagingIntegrationTests : StagingVenueExecutionSuite
{
    /// <summary>
    /// How far below the market the <b>control</b> entry rests. Far enough that it cannot fill inside the seconds
    /// this gate takes — a fill would attach the native protective stop, adding a resting order the "nothing new
    /// rests" assertion would then blame on the refused probe — and well inside the exchange's price bands.
    /// </summary>
    private const int ControlTicksBelowMarket = 200;

    /// <summary>The safety stop rides <c>20</c> ticks under the entry (the shared helper's shape); one contract.</summary>
    private const int Size = 1;

    private readonly ITestOutputHelper _output;

    /// <summary>Captures the observed venue shape into the run's output (acceptance criterion 4).</summary>
    public VenueRefusalDefinitiveStagingIntegrationTests(ITestOutputHelper output) => _output = output;

    /// <summary>
    /// Precondition for the gate below: the reserved <b>practice</b> account is visible to the gateway credentials
    /// (R-14 — a live real-money account is never wired into staging). Asserted first and on its own, because a
    /// failure here means the gate could not have run at all rather than that the venue behaved unexpectedly.
    /// </summary>
    [StagingGatewayFact]
    public async Task PracticeAccount_ShouldResolveThroughTheGateway_ForTheDefinitiveRefusalGate()
    {
        await using StagingProjectXGateway gateway = StagingProjectXGateway.Create();

        int gatewayAccountId = await gateway.ResolveAccountIdAsync();

        gatewayAccountId.Should().BePositive(
            "the reserved practice account resolved through the gateway's own credentials — the gh#668 probe "
            + "transmits directly to the venue, so it must be certain which account it is transmitting to");
    }

    /// <summary>
    /// <b>The gate.</b> Sends a genuinely-invalid order — a ticket that is valid in every respect <i>except</i> that
    /// its contract handle names a contract the venue does not know — through the production
    /// <c>ProjectXVenue.PlaceOrderAsync</c>, and requires it to surface as a
    /// <see cref="VenueRefusalException"/> of kind <see cref="VenueRefusalKind.Definitive"/> carrying the gateway's
    /// error code, with <b>nothing resting</b> at the venue afterwards.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why an unknown contract, and why derived rather than invented.</b> The probe must be something the gateway
    /// <i>must</i> reject and <i>cannot partially accept</i>. A tick or size violation could be rounded, clamped, or
    /// accepted and left resting; an oversized order that slipped through would be a live position on the shared
    /// account. An order on a contract that does not exist cannot rest anywhere, cannot fill, and cannot be partially
    /// accepted. The handle is built from the <b>real active contract</b>
    /// (<see cref="StagingProjectXGateway.ResolveContractAsync"/>) with only its trailing expiry segment replaced by
    /// an impossible one, so the product, venue and prefix stay genuine and the ticket carries <b>exactly one</b>
    /// defect — the discipline gh#663's suite applies to its pairs, here applied to the ticket.
    /// </para>
    /// <para>
    /// <b>Prove-the-red.</b> Three distinct failures are reachable, and each is the discovery this gate exists to
    /// make. (1) The venue answers with an HTTP error or a transport fault: <see cref="ProjectXApiException"/>
    /// escapes <c>PlaceOrderAsync</c> untranslated, the type assertion reddens, and gh#629's <c>Definitive</c> arm is
    /// unreachable in production — the auto-resolve is inert. (2) The venue answers <c>!success</c> but the adapter
    /// classifies it <see cref="VenueRefusalKind.Indeterminate"/> — the kind assertion reddens. (3) The venue
    /// <i>accepts</i> it: no exception at all, and the "nothing rests" reads below would have caught a live order.
    /// Per the QA contract, a red here is <b>reported against gh#629 as a new <c>work:code</c> issue</b>, never fixed
    /// from this suite — a wrong mapping is an adapter change on a safety-critical path and wants its own review.
    /// </para>
    /// <para>
    /// <b>On the <see cref="VenueRefusalException.ErrorCode"/> assertion.</b> It cannot fail against today's adapter,
    /// which widens the client's non-nullable <c>int</c> into the exception's <c>int?</c>. It is kept deliberately, as
    /// a regression guard on the <i>mapping</i>: it goes red the day the adapter stops propagating the gateway's code,
    /// which is the field a triaging human reads first. The <i>value</i> is an observation, not an expectation — the
    /// issue's criterion is "carries the gateway's code <i>where it supplies one</i>", so nothing here demands it be
    /// non-zero; the run records what it actually was.
    /// </para>
    /// </remarks>
    [StagingGatewayFact]
    public async Task PlaceOrder_ShouldRefuseDefinitivelyAndRestNothing_ForAContractTheVenueDoesNotKnow()
    {
        string symbol = StagingConfig.ExecutionInstrument!;
        await using StagingProjectXGateway gateway = StagingProjectXGateway.Create();
        int gatewayAccountId = await gateway.ResolveAccountIdAsync();
        ClientModels.Contract contract = await gateway.ResolveContractAsync(symbol);

        // The collection serializes, but a prior crash could have left residue on the shared practice account — and
        // the whole gate is a before/after comparison of venue state, so it starts from a known one.
        await gateway.FlattenAsync(gatewayAccountId, contract.Id);

        // A few minutes' margin before the transmit, covering clock skew between this process and the gateway.
        DateTime since = DateTime.UtcNow.AddMinutes(-5);

        try
        {
            decimal market = await gateway.ReferencePriceAsync(contract.Id);
            string controlTag = await PlaceRestingControlAsync(gateway, gatewayAccountId, symbol, contract, market);

            // Venue state BEFORE the probe, with the control resting in it — so "nothing rests" can be stated as
            // "the venue's state is unchanged", which is stronger than "the book is empty" and true on a shared account.
            IReadOnlyList<ClientModels.Order> before = await gateway.OpenOrdersAsync(gatewayAccountId);
            HashSet<long> restingBefore = [.. before.Select(order => order.Id)];
            int netPositionBefore = await NetPositionAsync(gateway, gatewayAccountId, contract.Id);

            // THE PROBE. Every field is valid — the account is the reserved practice one, the size is one contract,
            // the entry is on the tick grid, the always-native safety stop rides along (ADR-0007) — except the
            // contract handle, which names an expiry that cannot exist. It is priced the same distance off the
            // market as the control for the same reason: if the venue accepts what it must reject, the assertions
            // below fail on an order that is at least resting rather than one that filled on arrival.
            string refusedTag = $"qa668-{Guid.NewGuid():N}";
            Price entry = new Price(market - (ControlTicksBelowMarket * contract.TickSize)).RoundToTick(contract.TickSize);
            Price safetyStop = new Price(entry.Value - (20m * contract.TickSize)).RoundToTick(contract.TickSize);
            OrderRequest ticket = new(
                StagingProjectXGateway.VenueAccount(gatewayAccountId),
                StagingProjectXGateway.VenueContract(UnknownContractKey(contract.Id)),
                OrderSide.Buy,
                OrderType.Limit,
                Size,
                entry,
                StopPrice: null)
            {
                ProtectiveStop = safetyStop,
                CustomTag = refusedTag,
            };

            Exception? thrown = await Record.ExceptionAsync(() => gateway.PlaceOrderAsync(ticket));
            _output.WriteLine(Observed(thrown, ticket));

            // --- 1. It surfaces as a refusal at all, rather than a transport/HTTP error the catch sites never see.
            thrown.Should().NotBeNull(
                "the venue must reject an order on a contract it does not know — if it accepted this, the ticket was "
                + "not invalid and the probe itself is wrong, and an order may now be resting on the practice account");
            VenueRefusalException refusal = thrown.Should().BeOfType<VenueRefusalException>(
                "gh#629 classifies at the throw site, so a real gateway rejection must arrive as the venue-neutral "
                + "VenueRefusalException — a ProjectXApiException escaping here means the !success arm is never "
                + "reached in production and the auto-resolve is inert while every other test stays green").Which;

            // --- 2. …classified Definitive, carrying the gateway's code.
            refusal.Kind.Should().Be(VenueRefusalKind.Definitive,
                "the gateway answered in the negative and placed nothing, which is the one case the adapter may "
                + "assert as definitive — Indeterminate here would strand every venue rejection for a human");
            refusal.ErrorCode.Should().NotBeNull(
                "the adapter propagates the gateway's error code onto the refusal — it is the first field a human "
                + "triaging a stranded row reads");

            // --- 3. NOTHING RESTS. "Definitive" is a claim about the venue's state, not about the exception's shape,
            //        and this is the only tier that can check it.
            IReadOnlyList<ClientModels.Order> after = await gateway.OpenOrdersAsync(gatewayAccountId);
            after.Should().Contain(order => order.CustomTag == controlTag,
                "the control entry is still resting, so this read demonstrably sees orders — without it, every "
                + "assertion below would pass just as happily against a blind read (QA contract, guard discipline 1)");
            after.Should().NotContain(order => order.CustomTag == refusedTag,
                "a definitively-rejected order leaves nothing working at the venue — anything resting under this tag "
                + "is the gh#530 / gh#589 stacking hazard, auto-released as though it were absent");
            after.Select(order => order.Id).Should().BeSubsetOf(restingBefore,
                "the refused order added nothing to the book — not under our tag, and not under one we cannot predict");
            int netPositionAfter = await NetPositionAsync(gateway, gatewayAccountId, contract.Id);
            netPositionAfter.Should().Be(netPositionBefore,
                "a rejected order cannot have filled — the account's net position on the contract is untouched");

            // The order-history read is the only surface that retains an order once it has gone terminal, so it
            // catches the case the two reads above structurally cannot: placed, then immediately terminal. A REJECTED
            // record under the tag is fine and expected if the venue journals its rejections — what must not exist is
            // a record that worked or executed.
            IReadOnlyList<ClientModels.Order> history = await gateway.GetOrdersAsync(gatewayAccountId, since);
            history.Should().NotContain(
                order => order.CustomTag == refusedTag
                    && (order.FillVolume > 0
                        || order.Status == ClientModels.OrderStatus.Open
                        || order.Status == ClientModels.OrderStatus.Pending),
                "the venue may journal the rejection, but it must never show the refused order as having worked or "
                + "executed — that is the maybe-live case gh#629 classifies as Indeterminate, not Definitive");
        }
        finally
        {
            // Cleanup, whatever happened: the control entry leaves the book, and anything the venue accepted against
            // expectation is closed out, so the next serialized run on this shared account starts flat.
            await gateway.FlattenAsync(gatewayAccountId, contract.Id);
        }
    }

    /// <summary>
    /// The non-vacuity control: a <b>valid</b> resting long placed <b>through the deployed app</b> — so the risk /
    /// execution gate stays in the loop for anything legitimate — carrying the app's journaled order id as its
    /// <c>customTag</c> (<c>OrderEndpoints</c> matches resting orders on <c>CustomTag == id.ToString()</c>). Returns
    /// once the gateway itself can see it resting; the tag it returns is what the gate's reads must keep witnessing.
    /// </summary>
    private static async Task<string> PlaceRestingControlAsync(
        StagingProjectXGateway gateway,
        int gatewayAccountId,
        string symbol,
        ClientModels.Contract contract,
        decimal market)
    {
        using HttpClient app = await AuthenticatedClientAsync();
        Guid accountId = await ResolvePracticeAccountAsync(app);
        await DeclareRiskAsync(app, accountId, maxContractsPerOrder: Size);

        decimal pointValue = contract.TickSize == 0m ? contract.TickValue : contract.TickValue / contract.TickSize;
        SendOrderResponse placed = await PlaceRestingLongAsync(
            app, accountId, symbol, contract.TickSize, pointValue, Size, market, ControlTicksBelowMarket);
        placed.OrderId.Should().NotBeNull("the control entry was accepted by the app's gate and journaled as Working");

        string controlTag = placed.OrderId!.Value.ToString();
        bool visible = await WaitUntilAsync(
            async () => (await gateway.OpenOrdersAsync(gatewayAccountId)).Any(order => order.CustomTag == controlTag),
            TimeSpan.FromSeconds(30));
        visible.Should().BeTrue(
            "the gateway's working-orders read must see the control entry before the probe runs — otherwise the "
            + "gate's 'nothing rests' assertions are being made through a read that cannot see anything at all");
        return controlTag;
    }

    /// <summary>
    /// The account's <b>net</b> signed position on the contract — long positive, short negative, zero when flat.
    /// Compared before and after the probe rather than merely asserted flat, so a control that unexpectedly filled
    /// reddens on its own terms instead of being mistaken for the refused order having reached the market.
    /// </summary>
    private static async Task<int> NetPositionAsync(StagingProjectXGateway gateway, int accountId, string contractIdHint)
    {
        IReadOnlyList<ClientModels.Position> positions = await gateway.OpenPositionsAsync(accountId);
        return positions
            .Where(position => position.ContractId.Contains(contractIdHint, StringComparison.OrdinalIgnoreCase))
            .Sum(position => position.Type == ClientModels.PositionType.Short ? -position.Size : position.Size);
    }

    /// <summary>
    /// Derives a contract handle the venue cannot know from the <b>real</b> active one, by replacing the trailing
    /// expiry segment (<c>CON.F.US.EP.M25</c> → <c>CON.F.US.EP.QA668</c>). Derived rather than invented so the venue,
    /// product and prefix stay genuine and the ticket's single defect is unambiguous; the sentinel segment is not a
    /// month code in any convention, so no future roll can accidentally make it real.
    /// </summary>
    private static string UnknownContractKey(string activeContractId)
    {
        int lastSeparator = activeContractId.LastIndexOf('.');
        return lastSeparator > 0
            ? string.Concat(activeContractId.AsSpan(0, lastSeparator), ".QA668")
            : activeContractId + ".QA668";
    }

    /// <summary>
    /// The single-line record of what ProjectX actually did with the invalid ticket (acceptance criterion 4), written
    /// to the run's output so the shape is captured whether the gate goes green or red — a red is precisely when the
    /// observation matters most, and an assertion message alone would not survive into the next adapter's notes.
    /// </summary>
    private static string Observed(Exception? thrown, OrderRequest ticket)
    {
        string probe = $"contract=\"{ticket.Contract.Key}\" side={ticket.Side} type={ticket.Type} "
            + $"size={ticket.Quantity} limit={ticket.LimitPrice} customTag=\"{ticket.CustomTag}\"";

        string outcome = thrown switch
        {
            null => "NO EXCEPTION — the venue ACCEPTED the invalid ticket (the probe is not invalid, or the venue is)",
            VenueRefusalException refusal =>
                $"VenueRefusalException kind={refusal.Kind} "
                + $"errorCode={refusal.ErrorCode?.ToString(CultureInfo.InvariantCulture) ?? "<null>"} "
                + $"message=\"{refusal.Message}\"",
            ProjectXApiException api =>
                $"ProjectXApiException httpStatus={api.StatusCode?.ToString(CultureInfo.InvariantCulture) ?? "<none>"} "
                + $"message=\"{api.Message}\" inner={api.InnerException?.GetType().Name ?? "<none>"}",
            _ => $"{thrown.GetType().FullName} message=\"{thrown.Message}\"",
        };

        return $"gh#668 OBSERVED: {outcome} | probe: {probe}";
    }
}
