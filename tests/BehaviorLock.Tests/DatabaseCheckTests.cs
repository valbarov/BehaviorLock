using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SampleApi;

namespace BehaviorLock.Tests;

/// <summary>
/// Each database provider writes its own SQL. A profile that was recorded with one provider matches nothing
/// on another one, so the profile names its provider and the guard compares it with the provider of the app.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class DatabaseCheckTests(ProfileFixture fixture) : IDisposable
{
    private const string DatabaseKey = "$database";
    private const string Sqlite = "Microsoft.EntityFrameworkCore.Sqlite";
    private const string Postgres = "Npgsql.EntityFrameworkCore.PostgreSQL";

    private readonly string _directory = Directory.CreateTempSubdirectory("behaviorlock-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void The_recording_writes_the_database_into_the_profile()
    {
        var database = Assert.Single(ReadFile(fixture.ProfilePath)[DatabaseKey]);

        Assert.StartsWith(Sqlite + " ", database);
    }

    [Fact]
    public void Enforce_mode_does_not_start_with_a_profile_of_another_provider()
    {
        using var api = new GuardedApi(GuardMode.Enforce, ProfileRecordedWith($"{Postgres} 11.0.0"));

        var exception = Assert.Throws<InvalidOperationException>(() => api.CreateClient());

        Assert.Contains(Postgres, exception.Message);
        Assert.Contains(Sqlite, exception.Message);
    }

    [Fact]
    public async Task Audit_mode_starts_with_a_profile_of_another_provider()
    {
        Assert.Equal(HttpStatusCode.OK, await GetOrder(GuardMode.Audit, ProfileRecordedWith($"{Postgres} 11.0.0")));
    }

    [Fact]
    public async Task Enforce_mode_starts_with_another_version_of_the_same_provider()
    {
        Assert.Equal(HttpStatusCode.OK, await GetOrder(GuardMode.Enforce, ProfileRecordedWith($"{Sqlite} 1.0.0")));
    }

    [Fact]
    public async Task Enforce_mode_starts_with_a_profile_that_names_no_database()
    {
        Assert.Equal(HttpStatusCode.OK, await GetOrder(GuardMode.Enforce, ProfileRecordedWith(null)));
    }

    [Theory]
    [InlineData(new string[0], "A 1", DatabaseMatch.NotRecorded)]
    [InlineData(new[] { "A 1" }, "A 1", DatabaseMatch.Same)]
    [InlineData(new[] { "A 1", "B 1" }, "B 1", DatabaseMatch.Same)]
    [InlineData(new[] { "A 1" }, "A 2", DatabaseMatch.OtherVersion)]
    [InlineData(new[] { "A" }, "A 2", DatabaseMatch.OtherVersion)]
    [InlineData(new[] { "A 1" }, "B 1", DatabaseMatch.OtherProvider)]
    [InlineData(new[] { "A 1" }, "AB 1", DatabaseMatch.OtherProvider)]
    public void A_database_is_compared_by_provider_and_then_by_version(string[] recorded, string database, DatabaseMatch expected)
    {
        Assert.Equal(expected, DatabaseCheck.Compare(recorded, database));
    }

    [Fact]
    public void Learn_mode_records_the_database_of_a_context()
    {
        var profile = new EndpointProfile();
        using var db = CreateContext();

        Check(GuardMode.Learn, profile).Observe(db);

        Assert.StartsWith(Sqlite + " ", Assert.Single(profile.Databases));
    }

    [Fact]
    public void A_context_with_another_provider_is_found_at_its_first_command()
    {
        // The startup check only sees the contexts that are registered with AddDbContext.
        var profile = new EndpointProfile();
        profile.AddDatabase($"{Postgres} 11.0.0");
        var check = Check(GuardMode.Enforce, profile);
        using var db = CreateContext();

        var result = check.Observe(db);

        Assert.Equal(DatabaseMatch.OtherProvider, result.Match);
        Assert.StartsWith(Sqlite, result.Database);
        Assert.Contains(Postgres, check.Explain(result));
    }

    [Fact]
    public async Task The_normalizer_gets_the_dialect_of_the_database()
    {
        await using var api = fixture.Create(GuardMode.Audit);

        // SQLite does not accept '#', so the query fails. The guard reports the access before that.
        using var response = await api.CreateClient().GetAsync("/products?sort=" + Uri.EscapeDataString("Name #1"));

        // An unknown database would keep the command as it is, with the 1. SQLite has standard rules.
        var violation = Assert.Single(api.Violations.Items);
        Assert.Equal("SELECT * FROM Products ORDER BY Name #?", violation.Access.Key);
    }

    [Fact]
    public void The_store_keeps_the_database_with_the_profile()
    {
        var store = new JsonFileProfileStore(
            Options.Create(new BehaviorLockOptions { ProfilePath = "behavior.lock.json" }), new ContentRoot(_directory));
        var profile = new EndpointProfile();
        profile.AddEndpoint("GET /health");
        profile.AddDatabase($"{Sqlite} 11.0.0");

        store.Save(profile);
        var loaded = store.Load();

        Assert.Equal([$"{Sqlite} 11.0.0"], loaded.Databases);
        Assert.Equal(1, loaded.EndpointCount);
        Assert.False(loaded.ContainsEndpoint(DatabaseKey));
    }

    private static DatabaseCheck Check(GuardMode mode, EndpointProfile profile) =>
        new(profile, Options.Create(new BehaviorLockOptions { Mode = mode }), NullLogger<DatabaseCheck>.Instance);

    private static ShopDb CreateContext() =>
        new(new DbContextOptionsBuilder<ShopDb>().UseSqlite("Data Source=:memory:").Options);

    private static async Task<HttpStatusCode> GetOrder(GuardMode mode, string profilePath)
    {
        await using var api = new GuardedApi(mode, profilePath);
        using var response = await api.CreateClient().GetAsync("/orders/1");
        return response.StatusCode;
    }

    /// <summary>Copies the recorded profile and changes only the database that it names. Null removes the name.</summary>
    private string ProfileRecordedWith(string? database)
    {
        var content = ReadFile(fixture.ProfilePath);
        content.Remove(DatabaseKey);
        if (database is not null)
        {
            content[DatabaseKey] = [database];
        }

        var path = Path.Combine(_directory, "behavior.lock.json");
        File.WriteAllText(path, JsonSerializer.Serialize(content));
        return path;
    }

    private static Dictionary<string, string[]> ReadFile(string path) =>
        JsonSerializer.Deserialize<Dictionary<string, string[]>>(File.ReadAllText(path))!;
}
