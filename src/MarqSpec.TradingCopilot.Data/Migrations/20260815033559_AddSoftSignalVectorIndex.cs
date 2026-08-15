using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarqSpec.TradingCopilot.Data.Migrations
{
    /// <summary>
    /// A <b>partial</b> HNSW cosine index over the soft-signal rows only (gh#864), so the nearest-news read's
    /// vector search happens <i>inside</i> the owner kind it filters on rather than across the whole polymorphic
    /// table.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The defect this closes.</b> <c>PgVectorNewsSimilarity.NearestNewsAsync</c> filters
    /// <c>OwnerKind = SoftSignal</c> and orders by cosine distance. The table-wide
    /// <c>IX_Embeddings_Vector_Cosine</c> covers no such predicate, so Postgres may serve the <c>ORDER BY … LIMIT</c>
    /// from the HNSW graph and apply <c>OwnerKind</c> as a post-scan <b>Filter</b> — and the approximate scan bounds
    /// its candidate window (about <c>hnsw.ef_search</c>, default 40) <i>before</i> that filter runs. When the other
    /// owner kinds crowd closer to the query vector, every candidate in the window is discarded and the read returns
    /// <b>zero</b> neighbours though thousands exist. gh#861's suite reproduces it deterministically at 15,000 rows
    /// with 3,750 soft signals.
    /// </para>
    /// <para>
    /// <b>Why a partial index rather than a bigger window.</b> Raising <c>hnsw.ef_search</c> makes starvation less
    /// likely without making it impossible — a large enough, close enough crowd defeats any fixed window, and the
    /// cost is paid on every query. A partial index removes the hazard <b>by construction</b>: the index contains
    /// only soft-signal rows, so there is nothing for a post-filter to discard. It is also the cheaper index, at
    /// roughly the soft-signal share of the table.
    /// </para>
    /// <para>
    /// <b>One kind, not all five.</b> Only the soft-signal read orders by distance today; the topic and by-owner
    /// reads fetch by key and rank in process. A per-kind index is added when that kind gets a vector-ordered query,
    /// rather than paying for four indexes that nothing plans against.
    /// </para>
    /// <para>
    /// Guarded on the table existing at all: where pgvector was unavailable, <c>AddEmbeddingStore</c> deliberately
    /// created no <c>Embeddings</c> table (gh#109), and this must degrade the same way rather than fail the migration.
    /// </para>
    /// </remarks>
    public partial class AddSoftSignalVectorIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $body$
                BEGIN
                    IF to_regclass('"Embeddings"') IS NOT NULL THEN
                        -- WHERE "OwnerKind" = 1 is EmbeddingOwnerKind.SoftSignal. The literal is deliberate: a
                        -- partial index predicate is matched against the query's predicate by the planner, so it
                        -- has to be the same constant the read sends.
                        CREATE INDEX IF NOT EXISTS "IX_Embeddings_Vector_Cosine_SoftSignal"
                            ON "Embeddings" USING hnsw ("Embedding" vector_cosine_ops)
                            WHERE "OwnerKind" = 1;
                    ELSE
                        RAISE WARNING 'pgvector unavailable: no "Embeddings" table, so the soft-signal vector index was NOT created (gh#109/gh#864). Semantic retrieval is already off for this deployment; trading is unaffected.';
                    END IF;
                END
                $body$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""DROP INDEX IF EXISTS "IX_Embeddings_Vector_Cosine_SoftSignal";""");
        }
    }
}
