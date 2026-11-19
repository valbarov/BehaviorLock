using System.Net;
using Microsoft.Extensions.Options;

namespace BehaviorLock.Tests;

/// <summary>
/// A missing profile must show at startup, not as a 403 for every request.
/// The profile file must be found in the content root, whatever the current directory is.
/// </summary>
public sealed class ProfileStartupTests : IDisposable
{
    private readonly string _contentRoot = Directory.CreateTempSubdirectory("behaviorlock-").FullName;

    private string MissingProfile => Path.Combine(_contentRoot, "missing.lock.json");

    public void Dispose() => Directory.Delete(_contentRoot, recursive: true);

    [Fact]
    public void A_relative_path_is_resolved_against_the_content_root()
    {
        var store = Store(Path.Combine("profiles", "behavior.lock.json"));

        Assert.Equal(Path.Combine(_contentRoot, "profiles", "behavior.lock.json"), store.Location);
    }

    [Fact]
    public void An_absolute_path_is_kept()
    {
        Assert.Equal(MissingProfile, Store(MissingProfile).Location);
    }

    [Fact]
    public void A_saved_profile_is_loaded_again_from_the_content_root()
    {
        var access = ResourceAccess.Sql("SELECT * FROM Products");
        var profile = new EndpointProfile();
        profile.Allow("GET /products", access);

        Store("behavior.lock.json").Save(profile);

        Assert.True(File.Exists(Path.Combine(_contentRoot, "behavior.lock.json")));
        Assert.True(Store("behavior.lock.json").Load().IsAllowed("GET /products", access));
    }

    [Fact]
    public void A_missing_file_gives_an_empty_profile()
    {
        Assert.Equal(0, Store(MissingProfile).Load().EndpointCount);
    }

    [Fact]
    public void Enforce_mode_with_a_missing_profile_fails_at_startup()
    {
        using var api = new GuardedApi(GuardMode.Enforce, MissingProfile);

        var exception = Assert.Throws<InvalidOperationException>(() => api.CreateClient());

        Assert.Contains(MissingProfile, exception.Message);
    }

    [Fact]
    public async Task Enforce_mode_with_an_empty_profile_file_fails_at_startup()
    {
        var path = Path.Combine(_contentRoot, "empty.lock.json");
        await File.WriteAllTextAsync(path, "{}");
        using var api = new GuardedApi(GuardMode.Enforce, path);

        Assert.Throws<InvalidOperationException>(() => api.CreateClient());
    }

    [Theory]
    [InlineData(GuardMode.Off)]
    [InlineData(GuardMode.Learn)]
    [InlineData(GuardMode.Audit)]
    public async Task Other_modes_start_without_a_profile(GuardMode mode)
    {
        await using var api = new GuardedApi(mode, MissingProfile);

        using var response = await api.CreateClient().GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private JsonFileProfileStore Store(string profilePath) =>
        new(Options.Create(new BehaviorLockOptions { ProfilePath = profilePath }), new ContentRoot(_contentRoot));
}
