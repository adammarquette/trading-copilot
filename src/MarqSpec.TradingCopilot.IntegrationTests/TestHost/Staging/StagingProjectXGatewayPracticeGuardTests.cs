using MarqSpec.Client.ProjectX.Api.Models;

namespace MarqSpec.TradingCopilot.IntegrationTests.TestHost.Staging;

/// <summary>
/// Proves the R-14 by-construction guard in <see cref="StagingProjectXGateway.ResolvePracticeAccountId"/> (gh#1074)
/// — practice-only must hold regardless of which base URL drove the gate there (a deployed staging instance or a
/// locally composed one, gh#1074's own scope), and that property needs a test that can fail on the defect, not a
/// comment next to the credentials.
/// </summary>
/// <remarks>
/// Deliberately a plain <see cref="FactAttribute"/>, <b>not</b> gated behind <see cref="StagingGatewayFactAttribute"/>
/// — the guard is exercised against synthetic <see cref="TradingAccount"/> rows and needs no live venue call, no
/// credentials, and no network, so it runs pre-merge on every PR rather than skipping by construction like the
/// gates it protects.
/// </remarks>
public sealed class StagingProjectXGatewayPracticeGuardTests
{
    private const string PracticeAccountKey = "PRAC-50K-101";

    /// <summary>
    /// The guard's job, stated positively: a matching account the venue itself reports as simulated resolves.
    /// </summary>
    [Fact]
    public void ResolvePracticeAccountId_ShouldResolveTheId_WhenTheMatchingAccountIsSimulated()
    {
        TradingAccount[] accounts =
        [
            new() { Id = 101, Name = PracticeAccountKey, Simulated = true },
            new() { Id = 202, Name = "LIVE-FUNDED-202", Simulated = false },
        ];

        int resolved = StagingProjectXGateway.ResolvePracticeAccountId(accounts, PracticeAccountKey);

        resolved.Should().Be(101);
    }

    /// <summary>
    /// <b>Prove-the-red on the exact hazard this guard exists to catch (R-14).</b> A name matching the reserved
    /// key that the venue nonetheless reports <c>Simulated=false</c> — e.g. the operator's
    /// <c>STAGING_PROJECTX_API_KEY/SECRET</c> pointed at a live account whose name happens to collide, or a venue
    /// naming convention that is not the practice signal it looks like — must be refused, never silently traded.
    /// Before the gh#1074 guard this returned <c>202</c> and every write above it (the partial close, the direct
    /// <c>PlaceOrderAsync</c>) would have transmitted to a live account with no code standing in the way.
    /// </summary>
    [Fact]
    public void ResolvePracticeAccountId_ShouldThrow_WhenTheMatchingAccountIsNotSimulated()
    {
        TradingAccount[] accounts =
        [
            new() { Id = 202, Name = PracticeAccountKey, Simulated = false },
        ];

        Action resolve = () => StagingProjectXGateway.ResolvePracticeAccountId(accounts, PracticeAccountKey);

        resolve.Should().Throw<InvalidOperationException>()
            .WithMessage("*R-14*")
            .WithMessage("*Simulated=false*");
    }

    /// <summary>Existing behaviour preserved: no name match is still refused, distinctly from a live-account match.</summary>
    [Fact]
    public void ResolvePracticeAccountId_ShouldThrow_WhenNoAccountMatchesTheReservedKey()
    {
        TradingAccount[] accounts =
        [
            new() { Id = 202, Name = "SOME-OTHER-ACCOUNT", Simulated = true },
        ];

        Action resolve = () => StagingProjectXGateway.ResolvePracticeAccountId(accounts, PracticeAccountKey);

        resolve.Should().Throw<InvalidOperationException>()
            .WithMessage("*not visible to the gateway credentials*");
    }
}
