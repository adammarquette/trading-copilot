using MarqSpec.TradingCopilot.IntegrationTests.TestHost.Staging;
using Xunit;

namespace MarqSpec.TradingCopilot.IntegrationTests.Api;

/// <summary>
/// <b>Post-merge staging gate (gh#269 ⇒ gh#259).</b> Verifies the gh#259 reprice safety claim that <i>only the real
/// ProjectX gateway can witness</i>: that an <b>attached protective bracket is preserved</b> when a working entry is
/// modified in place. The app cannot re-assert it (the client <c>ModifyOrderRequest</c> carries no bracket field), so
/// a fake venue proves nothing — this must run against a real practice account. It SKIPS pre-merge via
/// <see cref="StagingVenueFactAttribute"/> and is serialized onto the reserved account.
/// </summary>
[Trait("Category", "Staging")]
[Collection(StagingExecutionCollection.Name)]
public sealed class BracketPreservationStagingIntegrationTests : StagingVenueExecutionSuite
{
    [StagingVenueFact]
    public async Task PracticeAccount_ShouldResolveThroughTheDeployedApi_ForTheModifyGate()
    {
        // The execution-gate precondition: the reserved practice account is discoverable through the deployed API.
        // If this fails, the gate below cannot run — so it is asserted first and on its own.
        using HttpClient client = await AuthenticatedClientAsync();
        Guid accountId = await ResolvePracticeAccountAsync(client);

        accountId.Should().NotBe(Guid.Empty, "the reserved practice account resolved on staging");
    }

    // DRAFT (gh#269): the safety gate proper. Author + prove-the-red on the FIRST staging run against real ProjectX,
    // because only the gateway can witness bracket preservation:
    //   1. declare a risk profile on the practice account
    //   2. place a bracketed working entry (POST /accounts/{id}/orders) — the safety-stop bracket attaches at the venue
    //   3. reprice the entry in place (PATCH /orders/{id}/price)
    //   4. read VENUE TRUTH (working orders / account-event stream) and assert the safety-stop bracket still rests,
    //      covering the same quantity at the same trigger
    // Prove-the-red: a gateway that drops the bracket on modify leaves step 4 with no resting stop → the test fails.
    [Fact(Skip = "DRAFT gh#269: author + prove-the-red on the first staging run against real ProjectX (see comment).")]
    public Task Modify_ShouldPreserveTheAttachedProtectiveBracket_WhenTheEntryIsRepriced() => Task.CompletedTask;
}
