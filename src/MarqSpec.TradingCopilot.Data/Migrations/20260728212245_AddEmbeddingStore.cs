using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarqSpec.TradingCopilot.Data.Migrations
{
    /// <summary>
    /// The pgvector embedding store (gh#109, ADR-0001, engineering §2) — the system's third storage shape.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This degrade is not the Timescale degrade, and that decided its shape.</b> Without <c>timescaledb</c>
    /// the <c>Events</c> table still exists and every query still works — you lose compression and retention.
    /// Without <c>vector</c> there is <b>no column type at all</b>, so the table cannot be created. The choice
    /// was therefore: skip the table and start, or refuse to start.
    /// </para>
    /// <para>
    /// <b>Skip and start, loudly.</b> Refusing to start would let an unavailable <i>retrieval</i> feature take
    /// down the <b>safety-critical auto-flatten</b> (R-13) — a system that will not run before the CME close
    /// because semantic search is missing has its priorities exactly inverted. Nothing on the trading path
    /// depends on embeddings.
    /// </para>
    /// <para>
    /// But skipping <i>silently</i> is the failure this codebase refuses everywhere else (gh#227's silent hole,
    /// gh#245's unemitted metrics, gh#306's declared-unknown). So the absence is <b>declared</b>: this warns
    /// during migration, and <c>IEmbeddingProvider.IsAvailable</c> reports false so retrieval refuses rather
    /// than returning an empty result set that reads as "nothing is relevant".
    /// </para>
    /// <para>
    /// This only bites a managed or BYO Postgres: the compose image (<c>timescale/timescaledb-ha:pg17</c>)
    /// bundles pgvector.
    /// </para>
    /// </remarks>
    public partial class AddEmbeddingStore : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Raw SQL rather than CreateTable, for the same reason AddEventBackbone uses it: EF has no vocabulary
            // for a conditional extension, and the vector column type does not exist to be declared until the
            // extension is enabled. An EF-generated CreateTable would simply fail on a Postgres without pgvector,
            // which is precisely the case this has to survive.
            migrationBuilder.Sql("""
                DO $body$
                BEGIN
                    IF EXISTS (SELECT 1 FROM pg_available_extensions WHERE name = 'vector') THEN
                        CREATE EXTENSION IF NOT EXISTS vector;

                        CREATE TABLE "Embeddings" (
                            "OwnerKind"   integer                  NOT NULL,
                            "OwnerId"     character varying(512)   NOT NULL,
                            "Model"       character varying(128)   NOT NULL,
                            "Dimensions"  integer                  NOT NULL,
                            "Embedding"   vector(1024)             NOT NULL,
                            "ContentHash" character varying(64)    NOT NULL,
                            "RecordedAt"  timestamp with time zone NOT NULL,
                            CONSTRAINT "PK_Embeddings" PRIMARY KEY ("OwnerKind", "OwnerId", "Model"),
                            CONSTRAINT "CK_Embeddings_OwnerKind_NotUnknown" CHECK ("OwnerKind" <> 0)
                        );

                        CREATE INDEX "IX_Embeddings_OwnerKind_RecordedAt"
                            ON "Embeddings" ("OwnerKind", "RecordedAt");

                        -- HNSW over IVFFlat: IVFFlat must be built AFTER representative data exists and needs a
                        -- list count tuned to row count, so an empty-table migration cannot build a good one.
                        -- HNSW builds usefully on an empty table and stays correct as rows arrive, which is the
                        -- right trade for a store that starts empty and grows continuously.
                        --
                        -- Cosine distance because embedding models emit direction-normalised vectors: cosine is
                        -- what the model was trained for, and L2 over them would rank partly by magnitude, which
                        -- here is noise.
                        CREATE INDEX "IX_Embeddings_Vector_Cosine"
                            ON "Embeddings" USING hnsw ("Embedding" vector_cosine_ops);
                    ELSE
                        RAISE WARNING 'pgvector unavailable: the "Embeddings" table was NOT created, so semantic retrieval is OFF for this deployment (gh#109). Unlike the timescaledb degrade this removes the table entirely, because without the extension there is no vector column type. Trading is unaffected - nothing on the R-13 path depends on embeddings.';
                    END IF;
                END
                $body$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Guarded: on a deployment where Up() skipped the table, a bare DROP TABLE would fail the rollback.
            // The extension is deliberately left in place -- another schema may depend on it, and dropping a
            // shared extension to undo one table is not this migration's business.
            migrationBuilder.Sql("""DROP TABLE IF EXISTS "Embeddings";""");
        }
    }
}
