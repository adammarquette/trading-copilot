using System.Text.RegularExpressions;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.IntegrationTests.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MarqSpec.TradingCopilot.IntegrationTests.Data;

/// <summary>
/// Verifies every database identifier §2 of <c>documentation/integration-test-audit.md</c> claims a suite proves
/// <b>"by name"</b> — <c>CK_Trades_Mode_NotUndeclared</c>, <c>ct_suggestions_mode_matches_account</c>, and so on —
/// still exists on the <b>live migrated schema</b> (gh#981, the gh#979 class).
/// </summary>
/// <remarks>
/// <para>
/// <b>The defect this closes:</b> those 17-odd names are hand-copied prose. gh#762 renamed
/// <c>IX_SoftSignalFeedbacks_UserId_NewsDedupKey</c> to <c>UX_SoftSignalFeedback_Importance</c> and updated the
/// suite that proves it — but left §2 naming an index that no longer exists (gh#979), and
/// <c>check-integration-test-inventory.sh</c> cannot catch it: it verifies a suite <i>exists</i>, never that a
/// row's <i>content</i> is true.
/// </para>
/// <para>
/// <b>Why the live schema, and not the source tree or the ModelSnapshot</b> (gh#981's own measurement): grepping
/// the tree is a guard that cannot fail — migrations are append-only, so a renamed index still appears in the
/// migration that created it AND the one that renamed it, and grep finds all 17 names and flags zero. The
/// ModelSnapshot flags 4 of 17, but 3 of those are false positives — EF conventional FK/index names
/// (<c>FK_Suggestions_Suggestions_SupersedesId</c>, <c>IX_NewsTopics_Name</c>) are never spelled there, and a
/// raw-SQL constraint trigger (<c>ct_suggestions_mode_matches_account</c>) is not a model object at all. Only
/// <c>pg_constraint</c> / <c>pg_indexes</c> / <c>pg_trigger</c> answer definitively for every kind alike.
/// </para>
/// <para>
/// <b>The harvest is mechanical</b> — a regex over §2's own markdown text, never a hand-maintained list (a second
/// hand-copied list would carry the same defect as the first). Most names are spelled in full inside backticks;
/// a few rows use the doc's own shorthand for a family sharing one table prefix (e.g.
/// <c>`CK_Suggestions_Mode_NotUndeclared` / `_State_NotUnknown` / `_Size_Positive`</c>) — <see cref="Harvest"/>
/// expands those against the full name immediately preceding them, so no shorthand identifier goes unchecked.
/// </para>
/// </remarks>
public class InventoryIdentifierLiveSchemaIntegrationTests : IClassFixture<PostgresApiFactory>
{
    private readonly PostgresApiFactory _factory;

    public InventoryIdentifierLiveSchemaIntegrationTests(PostgresApiFactory factory)
    {
        _factory = factory;
    }

    // =================================================================================================================
    // Case 0: the anti-vacuity anchor. Without it, a harvester that silently matches nothing would pass every
    // Theory case below (an empty MemberData runs zero cases, which xUnit reports as a pass).
    // =================================================================================================================

    [Fact]
    public void Harvest_ShouldFindANonEmptySet_ContainingKnownMembersOfEveryKind()
    {
        IReadOnlySet<string> identifiers = Harvest(ReadSection2());

        identifiers.Should().NotBeEmpty(
            "§2 names several database objects \"by name\" — a harvester that silently matches nothing must not pass");

        // One stable, long-lived member per object kind actually in play (gh#981's own measurement: 13 CK_, 2 IX_,
        // 1 FK_, 1 ct_) — so a regex that quietly stopped matching one kind's backtick shape fails HERE, not by
        // every Theory case for that kind silently disappearing from the MemberData.
        identifiers.Should().Contain("CK_Trades_Mode_NotUndeclared", "a known CHECK constraint name must survive the harvest");
        identifiers.Should().Contain("IX_NewsTopics_Name", "a known index name must survive the harvest");
        identifiers.Should().Contain("FK_Suggestions_Suggestions_SupersedesId", "a known foreign-key name must survive the harvest");
        identifiers.Should().Contain("ct_suggestions_mode_matches_account", "a known constraint-trigger name must survive the harvest");

        // The doc's own shorthand continuation ("`CK_Foo_Bar` / `_Baz`") must expand to a full name, not the bare
        // "_Baz" fragment — proven on a pair that only exists in that shape.
        identifiers.Should().Contain("CK_ConditionalOrders_Direction_NotUnknown",
            "a shorthand continuation ('`CK_ConditionalOrders_CancelDrift_StaleSide` / `_Direction_NotUnknown`') must expand "
            + "to the full sibling name sharing its table prefix");
        identifiers.Should().NotContain(id => id.StartsWith('_'),
            "an unexpanded shorthand fragment ('_Direction_NotUnknown') is not a real database identifier");

        // The regression this suite's own harvest is built around (see _identifierChain's remarks): a bare
        // two-segment "`CK_StopPlans`" prose reference in the ModifyWorkingOrderIntegrationTests row must NOT be
        // harvested as if it were a specific constraint name — it names no real object, and would be a
        // permanently-false assertion no doc fix could ever turn green.
        identifiers.Should().NotContain("CK_StopPlans",
            "a bare {Kind}_{Table} token with no description segment is prose shorthand for a family (row 87's "
            + "own `CK_StopPlans_*` wildcard already covers it), not a specific 'proven by name' claim");
    }

    // =================================================================================================================
    // Case 1: every harvested identifier is checked against the live migrated schema — pg_constraint / pg_indexes /
    // pg_trigger, never the source tree and never the ModelSnapshot.
    // =================================================================================================================

    public static IEnumerable<object[]> HarvestedIdentifiers() =>
        Harvest(ReadSection2()).OrderBy(name => name, StringComparer.Ordinal).Select(name => new object[] { name });

    [Theory]
    [MemberData(nameof(HarvestedIdentifiers))]
    public async Task Identifier_ShouldExistOnTheLiveMigratedSchema(string identifier)
    {
        // This is the case that goes red on gh#979's stale name: with §2 still naming
        // IX_SoftSignalFeedbacks_UserId_NewsDedupKey (renamed by gh#762 to UX_SoftSignalFeedback_Importance), this
        // Theory case runs for the OLD name, pg_indexes has no such entry, and the assertion below fails — for
        // the right reason: the row is stale, not that the suite is broken. See the suite's own remarks / the PR
        // description for the prove-the-red transcript required by the QA contract.
        bool exists = await ExistsOnLiveSchemaAsync(identifier);

        exists.Should().BeTrue(
            $"§2 of documentation/integration-test-audit.md asserts a suite proves '{identifier}' \"by name\" against "
            + "the live schema — if this fails, the object was renamed or dropped and the row is stale (the gh#979 "
            + "class); fix the row in the same PR that renamed the object, per the same-PR docs rule");
    }

    // =================================================================================================================
    // Live-schema lookups. One query family per object kind, because only pg_constraint / pg_indexes / pg_trigger
    // together answer definitively for all four kinds in play (a raw-SQL constraint trigger has no model
    // representation at all, and EF's conventional FK/index names are never spelled in the ModelSnapshot).
    // =================================================================================================================

    private async Task<bool> ExistsOnLiveSchemaAsync(string identifier)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        TradingCopilotDbContext db = scope.ServiceProvider.GetRequiredService<TradingCopilotDbContext>();

        return identifier switch
        {
            _ when identifier.StartsWith("CK_", StringComparison.Ordinal) => await ConstraintExistsAsync(db, identifier, 'c'),
            _ when identifier.StartsWith("FK_", StringComparison.Ordinal) => await ConstraintExistsAsync(db, identifier, 'f'),
            _ when identifier.StartsWith("IX_", StringComparison.Ordinal) => await IndexExistsAsync(db, identifier),
            _ when identifier.StartsWith("UX_", StringComparison.Ordinal) => await IndexExistsAsync(db, identifier),
            _ when identifier.StartsWith("ct_", StringComparison.Ordinal) => await ConstraintTriggerExistsAsync(db, identifier),
            _ => throw new InvalidOperationException(
                $"'{identifier}' matches none of the known §2 identifier prefixes (CK_/FK_/IX_/UX_/ct_) — extend "
                + $"{nameof(ExistsOnLiveSchemaAsync)} for this kind before trusting the harvest"),
        };
    }

    // contype: 'c' = CHECK, 'f' = FOREIGN KEY. Scoped to the 'public' schema — the only one this app uses — so an
    // identically-named system object elsewhere in the catalog can never manufacture a false positive.
    private static async Task<bool> ConstraintExistsAsync(TradingCopilotDbContext db, string name, char contype)
    {
        int count = await db.Database.SqlQuery<int>(
                $"""
                 SELECT COUNT(*)::int AS "Value"
                 FROM pg_constraint c
                 JOIN pg_namespace n ON n.oid = c.connamespace
                 WHERE c.conname = {name} AND c.contype = {contype} AND n.nspname = 'public'
                 """)
            .SingleAsync();
        return count > 0;
    }

    // pg_indexes already carries schemaname, and reports every index regardless of how it was created (a bare
    // CREATE INDEX or one backing a UNIQUE constraint) — exactly the "answers for every kind" property this
    // suite exists to use instead of the ModelSnapshot, which never spells a conventional index name.
    private static async Task<bool> IndexExistsAsync(TradingCopilotDbContext db, string name)
    {
        int count = await db.Database.SqlQuery<int>(
                $"""SELECT COUNT(*)::int AS "Value" FROM pg_indexes WHERE indexname = {name} AND schemaname = 'public'""")
            .SingleAsync();
        return count > 0;
    }

    // A PostgreSQL CREATE CONSTRAINT TRIGGER (the gh#96 mode-matches-account guard's own shape) registers in
    // pg_trigger like any other trigger; pg_constraint also gains a contype='t' row of the same name, but
    // pg_trigger is the authority every constraint trigger name resolves through, so it is the one query that
    // needs no second kind-specific branch.
    private static async Task<bool> ConstraintTriggerExistsAsync(TradingCopilotDbContext db, string name)
    {
        int count = await db.Database.SqlQuery<int>(
                $"""
                 SELECT COUNT(*)::int AS "Value"
                 FROM pg_trigger t
                 JOIN pg_class c ON c.oid = t.tgrelid
                 JOIN pg_namespace n ON n.oid = c.relnamespace
                 WHERE t.tgname = {name} AND n.nspname = 'public' AND NOT t.tgisinternal
                 """)
            .SingleAsync();
        return count > 0;
    }

    // =================================================================================================================
    // The harvest: a regex over §2's own markdown text. Mechanical by construction — no hand-maintained list can
    // drift from the doc, because there is no second list; this IS the doc, read at test time.
    // =================================================================================================================

    // A full identifier, immediately followed by zero or more shorthand continuations sharing its table prefix —
    // the doc's own convention for a family of sibling constraints, e.g.
    // "`CK_Suggestions_Mode_NotUndeclared` / `_State_NotUnknown` / `_Size_Positive`". Captures the full name once
    // and the whole continuation tail once; the continuation regex below walks the tail for the individual
    // shorthand pieces.
    //
    // Requires AT LEAST TWO segments after the kind prefix (`_[A-Za-z0-9]+_[A-Za-z0-9_]+`, not a bare
    // `_[A-Za-z0-9_]+`) because every real object name in this codebase's convention is at minimum
    // `{Kind}_{Table}_{Description}` (EF's own default FK/index naming, and every hand-named CHECK/trigger
    // follows it too) — a bare `{Kind}_{Table}` two-segment token is never a real object name here. Without this,
    // the naive shape also matches "`CK_StopPlans`" in the ModifyWorkingOrderIntegrationTests row (§2, "so
    // `CK_StopPlans` can never trip post-reprice") — plain-English shorthand for the family row 87 already
    // covers via an explicit `CK_StopPlans_*` wildcard, never itself a real constraint name — which would harvest
    // a permanently-false "proven by name" claim no fix could ever turn green, exactly the ModelSnapshot's own
    // 75%-false-positive failure mode gh#981 exists to avoid.
    private static readonly Regex _identifierChain = new(
        @"`((?:CK|IX|FK|UX|ct)_[A-Za-z0-9]+_[A-Za-z0-9_]+)`((?:\s*/\s*`_[A-Za-z0-9_]+`)*)",
        RegexOptions.Compiled);

    private static readonly Regex _continuation = new(@"`(_[A-Za-z0-9_]+)`", RegexOptions.Compiled);

    /// <summary>
    /// Harvests every identifier §2 names "by name": each full <c>`PREFIX_Table_Rest`</c> token, plus every
    /// shorthand continuation (<c>`_Rest`</c>) expanded against the table prefix (its first two underscore
    /// segments, e.g. <c>CK_Suggestions</c>) of the full name immediately preceding it.
    /// </summary>
    internal static IReadOnlySet<string> Harvest(string section2Markdown)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);

        foreach (Match chain in _identifierChain.Matches(section2Markdown))
        {
            string full = chain.Groups[1].Value;
            found.Add(full);

            string[] segments = full.Split('_');
            if (segments.Length < 2)
            {
                continue; // no table prefix to share — nothing to expand a continuation against
            }

            string tablePrefix = segments[0] + "_" + segments[1]; // e.g. "CK_Suggestions", "CK_ConditionalOrders"
            foreach (Match piece in _continuation.Matches(chain.Groups[2].Value))
            {
                found.Add(tablePrefix + piece.Groups[1].Value); // "CK_Suggestions" + "_State_NotUnknown"
            }
        }

        return found;
    }

    /// <summary>
    /// Reads §2 ("Current Integration Test Inventory") out of the audit doc — the harvest is scoped to that
    /// section alone, matching gh#981's own scope, so a wildcard family reference elsewhere in the doc (e.g. §1's
    /// <c>`CK_ConditionalOrders_*`</c>) is never mistaken for a literal per-object claim.
    /// </summary>
    internal static string ReadSection2()
    {
        string markdown = File.ReadAllText(AuditDocPath());

        int start = markdown.IndexOf("## 2. Current Integration Test Inventory", StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0,
            "documentation/integration-test-audit.md must contain its §2 header — if this fails the doc was "
            + "restructured and the harvest's section boundary needs updating, not the harvested identifiers");

        int end = markdown.IndexOf("\n## 3.", start, StringComparison.Ordinal);
        end.Should().BeGreaterThan(start, "§2 must be terminated by a §3 header, or the harvest cannot bound its scan");

        return markdown[start..end];
    }

    // Copied next to the test binary by the csproj's Content Include (the same BaseDirectory-not-source-tree
    // convention AlertRuleSeriesReconciliationTests uses for the observability assets), so the suite reads it
    // identically in CI and local dev regardless of the working directory a test runner chooses.
    private static string AuditDocPath() =>
        Path.Combine(AppContext.BaseDirectory, "documentation", "integration-test-audit.md");
}
