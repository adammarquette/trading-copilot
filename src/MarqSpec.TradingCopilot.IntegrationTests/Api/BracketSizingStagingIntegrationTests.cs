using MarqSpec.TradingCopilot.IntegrationTests.TestHost.Staging;
using Xunit;

namespace MarqSpec.TradingCopilot.IntegrationTests.Api;

/// <summary>
/// <b>Hard pre-live staging gate (gh#293 ⇒ gh#292).</b> The resize feature relaxes the modify family's size-invariant
/// on a <i>structural inference</i>: the ProjectX safety bracket carries no size field and is instantiated on fill, so
/// the on-fill protective stop <i>should</i> size to the realized fill. On a safety-critical path an inference is not
/// a certification — a gateway that instead pinned the bracket to the placement-time parent size would leave a
/// resize-<b>up</b> partly naked on fill. Only the real gateway can certify this, so it SKIPS pre-merge via
/// <see cref="StagingVenueFactAttribute"/> and is serialized onto the reserved practice account.
/// </summary>
[Trait("Category", "Staging")]
[Collection(StagingExecutionCollection.Name)]
public sealed class BracketSizingStagingIntegrationTests : StagingVenueExecutionSuite
{
    [StagingVenueFact]
    public async Task PracticeAccount_ShouldResolveThroughTheDeployedApi_ForTheResizeGate()
    {
        // Precondition for the resize gate: the reserved practice account is discoverable through the deployed API.
        using HttpClient client = await AuthenticatedClientAsync();
        Guid accountId = await ResolvePracticeAccountAsync(client);

        accountId.Should().NotBe(Guid.Empty, "the reserved practice account resolved on staging");
    }

    // DRAFT (gh#293): the pre-live gate proper. Author + prove-the-red on the FIRST staging run against real ProjectX,
    // because only the gateway certifies on-fill bracket sizing:
    //   1. declare a risk profile; place a bracketed working entry at quantity N
    //   2. resize the working entry UP to N+M in place (the #292 capability)
    //   3. let it fill on the practice account
    //   4. read VENUE TRUTH and assert the on-fill protective stop covers the FULL realized fill (N+M) — no naked
    //      excess. Resize-DOWN is the symmetric case (the stop must not over-cover into an unwanted reverse).
    // Prove-the-red: a gateway that pins the bracket to the placement-time size leaves the resized-up excess
    // unprotected at step 4 → the test fails. This gate must be GREEN before any promotion toward a live account.
    [Fact(Skip = "DRAFT gh#293: author + prove-the-red on the first staging run against real ProjectX (see comment).")]
    public Task Resize_ShouldSizeTheOnFillBracketToTheRealizedFill_WhenAWorkingOrderIsResized() => Task.CompletedTask;
}
