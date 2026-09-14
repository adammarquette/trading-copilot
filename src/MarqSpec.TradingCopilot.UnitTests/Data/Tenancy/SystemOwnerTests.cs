using MarqSpec.TradingCopilot.Data.Tenancy;

namespace MarqSpec.TradingCopilot.UnitTests.Data.Tenancy;

/// <summary>
/// The deployment sentinel owner (gh#436): the non-operator principal that owns deployment-caused <c>IUserOwned</c>
/// rows — currently global AI embed spend, whose cost belongs to the deployment, not to any operator's trading
/// decisions. The load-bearing invariant is that it is <b>never</b> <see cref="System.Guid.Empty"/>: the data layer
/// reserves <c>Guid.Empty</c> as "no user context ⇒ read nothing", so a real row owned by it would poison that
/// default-deny sentinel (an anonymous-context reader would then match it).
/// </summary>
public class SystemOwnerTests
{
    [Fact]
    public void Id_IsNeverTheDenySentinel_SoRealRowsAreNotAnonymouslyReadable()
    {
        // R-20's fail-closed core: Guid.Empty means "no user ⇒ no rows" (ICurrentUser / the tenant filter). Were the
        // system owner Guid.Empty, its rows would be readable by any context with no user — the exact leak the deny
        // sentinel prevents. It MUST be a distinct, non-empty owner. This is the whole safety claim of the sentinel.
        SystemOwner.Id.Should().NotBe(Guid.Empty);
    }

    [Fact]
    public void Id_IsPinned_SoAConsumerStampsTheSameOwnerEveryTime_AndRowsNeverOrphan()
    {
        // A compile-time constant, fixed forever: a future embed consumer (gh#377) stamps this exact value, so a
        // shifting id would orphan every row written under a prior value. Pin it so a change is a deliberate, failing edit.
        SystemOwner.Id.Should().Be(new Guid("d0000000-0000-4000-8000-000000000436"));
    }
}
