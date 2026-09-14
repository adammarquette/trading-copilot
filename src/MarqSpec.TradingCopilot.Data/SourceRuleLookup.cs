using MarqSpec.TradingCopilot.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace MarqSpec.TradingCopilot.Data;

/// <summary>
/// Resolves <see cref="TriggerRecord.SourceRuleId"/> to a <see cref="Rule"/> without an FK (gh#866, R-7).
/// </summary>
/// <remarks>
/// The id is a <b>soft</b> reference: a missing, deleted, or (via the tenant filter) other-operator row
/// returns <see langword="null"/>. The trigger outlives the rule; the lookup never couples their lifetimes.
/// </remarks>
public static class SourceRuleLookup
{
    /// <summary>Navigates a single source-rule id to the owned <see cref="Rule"/> row, if one exists.</summary>
    /// <param name="rules">The tenant-filtered rule set.</param>
    /// <param name="sourceRuleId">The soft reference, or <see langword="null"/> when the trigger has no rule.</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    /// <returns>The matching rule, or <see langword="null"/> when the id is absent or no owned row matches.</returns>
    public static async Task<Rule?> ResolveAsync(
        IQueryable<Rule> rules,
        Guid? sourceRuleId,
        CancellationToken cancellationToken)
    {
        if (sourceRuleId is not Guid id)
        {
            return null;
        }

        return await rules.FirstOrDefaultAsync(rule => rule.Id == id, cancellationToken);
    }

    /// <summary>
    /// Batch-navigates source-rule ids to owned <see cref="Rule"/> rows. Missing and null ids are omitted.
    /// </summary>
    /// <param name="rules">The tenant-filtered rule set.</param>
    /// <param name="sourceRuleIds">The soft references to resolve (nulls ignored).</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    /// <returns>The owned rules keyed by id. Ids with no matching row are absent.</returns>
    public static async Task<IReadOnlyDictionary<Guid, Rule>> ResolveManyAsync(
        IQueryable<Rule> rules,
        IEnumerable<Guid?> sourceRuleIds,
        CancellationToken cancellationToken)
    {
        Guid[] ids = sourceRuleIds.OfType<Guid>().Distinct().ToArray();
        if (ids.Length == 0)
        {
            return new Dictionary<Guid, Rule>();
        }

        return await rules
            .Where(rule => ids.Contains(rule.Id))
            .ToDictionaryAsync(rule => rule.Id, cancellationToken);
    }
}
