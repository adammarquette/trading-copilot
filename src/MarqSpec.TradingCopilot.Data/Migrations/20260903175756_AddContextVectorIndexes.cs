using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarqSpec.TradingCopilot.Data.Migrations
{
    /// <summary>
    /// Adds a <b>partial</b> HNSW cosine index per newly retrievable owner kind (gh#1065): one over the
    /// <c>Suggestion</c> rows and one over the <c>JournalEntry</c> rows of the polymorphic <c>Embeddings</c> table.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why an index per kind, and not one shared table-wide one.</b> gh#864 established the rule this migration
    /// simply follows: a vector-ordered read whose owner-kind predicate is applied as a post-scan <b>Filter</b> can
    /// return <b>zero</b> neighbours though thousands exist, because the approximate HNSW scan bounds its candidate
    /// window (about <c>hnsw.ef_search</c>, default 40) <i>before</i> that filter runs. gh#864 wrote it down as "any
    /// other owner kind needing a vector-ordered read wants its own partial index rather than a shared table-wide
    /// one"; gh#1065 is the first kind to need one, so here they are.
    /// </para>
    /// <para>
    /// <b>The literals are load-bearing.</b> <c>WHERE "OwnerKind" = 2</c> is <c>EmbeddingOwnerKind.Suggestion</c> and
    /// <c>= 6</c> is <c>EmbeddingOwnerKind.JournalEntry</c>. A partial index predicate is matched against the query's
    /// predicate by the planner, so it must be the same constant the read sends — which is exactly why
    /// <c>PgVectorEmbeddingRecall</c> dispatches to one query per kind with a literal owner kind rather than
    /// parameterising it.
    /// </para>
    /// <para>
    /// <b>No column, table or constraint change.</b> The <c>OwnerKind</c> integer column's only constraint is
    /// <c>&lt;&gt; 0</c>, and the primary key <c>(OwnerKind, OwnerId, Model)</c> plus the existing indexes already
    /// admit any owner kind — the same "new owner kind, no migration" property gh#854 relied on for
    /// <c>Topic</c>. Only the two ANN indexes are new.
    /// </para>
    /// <para>
    /// Guarded on the table existing at all: where pgvector was unavailable, <c>AddEmbeddingStore</c> deliberately
    /// created no <c>Embeddings</c> table (gh#109), and this must degrade the same way rather than fail the migration.
    /// </para>
    /// </remarks>
    public partial class AddContextVectorIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $body$
                BEGIN
                    IF to_regclass('"Embeddings"') IS NOT NULL THEN
                        -- WHERE "OwnerKind" = 2 is EmbeddingOwnerKind.Suggestion; = 6 is .JournalEntry. The literals
                        -- are deliberate: a partial index predicate is matched against the query's predicate by the
                        -- planner, so each has to be the same constant its read sends.
                        CREATE INDEX IF NOT EXISTS "IX_Embeddings_Vector_Cosine_Suggestion"
                            ON "Embeddings" USING hnsw ("Embedding" vector_cosine_ops)
                            WHERE "OwnerKind" = 2;

                        CREATE INDEX IF NOT EXISTS "IX_Embeddings_Vector_Cosine_JournalEntry"
                            ON "Embeddings" USING hnsw ("Embedding" vector_cosine_ops)
                            WHERE "OwnerKind" = 6;
                    ELSE
                        RAISE WARNING 'pgvector unavailable: no "Embeddings" table, so the suggestion / journal-entry vector indexes were NOT created (gh#109/gh#1065). Semantic retrieval is already off for this deployment; trading is unaffected.';
                    END IF;
                END
                $body$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""DROP INDEX IF EXISTS "IX_Embeddings_Vector_Cosine_Suggestion";""");
            migrationBuilder.Sql("""DROP INDEX IF EXISTS "IX_Embeddings_Vector_Cosine_JournalEntry";""");
        }
    }
}
