namespace MarqSpec.TradingCopilot.IntegrationTests.TestHost;

/// <summary>
/// A per-class fixture for the smoke suite that holds an in-process API host <b>lazily</b> (gh#152): unlike a
/// plain <c>IClassFixture&lt;StubbedVenuePostgresFactory&gt;</c>, it starts <b>no</b> PostgreSQL container until a
/// test actually asks for the local host. When the smoke suite targets a <i>deployed</i> environment
/// (<c>SMOKE_TARGET_BASE_URL</c>) the local host is never requested, so no container — and no Docker — is
/// touched. A deploy-verification run should need nothing but network access and credentials.
/// </summary>
/// <remarks>
/// The underlying <see cref="StubbedVenuePostgresFactory"/> is created and started <b>once</b> per test class
/// (shared across the class's local-mode tests) and disposed with the fixture. Creation is guarded so the lazy
/// init is safe even if the runner ever parallelises within a class.
/// </remarks>
public sealed class LazySmokeHostFixture : IAsyncLifetime
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private StubbedVenuePostgresFactory? _factory;

    /// <summary>
    /// Deliberately does nothing: the whole point is that constructing the fixture starts no container. The host
    /// is stood up only when <see cref="GetFactoryAsync"/> is first called (i.e. by a local-mode test).
    /// </summary>
    /// <returns>A completed task.</returns>
    public Task InitializeAsync() => Task.CompletedTask;

    /// <summary>
    /// Returns the in-process host, creating and starting it (and its throwaway PostgreSQL container) on first
    /// call and reusing it thereafter. Only local-mode tests reach this; the remote path never forces it.
    /// </summary>
    /// <returns>The started factory.</returns>
    public async Task<StubbedVenuePostgresFactory> GetFactoryAsync()
    {
        if (_factory is not null)
        {
            return _factory;
        }

        await _gate.WaitAsync();
        try
        {
            if (_factory is null)
            {
                StubbedVenuePostgresFactory factory = new();
                await factory.InitializeAsync(); // starts the container; the host builds lazily against it
                _factory = factory;
            }
        }
        finally
        {
            _gate.Release();
        }

        return _factory;
    }

    /// <summary>Disposes the host and its container — but only if a local-mode test ever created one.</summary>
    /// <returns>A task that completes when the host and container are gone.</returns>
    public async Task DisposeAsync()
    {
        if (_factory is not null)
        {
            await ((IAsyncLifetime)_factory).DisposeAsync();
        }

        _gate.Dispose();
    }
}
