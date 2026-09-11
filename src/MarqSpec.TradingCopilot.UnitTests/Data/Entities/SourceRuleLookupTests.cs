using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Data.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace MarqSpec.TradingCopilot.UnitTests.Data.Entities;

/// <summary>
/// Soft-reference resolution for <see cref="TriggerRecord.SourceRuleId"/> (gh#866): the id is stored without an FK,
/// and <see cref="SourceRuleLookup"/> navigates it to a real <see cref="Rule"/> when one exists. A missing, deleted,
/// or cross-tenant id returns null — the trigger outlives the rule and never sees another operator's row (R-20).
/// </summary>
public class SourceRuleLookupTests
{
    private readonly Guid _owner = Guid.NewGuid();
    private readonly string _database = Guid.NewGuid().ToString();

    private sealed record FixedUser(Guid UserId) : ICurrentUser;

    private TradingCopilotDbContext Context(Guid? asUser = null) =>
        new(new DbContextOptionsBuilder<TradingCopilotDbContext>().UseInMemoryDatabase(_database).Options,
            new FixedUser(asUser ?? _owner));

    [Fact]
    public async Task ResolveAsync_ShouldReturnNull_WhenSourceRuleIdIsNull()
    {
        await using TradingCopilotDbContext context = Context();

        Rule? resolved = await SourceRuleLookup.ResolveAsync(context.Rules, sourceRuleId: null, CancellationToken.None);

        resolved.Should().BeNull("a trigger authored over the API has no rule behind it");
    }

    [Fact]
    public async Task ResolveAsync_ShouldReturnTheRule_WhenTheIdMatchesAnOwnedRow()
    {
        Rule seeded = await SeedRuleAsync(_owner, "Fade an RSI extreme on ES");
        await using TradingCopilotDbContext context = Context();

        Rule? resolved = await SourceRuleLookup.ResolveAsync(context.Rules, seeded.Id, CancellationToken.None);

        resolved.Should().NotBeNull();
        resolved!.Id.Should().Be(seeded.Id);
        resolved.IntentText.Should().Be("Fade an RSI extreme on ES");
    }

    [Fact]
    public async Task ResolveAsync_ShouldReturnNull_WhenNoRuleRowExistsForTheId()
    {
        await using TradingCopilotDbContext context = Context();

        Rule? resolved = await SourceRuleLookup.ResolveAsync(
            context.Rules, Guid.NewGuid(), CancellationToken.None);

        resolved.Should().BeNull(
            "the reference is soft: a trigger whose rule was deleted (or never existed) still reads, with no rule");
    }

    [Fact]
    public async Task ResolveAsync_ShouldReturnNull_WhenTheRuleBelongsToAnotherOperator()
    {
        Rule strangers = await SeedRuleAsync(Guid.NewGuid(), "A stranger's practice");
        await using TradingCopilotDbContext context = Context();

        Rule? resolved = await SourceRuleLookup.ResolveAsync(context.Rules, strangers.Id, CancellationToken.None);

        resolved.Should().BeNull(
            "R-20: the lookup rides the tenant filter, so another operator's rule is invisible, not a leak");
    }

    [Fact]
    public async Task ResolveManyAsync_ShouldReturnOnlyTheOwnedIdsThatExist()
    {
        Rule present = await SeedRuleAsync(_owner, "Keep");
        Guid missing = Guid.NewGuid();
        await using TradingCopilotDbContext context = Context();

        IReadOnlyDictionary<Guid, Rule> resolved = await SourceRuleLookup.ResolveManyAsync(
            context.Rules, [present.Id, missing, null], CancellationToken.None);

        resolved.Should().ContainKey(present.Id);
        resolved.Should().NotContainKey(missing);
        resolved.Should().HaveCount(1);
        resolved[present.Id].IntentText.Should().Be("Keep");
    }

    [Fact]
    public async Task ResolveManyAsync_ShouldReturnEmpty_WhenEveryIdIsNull()
    {
        await using TradingCopilotDbContext context = Context();

        IReadOnlyDictionary<Guid, Rule> resolved = await SourceRuleLookup.ResolveManyAsync(
            context.Rules, [null, null], CancellationToken.None);

        resolved.Should().BeEmpty();
    }

    private async Task<Rule> SeedRuleAsync(Guid owner, string intent)
    {
        Rule rule = new()
        {
            Id = Guid.NewGuid(),
            UserId = owner,
            IntentText = intent,
            CreatedAt = DateTimeOffset.UnixEpoch,
        };
        await using TradingCopilotDbContext context = Context(owner);
        context.Rules.Add(rule);
        await context.SaveChangesAsync();
        return rule;
    }
}
