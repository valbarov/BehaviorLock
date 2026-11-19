using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BehaviorLock.Tests;

/// <summary>
/// The loop from production back to the profile: the guard writes unknown accesses to the pending file,
/// a person moves the legal ones to the approved file, and the guard then allows them.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class PendingAccessTests(ProfileFixture fixture) : IDisposable
{
    // The debug switch stands for a legal path that no test covers. It reads the Users table.
    private const string UncoveredRequest = "/orders/1?debug=true";
    private const string UncoveredEndpoint = "GET /orders/{id:int}";

    private readonly string _directory = Directory.CreateTempSubdirectory("behaviorlock-").FullName;

    private string PendingPath => Path.Combine(_directory, "behavior.lock.pending.json");

    private string ApprovedPath => Path.Combine(_directory, "behavior.lock.approved.json");

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Theory]
    [InlineData(GuardMode.Audit, HttpStatusCode.OK)]
    [InlineData(GuardMode.Enforce, HttpStatusCode.Forbidden)]
    public async Task An_unknown_access_is_written_to_the_pending_file(GuardMode mode, HttpStatusCode expected)
    {
        Assert.Equal(expected, await Send(mode, UncoveredRequest));

        var access = Assert.Single(ReadFile(PendingPath)[UncoveredEndpoint]);
        Assert.StartsWith("sql: SELECT", access);
        Assert.Contains("FROM \"Users\"", access);
    }

    [Fact]
    public async Task The_same_access_is_written_once()
    {
        await Send(GuardMode.Audit, UncoveredRequest, UncoveredRequest, "/orders/2?debug=true");

        Assert.Single(Assert.Single(ReadFile(PendingPath)).Value);
    }

    [Fact]
    public async Task A_legal_request_writes_no_pending_file()
    {
        await Send(GuardMode.Audit, "/orders/1");

        Assert.False(File.Exists(PendingPath));
    }

    [Fact]
    public async Task The_recorded_profile_is_not_changed()
    {
        var before = await File.ReadAllTextAsync(fixture.ProfilePath);

        await Send(GuardMode.Audit, UncoveredRequest);

        Assert.Equal(before, await File.ReadAllTextAsync(fixture.ProfilePath));
    }

    [Fact]
    public async Task An_approved_access_passes_in_enforce_mode()
    {
        // 1. Production reports the access.
        await Send(GuardMode.Audit, UncoveredRequest);

        // 2. A person reviews the pending file and accepts its entries.
        File.Move(PendingPath, ApprovedPath);

        // 3. The guard allows the access, and still blocks what nobody approved.
        await using var api = Create(GuardMode.Enforce);
        var client = api.CreateClient();

        using var approved = await client.GetAsync(UncoveredRequest);
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
        Assert.Empty(api.Violations.Items);

        using var attack = await Attacks.All[Attacks.UnionRead].Send(client);
        Assert.Equal(HttpStatusCode.Forbidden, attack.StatusCode);
    }

    [Fact]
    public async Task Pending_entries_survive_a_restart()
    {
        await Send(GuardMode.Audit, UncoveredRequest);
        await Send(GuardMode.Audit, "/products?sort=Id");

        Assert.Equal([UncoveredEndpoint, "GET /products"], ReadFile(PendingPath).Keys);
    }

    [Fact]
    public async Task An_approved_entry_leaves_the_pending_file()
    {
        await Send(GuardMode.Audit, UncoveredRequest, "/products?sort=Id");
        WriteFile(ApprovedPath, new() { [UncoveredEndpoint] = ReadFile(PendingPath)[UncoveredEndpoint] });

        await Send(GuardMode.Audit, "/health");

        Assert.Equal(["GET /products"], ReadFile(PendingPath).Keys);
    }

    [Fact]
    public async Task A_missing_approved_file_does_not_stop_the_app()
    {
        Assert.Equal(HttpStatusCode.OK, await Send(GuardMode.Enforce, "/orders/1"));
    }

    [Fact]
    public async Task Learn_mode_does_not_record_approved_accesses()
    {
        var recorded = Path.Combine(_directory, "behavior.lock.json");
        WriteFile(ApprovedPath, new() { ["GET /health"] = ["sql: SELECT * FROM Approved"] });

        await using (var api = new GuardedApi(GuardMode.Learn, recorded) { ApprovedPath = ApprovedPath })
        {
            using var response = await api.CreateClient().GetAsync("/health");
        }

        Assert.Empty(ReadFile(recorded)["GET /health"]);
    }

    [Fact]
    public void The_pending_file_stops_growing_at_the_limit()
    {
        var options = Options.Create(new BehaviorLockOptions { PendingPath = PendingPath, PendingLimit = 2 });
        var pending = new PendingAccesses(options, new ContentRoot(_directory), new EndpointProfile(), NullLogger<PendingAccesses>.Instance);

        // Each attack attempt can have a new shape.
        for (var i = 0; i < 5; i++)
        {
            pending.Report(new GuardViolation("GET /products", ResourceAccess.Sql($"SELECT * FROM T{i}"), Blocked: true));
        }

        pending.Write();

        Assert.Equal(2, ReadFile(PendingPath)["GET /products"].Length);
    }

    [Fact]
    public void No_pending_path_means_no_file()
    {
        var options = Options.Create(new BehaviorLockOptions());
        var pending = new PendingAccesses(options, new ContentRoot(_directory), new EndpointProfile(), NullLogger<PendingAccesses>.Instance);

        pending.Report(new GuardViolation("GET /products", ResourceAccess.Sql("SELECT * FROM Users"), Blocked: false));
        pending.Write();

        Assert.Null(pending.Location);
        Assert.Empty(Directory.GetFiles(_directory));
    }

    /// <summary>Starts the API, sends the requests and stops the API. The stop writes the pending file.</summary>
    private async Task<HttpStatusCode> Send(GuardMode mode, params string[] urls)
    {
        await using var api = Create(mode);
        var client = api.CreateClient();
        var status = HttpStatusCode.OK;

        foreach (var url in urls)
        {
            using var response = await client.GetAsync(url);
            status = response.StatusCode;
        }

        return status;
    }

    private GuardedApi Create(GuardMode mode) =>
        new(mode, fixture.ProfilePath) { PendingPath = PendingPath, ApprovedPath = ApprovedPath };

    private static Dictionary<string, string[]> ReadFile(string path) =>
        JsonSerializer.Deserialize<Dictionary<string, string[]>>(File.ReadAllText(path))!;

    private static void WriteFile(string path, Dictionary<string, string[]> content) =>
        File.WriteAllText(path, JsonSerializer.Serialize(content));
}
