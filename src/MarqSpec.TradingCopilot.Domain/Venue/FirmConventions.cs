namespace MarqSpec.TradingCopilot.Domain.Venue;

/// <summary>
/// What each account stage <b>means at one firm</b> — the operator's declaration of where capital is actually at
/// risk (R-14).
/// </summary>
/// <remarks>
/// <para>
/// No venue reports this, and no adapter can infer it. Firms differ, and several share a platform: the operator
/// holds one login per firm, so the same venue flag means different things depending on whose login produced the
/// account. Conventions therefore belong to the <b>firm</b>, not the platform.
/// </para>
/// <para>
/// An <b>undeclared</b> stage resolves to <see cref="TradingMode.Undeclared"/>, which is tradeable nowhere.
/// Silence is not consent: the failure mode is "classify this before trading it", never "assumed practice, then
/// traded a funded account".
/// </para>
/// </remarks>
public sealed record FirmConventions
{
    private readonly IReadOnlyDictionary<AccountStage, bool> _capitalAtRisk;

    private FirmConventions(string firm, IReadOnlyDictionary<AccountStage, bool> capitalAtRisk, bool modeFollowsVenue)
    {
        Firm = firm;
        _capitalAtRisk = capitalAtRisk;
        ModeFollowsVenue = modeFollowsVenue;
    }

    /// <summary>Nothing declared. Every stage resolves to <see cref="TradingMode.Undeclared"/>.</summary>
    public static FirmConventions None { get; } = new(string.Empty, new Dictionary<AccountStage, bool>(), false);

    /// <summary>The firm these conventions belong to.</summary>
    public string Firm { get; }

    /// <summary>
    /// Whether this firm's mode comes from the <b>venue's</b> live/paper flag rather than from a per-stage
    /// declaration (gh#780) — true for a brokerage, false for a prop firm.
    /// </summary>
    /// <remarks>
    /// Deliberately named for the <i>behaviour</i>, not the taxonomy. The domain does not care that the firm is a
    /// brokerage; it cares which source is authoritative for mode — and a second firm taxonomy in
    /// <c>Domain</c> would be one more thing to drift against <c>Data.Entities.FirmType</c>, which the
    /// <c>Firm.ToConventions()</c> bridge already owns translating.
    /// </remarks>
    public bool ModeFollowsVenue { get; }

    /// <summary>Declares what each stage means at a firm.</summary>
    /// <param name="firm">The firm's name.</param>
    /// <param name="declarations">Each stage the operator has classified, and whether capital is at risk there.</param>
    /// <returns>The conventions.</returns>
    /// <exception cref="ArgumentException">
    /// A stage is declared more than once, is <see cref="AccountStage.Unknown"/>, or is not a defined
    /// <see cref="AccountStage"/>.
    /// </exception>
    public static FirmConventions For(string firm, params (AccountStage Stage, bool CapitalAtRisk)[] declarations)
    {
        Dictionary<AccountStage, bool> map = [];

        foreach ((AccountStage stage, bool atRisk) in declarations)
        {
            // A stage outside the enum would be stored and then resolved to Practice or Live by ModeFor --
            // something the domain cannot identify becoming tradeable. Refuse it at the door.
            if (!Enum.IsDefined(stage))
            {
                throw new ArgumentException(
                    $"'{(int)stage}' is not a defined account stage (firm '{firm}').", nameof(declarations));
            }

            // Unknown means the stage could not be read, so declaring what it means is incoherent. Accepting it
            // silently is worse than refusing: ModeFor ignores Unknown, so the operator would believe they had
            // classified something that still resolves to Undeclared.
            if (stage == AccountStage.Unknown)
            {
                throw new ArgumentException(
                    $"Stage '{AccountStage.Unknown}' cannot be declared — it means the stage could not be "
                    + $"identified (firm '{firm}').",
                    nameof(declarations));
            }

            // Two answers for one stage is an ambiguous safety input, not something to merge or last-write-wins.
            if (!map.TryAdd(stage, atRisk))
            {
                throw new ArgumentException(
                    $"Stage '{stage}' is declared more than once for firm '{firm}'.", nameof(declarations));
            }
        }

        return new FirmConventions(firm, map, modeFollowsVenue: false);
    }

    /// <summary>
    /// The conventions of a <b>brokerage</b>, whose accounts take their mode from the venue's own live/paper flag
    /// rather than from per-stage declarations (gh#780).
    /// </summary>
    /// <param name="firm">The firm's name.</param>
    /// <returns>Conventions that resolve mode from the venue flag supplied at <see cref="ModeFor(AccountStage, bool?)"/>.</returns>
    /// <remarks>
    /// <para>
    /// There are no stage declarations to make. A brokerage has no evaluation / funded ladder, its account names
    /// resolve to <see cref="AccountStage.Unknown"/>, and <see cref="For"/> refuses to declare <c>Unknown</c> on
    /// purpose — so the per-stage model cannot represent a brokerage at all. Routing them through it left every
    /// discovered brokerage account permanently <see cref="TradingMode.Undeclared"/>: untradeable in every
    /// environment, with nothing the operator could declare to fix it (gh#780, found in the gh#653 walk).
    /// </para>
    /// <para>
    /// <b>This does not weaken R-14 — it scopes it to the case it was written for.</b> "Never derive mode from the
    /// venue's flag" exists because a prop-firm <i>funded</i> account executes on a simulated engine while a real
    /// payout is at stake, so the flag is a lie there. At a brokerage the flag is the truth: a paper account is
    /// practice and a live account is live, and no declaration the operator could type would be more reliable
    /// than the venue's own answer — it could only disagree with it.
    /// </para>
    /// </remarks>
    public static FirmConventions ForBrokerage(string firm) =>
        new(firm, new Dictionary<AccountStage, bool>(), modeFollowsVenue: true);

    /// <summary>Whether the operator has classified this stage at all.</summary>
    /// <param name="stage">The stage.</param>
    /// <returns><see langword="true"/> if declared. Distinguishes an explicit "practice" from silence.</returns>
    public bool IsDeclared(AccountStage stage)
    {
        return IsDeclarable(stage) && _capitalAtRisk.ContainsKey(stage);
    }

    /// <summary>
    /// Resolves what an account means economically, given the venue's own live/paper flag for it (gh#780).
    /// </summary>
    /// <param name="stage">The account's stage. Ignored when <see cref="ModeFollowsVenue"/>.</param>
    /// <param name="venueReportsSimulated">
    /// Whether the <b>venue</b> reports this account as simulated / paper, or <see langword="null"/> when it has
    /// never been observed. Consulted <b>only</b> for a firm whose <see cref="ModeFollowsVenue"/> — at a prop firm
    /// this flag is precisely the thing R-14 refuses to trust.
    /// </param>
    /// <returns>The resolved mode, or <see cref="TradingMode.Undeclared"/> when nothing authorises an answer.</returns>
    public TradingMode ModeFor(AccountStage stage, bool? venueReportsSimulated)
    {
        // A brokerage's mode is the venue's answer, and the stage is not consulted at all -- there is no ladder to
        // read and no declaration to match. A prop firm's mode ignores the flag entirely (R-14): a funded account
        // reports `simulated` and is nonetheless Live, which is the inversion this branch must never introduce.
        //
        // Unobserved is NOT "not simulated": a null resolves to Undeclared rather than Live, so an account whose
        // flag nobody has read is refused everywhere until a discovery fills it in, rather than becoming
        // live-tradeable off a default.
        if (!ModeFollowsVenue)
        {
            return ModeFor(stage);
        }

        return venueReportsSimulated switch
        {
            true => TradingMode.Practice,
            false => TradingMode.Live,
            null => TradingMode.Undeclared,
        };
    }

    /// <summary>Resolves what an account at this stage means economically.</summary>
    /// <param name="stage">The account's stage.</param>
    /// <returns>
    /// <see cref="TradingMode.Practice"/> or <see cref="TradingMode.Live"/> where declared; otherwise
    /// <see cref="TradingMode.Undeclared"/>.
    /// </returns>
    /// <remarks>
    /// The stage-only form. It cannot answer for a firm whose <see cref="ModeFollowsVenue"/> — it has no flag to
    /// read — so such a firm resolves to <see cref="TradingMode.Undeclared"/> here: fail closed rather than guess.
    /// A caller that can supply the venue's flag uses <see cref="ModeFor(AccountStage, bool?)"/>.
    /// </remarks>
    public TradingMode ModeFor(AccountStage stage)
    {
        // An unreadable or unrecognised stage cannot inherit whatever the firm declared for something else.
        // For() already refuses to store either, so this is defence in depth rather than the only guard.
        if (!IsDeclarable(stage))
        {
            return TradingMode.Undeclared;
        }

        return _capitalAtRisk.TryGetValue(stage, out bool capitalAtRisk)
            ? capitalAtRisk ? TradingMode.Live : TradingMode.Practice
            : TradingMode.Undeclared;
    }

    private static bool IsDeclarable(AccountStage stage)
    {
        return stage != AccountStage.Unknown && Enum.IsDefined(stage);
    }
}
