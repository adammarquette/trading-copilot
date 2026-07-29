namespace MarqSpec.TradingCopilot.Data.Tenancy;

/// <summary>
/// The deployment's own principal (gh#436) for <b>deployment-caused</b> <c>IUserOwned</c> rows — the owner to stamp
/// on spend that belongs to the deployment itself rather than to any operator's trading decisions. Its one sanctioned
/// use today is <b>global AI embed spend</b>: embedding deployment-global news / market snapshots (gh#377) is
/// infrastructure cost, not a per-decision operator cost, so it is kept in its own owner bucket rather than inflating
/// the operator's per-decision spend meter (gh#62, ADR-0008 — "embeddings reported in Grafana, not surfaced to end
/// users").
/// </summary>
/// <remarks>
/// <para>
/// A well-known, <b>non-empty</b> Guid — deliberately distinct from <see cref="System.Guid.Empty"/>, which the data
/// layer reserves as "no user context ⇒ read nothing" (<see cref="ICurrentUser"/> and the tenant query filter). A
/// real row owned by <c>Guid.Empty</c> would be readable by any context with no user, poisoning that fail-closed
/// default-deny sentinel; a distinct owner keeps it literally true. It is backed by <b>no</b> <c>User</c> row — an
/// <c>IUserOwned</c> row's owner column is a query-filter scope, not a foreign key, so nothing needs seeding.
/// </para>
/// <para>
/// <b>Narrowly scoped on purpose.</b> This is not a general "run as admin" principal: a context scoped to
/// <c>new OwnerUser(SystemOwner.Id)</c> reads every row it owns, so widening its use to other entities is a reviewed
/// decision, never a default. An <i>owner-scoped</i> AI call (for example, embedding an operator's own chat query)
/// stamps the real operator — never this sentinel.
/// </para>
/// </remarks>
public static class SystemOwner
{
    /// <summary>The deployment principal's fixed id — a non-empty, non-operator Guid, pinned forever.</summary>
    public static Guid Id { get; } = new("d0000000-0000-4000-8000-000000000436");
}
