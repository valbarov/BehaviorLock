namespace EndpointGuard.Tests;

/// <summary>
/// Records the profile once per test run: runs every legal request in Learn mode and writes
/// samples/SampleApi/endpointguard.profile.json. Commit that file; its diff shows behavior changes.
/// </summary>
public sealed class ProfileFixture : IAsyncLifetime
{
    public string ProfilePath { get; } = Path.Combine(FindRepositoryRoot(), "samples", "SampleApi", "endpointguard.profile.json");

    public async Task InitializeAsync()
    {
        // Start clean, so that accesses the code no longer makes leave the profile.
        File.Delete(ProfilePath);

        await using var api = Create(GuardMode.Learn);
        var client = api.CreateClient();
        foreach (var (name, send) in LegalRequests.All)
        {
            using var response = await send(client);
            Assert.True(response.IsSuccessStatusCode, $"Legal request '{name}' failed with {(int)response.StatusCode}.");
        }

        // Disposing the API stops the host, and that saves the profile.
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public GuardedApi Create(GuardMode mode) => new(mode, ProfilePath);

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "EndpointGuard.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("EndpointGuard.slnx was not found.");
    }
}

/// <summary>All API test classes share one recorded profile.</summary>
[CollectionDefinition(Name)]
public sealed class ApiCollection : ICollectionFixture<ProfileFixture>
{
    public const string Name = "Sample API";
}
