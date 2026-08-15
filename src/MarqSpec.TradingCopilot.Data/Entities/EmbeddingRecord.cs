using Pgvector;

namespace MarqSpec.TradingCopilot.Data.Entities;

/// <summary>What an embedding belongs to (gh#109). Part of the row's identity, so two owner kinds never collide.</summary>
public enum EmbeddingOwnerKind
{
    /// <summary>Not an owner — the refusable zero. Never persisted.</summary>
    Unknown = 0,

    /// <summary>A <see cref="NewsRecord"/> — the first real consumer (gh#377, R-2).</summary>
    SoftSignal = 1,

    /// <summary>A suggestion's rationale (R-4). Arrives with the suggestion pipeline.</summary>
    Suggestion = 2,

    /// <summary>A rulebook rule (R-7). Arrives with epic gh#15, which has not started.</summary>
    Rule = 3,

    /// <summary>A market snapshot at issuance (R-8).</summary>
    MarketSnapshot = 4,

    /// <summary>A <see cref="NewsTopic"/> — its name + keywords, embedded for semantic topic match (gh#854, R-2).</summary>
    Topic = 5,
}

/// <summary>
/// One stored embedding — the data dictionary's polymorphic <c>VEC</c> row (§10–§12), and the whole of the
/// system's third storage shape (gh#109, ADR-0001, engineering §2).
/// </summary>
/// <remarks>
/// <para>
/// <b>Polymorphic by owner kind + id</b>, as the dictionary specifies: one table and one index serve every
/// embeddable type, and cross-type retrieval is a filter rather than a <c>UNION</c>. The cost is honest — no FK
/// to the owner, so a deleted owner leaves its vector behind until swept.
/// </para>
/// <para>
/// <b>The content hash is what makes re-embedding idempotent.</b> Re-ingestion is normal (gh#358 re-polls the
/// same stories), and embedding is a <i>paid</i> call, so the hash lets an unchanged owner be recognised and
/// skipped. Without it the default behaviour is one duplicate row and one wasted API charge per poll.
/// </para>
/// <para>
/// <b>Model and dimensions are stored, not assumed.</b> Vectors from different models are not comparable, so a
/// row that does not record which model produced it becomes unusable the moment the model changes — and it
/// <i>will</i> change.
/// </para>
/// <para>
/// <b>Global / not <c>IUserOwned</c></b> (R-20), following its owners: news and market data are shared, and a
/// tenant filter here would hide derived data from the operator who owns the deployment.
/// </para>
/// </remarks>
public sealed class EmbeddingRecord
{
    /// <summary>What kind of thing this embeds. Part of the key.</summary>
    public required EmbeddingOwnerKind OwnerKind { get; set; }

    /// <summary>
    /// The owner's identity, as text so any key shape fits — a <c>NewsRecord</c> is keyed by its dedup key, not
    /// a Guid. Part of the key.
    /// </summary>
    public required string OwnerId { get; set; }

    /// <summary>The model that produced the vector. Part of the key: two models' vectors coexist, never merge.</summary>
    public required string Model { get; set; }

    /// <summary>The vector's width, recorded so a dimension change is detectable rather than a silent mismatch.</summary>
    public required int Dimensions { get; set; }

    /// <summary>The embedding itself.</summary>
    public required Vector Embedding { get; set; }

    /// <summary>
    /// A hash of the embedded content. Unchanged hash → the stored vector still describes the owner, so the
    /// paid re-embed is skipped.
    /// </summary>
    public required string ContentHash { get; set; }

    /// <summary>When this row was written. Always UTC.</summary>
    public required DateTimeOffset RecordedAt { get; set; }
}
