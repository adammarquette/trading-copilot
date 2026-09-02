using MarqSpec.Client.ProjectX.Api.Models;

namespace MarqSpec.TradingCopilot.IntegrationTests.TestHost.Staging;

/// <summary>
/// Proves the R-14 by-construction guard in <see cref="StagingProjectXGateway.ResolvePracticeAccountId"/> (gh#1074)
/// — practice-only must hold regardless of which base URL drove the gate there (a deployed staging instance or a
/// locally composed one, gh#1074's own scope), and that property needs a test that can fail on the defect, not a
/// comment next to the credentials.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately a plain <see cref="FactAttribute"/>, <b>not</b> gated behind <see cref="StagingGatewayFactAttribute"/>
/// — the guard is exercised against synthetic <see cref="TradingAccount"/> rows and needs no live venue call, no
/// credentials, and no network, so it runs pre-merge on every PR rather than skipping by construction like the
/// gates it protects.
/// </para>
/// <para>
/// The guard requires <b>two independent signals to agree</b> (PR #1075 review): the venue's own
/// <c>Simulated</c> flag is not sufficient alone for a ProjectX prop-firm-style account — gh#780's
/// <c>FirmConventions.ForBrokerage</c> remarks establish that a funded account can report <c>Simulated=true</c>
/// while real payout is at stake — so the name-based classification the production adapter itself uses
/// (<c>ProjectXAccountStage.Resolve</c>) must also land on Practice. Each test below isolates one signal so a
/// regression that trusts either alone reddens on its own case.
/// </para>
/// </remarks>
public sealed class StagingProjectXGatewayPracticeGuardTests
{
    private const string PracticeAccountKey = "PRAC-50K-101";

    /// <summary>
    /// The guard's job, stated positively: a matching account where <b>both</b> signals agree it is Practice
    /// resolves — the venue reports it simulated, and its name classifies as Practice
    /// (<c>ProjectXAccountStage.Resolve</c>'s <c>PRAC</c> family).
    /// </summary>
    [Fact]
    public void ResolvePracticeAccountId_ShouldResolveTheId_WhenBothSignalsAgreeTheAccountIsPractice()
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
    /// <b>Prove-the-red on the hazard this guard originally caught (R-14).</b> A name matching the reserved key
    /// that the venue nonetheless reports <c>Simulated=false</c> — e.g. the operator's
    /// <c>STAGING_PROJECTX_API_KEY/SECRET</c> pointed at a live account whose name happens to collide — must be
    /// refused, never silently traded. Before the gh#1074 guard this returned <c>202</c> and every write above it
    /// (the partial close, the direct <c>PlaceOrderAsync</c>) would have transmitted to a live account with
    /// nothing in code standing in the way.
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
            .WithMessage("*Simulated=False*");
    }

    /// <summary>
    /// <b>Prove-the-red on the hazard PR #1075's review found (gh#780).</b> A <b>funded</b> account (its name
    /// classifies via the <c>EXPRESS</c> family, per <c>ProjectXAccountStage.Resolve</c>) that nonetheless reports
    /// <c>Simulated=true</c> — the exact scenario <c>FirmConventions.ForBrokerage</c>'s remarks warn about for a
    /// prop-firm-style venue: real payout at stake while the venue's own flag says "simulated". A guard that
    /// trusted <see cref="TradingAccount.Simulated"/> alone would have resolved this id and let
    /// <c>PlaceOrderAsync</c>/<c>PartialCloseAsync</c> transmit real orders against it.
    /// </summary>
    [Fact]
    public void ResolvePracticeAccountId_ShouldThrow_WhenTheMatchingAccountIsSimulatedButNameClassifiesAsFunded()
    {
        const string fundedKey = "EXPRESS-50K-303";
        TradingAccount[] accounts =
        [
            new() { Id = 303, Name = fundedKey, Simulated = true },
        ];

        Action resolve = () => StagingProjectXGateway.ResolvePracticeAccountId(accounts, fundedKey);

        resolve.Should().Throw<InvalidOperationException>()
            .WithMessage("*R-14*")
            .WithMessage("*name-classified stage=Funded*");
    }

    /// <summary>
    /// The direction-symmetric case for a name the classifier cannot confidently place at all
    /// (<see cref="AccountStage.Unknown"/>) — fails closed the same as an outright disagreement, never treated as
    /// "not proven funded, so allow it".
    /// </summary>
    [Fact]
    public void ResolvePracticeAccountId_ShouldThrow_WhenTheMatchingAccountIsSimulatedButNameIsUnclassifiable()
    {
        const string unknownKey = "UNKNOWN-NAME-999";
        TradingAccount[] accounts =
        [
            new() { Id = 999, Name = unknownKey, Simulated = true },
        ];

        Action resolve = () => StagingProjectXGateway.ResolvePracticeAccountId(accounts, unknownKey);

        resolve.Should().Throw<InvalidOperationException>()
            .WithMessage("*R-14*")
            .WithMessage("*name-classified stage=Unknown*");
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
