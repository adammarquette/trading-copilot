using MarqSpec.Client.ProjectX.Api.Models;
using MarqSpec.TradingCopilot.Domain.Venue;

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
/// <b>The guard does not consult <see cref="TradingAccount.Simulated"/> at all (PR #1075 second review round).</b>
/// An earlier version of this guard trusted that flag — alone, then as one of two AND-ed signals — but gh#780's
/// <c>FirmConventions.ForBrokerage</c> remarks establish it is not authoritative for a ProjectX prop-firm-style
/// account: a funded account can report <c>Simulated=true</c> while real payout is at stake. The guard now derives
/// <see cref="TradingMode"/> the <b>same way production does</b> for a <c>FirmType.PropFirm</c> connection — via
/// <see cref="FirmConventions.ModeFor(AccountStage)"/>, which never reads the venue flag either. Each test below
/// isolates one way that derivation can (or must) resolve, so a regression that starts trusting <c>Simulated</c>
/// again reddens on its own case — most pointedly
/// <see cref="ResolvePracticeAccountId_ShouldThrow_WhenSimulatedIsTrueButTheDeclarationResolvesToLive"/>.
/// </para>
/// </remarks>
public sealed class StagingProjectXGatewayPracticeGuardTests
{
    private const string PracticeAccountKey = "PRAC-50K-101";

    /// <summary>
    /// The guard's job, stated positively: a matching account whose name classifies as Practice, under
    /// conventions that declare Practice risk-free, resolves — this is
    /// <see cref="StagingProjectXGateway.ReservedAccountConventions"/> itself, the same declaration the app-side
    /// connection registers.
    /// </summary>
    [Fact]
    public void ResolvePracticeAccountId_ShouldResolveTheId_WhenTheDeclaredConventionsClassifyTheAccountAsPractice()
    {
        TradingAccount[] accounts =
        [
            new() { Id = 101, Name = PracticeAccountKey, Simulated = true },
            new() { Id = 202, Name = "LIVE-FUNDED-202", Simulated = false },
        ];

        int resolved = StagingProjectXGateway.ResolvePracticeAccountId(
            accounts, PracticeAccountKey, StagingProjectXGateway.ReservedAccountConventions);

        resolved.Should().Be(101);
    }

    /// <summary>
    /// <b>Deliberate, not an oversight:</b> a genuinely Practice-classified account still resolves even when the
    /// venue reports <c>Simulated=false</c> — because for a <c>FirmType.PropFirm</c> connection the flag is never
    /// consulted at all (<see cref="FirmConventions.ModeFor(AccountStage)"/>'s <c>ModeFollowsVenue=false</c> path),
    /// exactly matching how production resolves this same venue's accounts. A test asserting the opposite would be
    /// asserting the exact trust gh#780 says this venue must not extend to that flag.
    /// </summary>
    [Fact]
    public void ResolvePracticeAccountId_ShouldResolveTheId_EvenWhenSimulatedIsFalse_BecauseTheFlagIsNotConsulted()
    {
        TradingAccount[] accounts =
        [
            new() { Id = 101, Name = PracticeAccountKey, Simulated = false },
        ];

        int resolved = StagingProjectXGateway.ResolvePracticeAccountId(
            accounts, PracticeAccountKey, StagingProjectXGateway.ReservedAccountConventions);

        resolved.Should().Be(101);
    }

    /// <summary>
    /// <b>The mutation that kills a regression back to trusting the venue flag (gh#780, coordinator review).</b>
    /// A firm's conventions <i>explicitly</i> declare the name-classified stage as capital-at-risk (Live) — not
    /// merely undeclared — and the account nonetheless reports <c>Simulated=true</c>: the documented prop-firm
    /// inversion. If this guard ever again consulted <c>Simulated</c> — even as one of two AND-ed conditions —
    /// this case would wrongly resolve and <c>PlaceOrderAsync</c>/<c>PartialCloseAsync</c> would transmit real
    /// orders against a funded account with a real payout at stake, through the exact path this PR frames as
    /// safety-hardened. The only thing that can make this throw is the declaration being consulted <i>instead of</i>
    /// the flag, which is the whole point of the redesign.
    /// </summary>
    [Fact]
    public void ResolvePracticeAccountId_ShouldThrow_WhenSimulatedIsTrueButTheDeclarationResolvesToLive()
    {
        const string fundedKey = "EXPRESS-50K-303";
        TradingAccount[] accounts =
        [
            new() { Id = 303, Name = fundedKey, Simulated = true },
        ];
        FirmConventions fundedIsLive = FirmConventions.For(
            "Live-Payout-Firm", (AccountStage.Funded, CapitalAtRisk: true));

        Action resolve = () => StagingProjectXGateway.ResolvePracticeAccountId(accounts, fundedKey, fundedIsLive);

        resolve.Should().Throw<InvalidOperationException>()
            .WithMessage("*R-14*")
            .WithMessage("*TradingMode.Live*")
            .WithMessage("*name-classified stage=Funded*");
    }

    /// <summary>
    /// The reserved-account conventions declare only Practice; a funded-classified account is therefore
    /// <see cref="TradingMode.Undeclared"/> under them (never promoted to tradeable by default) — refused the
    /// same as an explicit Live declaration, distinguishing "the declaration says no" from "the declaration says
    /// nothing" without either one being treated as permission.
    /// </summary>
    [Fact]
    public void ResolvePracticeAccountId_ShouldThrow_WhenTheNameClassifiesAsFundedAndItIsUndeclared()
    {
        const string fundedKey = "EXPRESS-50K-303";
        TradingAccount[] accounts =
        [
            new() { Id = 303, Name = fundedKey, Simulated = true },
        ];

        Action resolve = () => StagingProjectXGateway.ResolvePracticeAccountId(
            accounts, fundedKey, StagingProjectXGateway.ReservedAccountConventions);

        resolve.Should().Throw<InvalidOperationException>()
            .WithMessage("*R-14*")
            .WithMessage("*TradingMode.Undeclared*")
            .WithMessage("*name-classified stage=Funded*");
    }

    /// <summary>
    /// The direction-symmetric case for a name the classifier cannot confidently place at all
    /// (<see cref="AccountStage.Unknown"/>) — fails closed the same as an outright disagreement, never treated as
    /// "not proven funded, so allow it".
    /// </summary>
    [Fact]
    public void ResolvePracticeAccountId_ShouldThrow_WhenTheNameIsUnclassifiable()
    {
        const string unknownKey = "UNKNOWN-NAME-999";
        TradingAccount[] accounts =
        [
            new() { Id = 999, Name = unknownKey, Simulated = true },
        ];

        Action resolve = () => StagingProjectXGateway.ResolvePracticeAccountId(
            accounts, unknownKey, StagingProjectXGateway.ReservedAccountConventions);

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

        Action resolve = () => StagingProjectXGateway.ResolvePracticeAccountId(
            accounts, PracticeAccountKey, StagingProjectXGateway.ReservedAccountConventions);

        resolve.Should().Throw<InvalidOperationException>()
            .WithMessage("*not visible to the gateway credentials*");
    }
}
