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

    private FirmConventions(string firm, IReadOnlyDictionary<AccountStage, bool> capitalAtRisk)
    {
        Firm = firm;
        _capitalAtRisk = capitalAtRisk;
    }

    /// <summary>Nothing declared. Every stage resolves to <see cref="TradingMode.Undeclared"/>.</summary>
    public static FirmConventions None { get; } = new(string.Empty, new Dictionary<AccountStage, bool>());

    /// <summary>The firm these conventions belong to.</summary>
    public string Firm { get; }

    /// <summary>Declares what each stage means at a firm.</summary>
    /// <param name="firm">The firm's name.</param>
    /// <param name="declarations">Each stage the operator has classified, and whether capital is at risk there.</param>
    /// <returns>The conventions.</returns>
    /// <exception cref="ArgumentException">A stage is declared more than once.</exception>
    public static FirmConventions For(string firm, params (AccountStage Stage, bool CapitalAtRisk)[] declarations)
    {
        Dictionary<AccountStage, bool> map = [];

        foreach ((AccountStage stage, bool atRisk) in declarations)
        {
            // Two answers for one stage is an ambiguous safety input, not something to merge or last-write-wins.
            if (!map.TryAdd(stage, atRisk))
            {
                throw new ArgumentException(
                    $"Stage '{stage}' is declared more than once for firm '{firm}'.", nameof(declarations));
            }
        }

        return new FirmConventions(firm, map);
    }

    /// <summary>Whether the operator has classified this stage at all.</summary>
    /// <param name="stage">The stage.</param>
    /// <returns><see langword="true"/> if declared. Distinguishes an explicit "practice" from silence.</returns>
    public bool IsDeclared(AccountStage stage)
    {
        return stage != AccountStage.Unknown && _capitalAtRisk.ContainsKey(stage);
    }

    /// <summary>Resolves what an account at this stage means economically.</summary>
    /// <param name="stage">The account's stage.</param>
    /// <returns>
    /// <see cref="TradingMode.Practice"/> or <see cref="TradingMode.Live"/> where declared; otherwise
    /// <see cref="TradingMode.Undeclared"/>.
    /// </returns>
    public TradingMode ModeFor(AccountStage stage)
    {
        // An unreadable stage cannot inherit whatever the firm declared for something else.
        if (stage == AccountStage.Unknown)
        {
            return TradingMode.Undeclared;
        }

        return _capitalAtRisk.TryGetValue(stage, out bool capitalAtRisk)
            ? capitalAtRisk ? TradingMode.Live : TradingMode.Practice
            : TradingMode.Undeclared;
    }
}
