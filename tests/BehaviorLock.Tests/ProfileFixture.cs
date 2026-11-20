using System.Reflection;
using System.Runtime.Versioning;

namespace BehaviorLock.Tests;

/// <summary>
/// Records the profile once per test run: runs every legal request in Learn mode and writes
/// samples/SampleApi/behavior.lock.json. Commit that file; its diff shows behavior changes.
/// <para>
/// The tests run once per target framework, and only the run of <see cref="CommittedFramework"/> writes that
/// file. Each other run writes its own file next to the test assembly. A profile holds the version of the
/// database provider and the SQL of that version, and two runs at the same time must not write one file.
/// </para>
/// </summary>
public sealed class ProfileFixture : IAsyncLifetime
{
    /// <summary>The target framework whose recording is the committed profile of the sample API.</summary>
    private const string CommittedFramework = ".NETCoreApp,Version=v11.0";

    // The target framework of this test assembly. AppContext.TargetFrameworkName is the one of the test host.
    private static readonly string? Framework =
        typeof(ProfileFixture).Assembly.GetCustomAttribute<TargetFrameworkAttribute>()?.FrameworkName;

    public string ProfilePath { get; } = Framework == CommittedFramework
        ? Path.Combine(FindRepositoryRoot(), "samples", "SampleApi", "behavior.lock.json")
        : Path.Combine(AppContext.BaseDirectory, "recorded-profile", "behavior.lock.json");

    public async Task InitializeAsync()
    {
        // Start clean, so that accesses the code no longer makes leave the profile.
        Directory.CreateDirectory(Path.GetDirectoryName(ProfilePath)!);
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
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "BehaviorLock.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("BehaviorLock.slnx was not found.");
    }
}

/// <summary>All API test classes share one recorded profile.</summary>
[CollectionDefinition(Name)]
public sealed class ApiCollection : ICollectionFixture<ProfileFixture>
{
    public const string Name = "Sample API";
}
