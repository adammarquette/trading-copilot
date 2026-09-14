using Xunit;

namespace MarqSpec.TradingCopilot.IntegrationTests.TestHost.Staging;

/// <summary>
/// Serialises the order-placing staging suites onto the single reserved practice account (QA contract §1:
/// <i>"test runs that place orders use dedicated practice accounts reserved for CI, with serialization as default
/// to prevent execution collisions"</i>). Suites that transmit to the venue (gh#269, gh#293) join this collection
/// so no two of them place against the account at the same time.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class StagingExecutionCollection
{
    /// <summary>The collection name execution suites reference via <c>[Collection(StagingExecutionCollection.Name)]</c>.</summary>
    public const string Name = "Staging execution (serialized)";
}
