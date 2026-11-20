using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SampleApi;

namespace BehaviorLock.Tests;

/// <summary>
/// The guard records how many rows each statement returns, and it stops a command that returns more.
/// The search of the sample API returns one product in the recording, so its limit is 10.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class RowLimitTests(ProfileFixture fixture) : IDisposable
{
    private const string Endpoint = "GET /products/search";
    private const string LegalSearch = "/products/search?name=Lap";
    private const string Wildcard = "/products/search?name=%25";

    // What the wildcard needs, in the form of the pending file.
    private static readonly ResourceAccess MoreRows = ResourceAccess.Rows("SELECT * FROM Products WHERE Name LIKE ?", 100);
    private static readonly ResourceAccess LargerResponse = ResourceAccess.ResponseSize(1000);

    private readonly string _directory = Directory.CreateTempSubdirectory("behaviorlock-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public async Task A_legal_search_passes_in_a_shop_with_many_products()
    {
        await using var api = await CreateLargeShop(GuardMode.Enforce);

        using var response = await api.CreateClient().GetAsync(LegalSearch);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(api.Violations.Items);
    }

    [Fact]
    public async Task A_command_that_returns_more_rows_is_blocked_in_enforce_mode()
    {
        await using var api = await CreateLargeShop(GuardMode.Enforce);

        using var response = await api.CreateClient().GetAsync(Wildcard);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsStringAsync());
        Assert.Equal([new GuardViolation(Endpoint, MoreRows, Blocked: true)], api.Violations.Items);
    }

    [Fact]
    public async Task The_database_still_works_after_a_blocked_command()
    {
        await using var api = await CreateLargeShop(GuardMode.Enforce);
        var client = api.CreateClient();

        // The guard stops the command when its reader closes. The reader and the connection must still be released.
        for (var i = 0; i < 3; i++)
        {
            using var blocked = await client.GetAsync(Wildcard);
            Assert.Equal(HttpStatusCode.Forbidden, blocked.StatusCode);
        }

        using var legal = await client.GetAsync(LegalSearch);
        using var created = await LegalRequests.All["create product"](client);

        Assert.Equal(HttpStatusCode.OK, legal.StatusCode);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
    }

    [Fact]
    public async Task A_command_that_returns_more_rows_is_reported_but_not_blocked_in_audit_mode()
    {
        await using var api = await CreateLargeShop(GuardMode.Audit);

        var body = await api.CreateClient().GetStringAsync(Wildcard);

        // The answer with all products is also larger than the recorded size.
        Assert.Contains("Cable 20", body);
        Assert.Equal(
            [new GuardViolation(Endpoint, MoreRows, Blocked: false), new GuardViolation(Endpoint, LargerResponse, Blocked: false)],
            api.Violations.Items);
    }

    [Fact]
    public async Task Approved_limits_let_the_larger_result_pass()
    {
        // A person decides that a search can return up to 100 rows, and copies the entries of the pending file.
        var approved = Path.Combine(_directory, "behavior.lock.approved.json");
        File.WriteAllText(approved, JsonSerializer.Serialize(new Dictionary<string, string[]>
        {
            [Endpoint] = [MoreRows.ToString(), LargerResponse.ToString()],
        }));

        await using var api = await CreateLargeShop(GuardMode.Enforce, approved);

        var body = await api.CreateClient().GetStringAsync(Wildcard);

        Assert.Contains("Cable 20", body);
        Assert.Empty(api.Violations.Items);
    }

    [Fact]
    public async Task The_option_turns_the_check_off()
    {
        await using var api = await CreateLargeShop(GuardMode.Enforce, configure: options => options.CheckLimits = false);

        using var response = await api.CreateClient().GetAsync(Wildcard);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(api.Violations.Items);
    }

    [Fact]
    public async Task The_rows_of_one_command_belong_to_all_its_statements_together()
    {
        // Its own profile file: Learn mode saves the profile, and that must not change the profile of the sample.
        await using var api = new GuardedApi(GuardMode.Learn, Path.Combine(_directory, "behavior.lock.json"));
        using var scope = api.Services.CreateScope();

        // Stands for an endpoint that sends several statements in one command. One command has one reader.
        scope.ServiceProvider.GetRequiredService<IGuardContextAccessor>().Current = new GuardContext("POST /import");
        var db = scope.ServiceProvider.GetRequiredService<ShopDb>();
        await db.Database.SqlQueryRaw<int>("SELECT 1 AS Value; SELECT 2 AS Value").ToListAsync();
        await db.Database.SqlQueryRaw<int>("SELECT 1 AS Value; SELECT Id AS Value FROM Products").ToListAsync();

        // Equal statements give the limit of that one shape, so the number of statements is not part of the key.
        var recorded = api.Services.GetRequiredService<EndpointProfile>().ToDictionary()["POST /import"];
        Assert.Equal(
            [
                "rows: up to 10 from SELECT ? AS Value",
                "rows: up to 10 from SELECT ? AS Value; SELECT Id AS Value FROM Products",
            ],
            recorded.Where(entry => entry.StartsWith("rows:")));
    }

    /// <summary>Starts the sample API with 20 more products. A search for every product then returns 22 rows.</summary>
    private async Task<GuardedApi> CreateLargeShop(
        GuardMode mode, string? approvedPath = null, Action<BehaviorLockOptions>? configure = null)
    {
        var api = new GuardedApi(mode, fixture.ProfilePath) { ApprovedPath = approvedPath, Configure = configure };

        // This runs outside a request, so the guard does not check it.
        await api.QueryDb(db =>
        {
            db.Products.AddRange(Cables(20));
            return db.SaveChangesAsync();
        });

        return api;
    }

    private static IEnumerable<Product> Cables(int count) =>
        Enumerable.Range(1, count).Select(i => new Product { Name = $"Cable {i}", Price = 5 });
}
