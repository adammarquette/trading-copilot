using System.Linq.Expressions;
using MarqSpec.TradingCopilot.Api.Ai;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Domain.Ai;
using Pgvector;

namespace MarqSpec.TradingCopilot.UnitTests.Api.Ai;

/// <summary>
/// The ranked recall's <b>owner-kind predicate</b> — the mapping from a consumer-facing <see cref="RetrievalKind"/> to
/// the stored <see cref="EmbeddingOwnerKind"/> the query actually filters on.
/// </summary>
/// <remarks>
/// <para>
/// The query itself (<c>CosineDistance</c> over a relational-only <c>Vector</c> column) is integration-tier, but the
/// predicate in front of it is a plain expression, and it is the part with a <b>silent</b> failure mode: the branches
/// differ by one token, and a slipped one makes that kind's recall return rows the hydrate cannot match, so retrieval
/// for it is empty <i>forever</i> with nothing thrown, nothing logged, and its partial HNSW index never planned
/// against. Every consumer's unit test fakes <see cref="IEmbeddingRecall"/> and so cannot reach this switch; the
/// integration suites over the real seam only ever ask for <see cref="RetrievalKind.News"/>. Compiling the shipping
/// expression here is therefore the only tier that pins the two new kinds at all.
/// </para>
/// <para>
/// Written as a check of the predicate <b>against</b> <see cref="EmbeddingOwnerKinds.For"/> rather than against
/// hard-coded expectations, so the read path and the write path (which calls <c>For</c> directly) are bound to one
/// mapping: they cannot diverge without this going red.
/// </para>
/// </remarks>
public class PgVectorEmbeddingRecallTests
{
    private static EmbeddingRecord Row(EmbeddingOwnerKind ownerKind) => new()
    {
        OwnerKind = ownerKind,
        OwnerId = "owner",
        Model = "embed-english-v3.0",
        Dimensions = 3,
        Embedding = new Vector(new float[] { 0.1f, 0.2f, 0.3f }),
        ContentHash = "hash",
        RecordedAt = DateTimeOffset.UnixEpoch,
    };

    private static Func<EmbeddingRecord, bool> Predicate(RetrievalKind kind) =>
        PgVectorEmbeddingRecall.PredicateFor(kind).Compile();

    [Theory]
    [InlineData(RetrievalKind.News)]
    [InlineData(RetrievalKind.Suggestion)]
    [InlineData(RetrievalKind.JournalEntry)]
    public void PredicateFor_ShouldAcceptExactlyItsOwnOwnerKind_AndRejectEveryOther(RetrievalKind kind)
    {
        Func<EmbeddingRecord, bool> predicate = Predicate(kind);
        EmbeddingOwnerKind expected = EmbeddingOwnerKinds.For(kind);

        predicate(Row(expected)).Should().BeTrue(
            "the ranked read for {0} must select the owner kind the embed pass writes it under", kind);

        // Every OTHER stored kind must be rejected -- including the ones nothing retrieves yet (Rule, MarketSnapshot,
        // Topic), because the store is polymorphic and a widened predicate would rank rows the hydrate cannot resolve.
        foreach (EmbeddingOwnerKind other in Enum.GetValues<EmbeddingOwnerKind>().Where(value => value != expected))
        {
            predicate(Row(other)).Should().BeFalse(
                "{0}'s recall must never select a {1} row", kind, other);
        }
    }

    [Fact]
    public void PredicateFor_ShouldCarryTheOwnerKindAsAConstant_SoThePartialIndexStaysPlannable()
    {
        // gh#864's hazard, asserted rather than only documented: a partial HNSW index is matched by comparing the
        // query's predicate to the index's, which needs a CONSTANT. A predicate closing over a variable emits a SQL
        // parameter instead, the owner kind degrades to a post-scan filter on the approximate candidate window, and a
        // crowd of closer other-kind rows starves the read to ZERO neighbours (reproduced at 15,000 rows by gh#861).
        // So the expression must hold a ConstantExpression, never a member access on a closure.
        foreach (RetrievalKind kind in RetrievalKinds.All)
        {
            Expression<Func<EmbeddingRecord, bool>> predicate = PgVectorEmbeddingRecall.PredicateFor(kind);

            // BeAssignableTo, not BeOfType: the runtime node is an internal BinaryExpression subclass.
            BinaryExpression comparison = predicate.Body.Should().BeAssignableTo<BinaryExpression>().Subject;
            comparison.NodeType.Should().Be(ExpressionType.Equal);

            // Enum equality compiles the operands through a Convert to the underlying type, so strip those before
            // asking what the right-hand side really is.
            ConstantExpression constant = Unwrap(comparison.Right).Should().BeAssignableTo<ConstantExpression>(
                "a captured variable would be emitted as a SQL parameter, which no partial index can be proven to cover")
                .Subject;

            // And it is the RIGHT constant: the same owner kind the write path takes from EmbeddingOwnerKinds.For.
            Convert.ToInt32(constant.Value, System.Globalization.CultureInfo.InvariantCulture)
                .Should().Be((int)EmbeddingOwnerKinds.For(kind));
        }
    }

    // Enum comparisons in an expression tree wrap their operands in a Convert to the underlying type; the question
    // this suite asks is about what sits underneath, so peel them off.
    private static Expression Unwrap(Expression expression) =>
        expression is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } unary
            ? Unwrap(unary.Operand)
            : expression;

    [Fact]
    public void PredicateFor_ShouldRefuse_TheUnknownKind()
    {
        Action build = () => PgVectorEmbeddingRecall.PredicateFor(RetrievalKind.Unknown);

        build.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void PredicateFor_ShouldRefuse_AValueOutsideTheEnum()
    {
        Action build = () => PgVectorEmbeddingRecall.PredicateFor((RetrievalKind)99);

        build.Should().Throw<ArgumentOutOfRangeException>();
    }
}
