using System.Net.Http.Json;
using SampleApi;

namespace BehaviorLock.Tests;

/// <summary>A request that makes an endpoint do something its profile does not allow.</summary>
public sealed record Attack(string Endpoint, Func<HttpClient, Task<HttpResponseMessage>> Send);

/// <summary>One attack per weakness of the sample API. The guard must stop all of them.</summary>
public static class Attacks
{
    public const string UnionRead = "sql injection: UNION reads another table";
    public const string StackedDelete = "sql injection: stacked DELETE statement";
    public const string LoginBypass = "sql injection: login without the password";
    public const string BlindOrderBy = "sql injection: blind, through ORDER BY";
    public const string MassAssignment = "mass assignment: client sets IsAdmin";
    public const string DeleteAllBug = "bug: DELETE /orders/0 removes every order";
    public const string DebugSwitch = "forgotten debug switch: dumps the Users table";
    public const string SsrfMetadata = "ssrf: cloud metadata address";
    public const string SsrfOtherPort = "ssrf: other port on a legal host";
    public const string Exfiltration = "compromised dependency: copies an order to a foreign host";
    public const string WildcardDump = "sql injection that keeps the shape: a wildcard returns every product";
    public const string UserAsAdmin = "broken authorization: a normal user calls an admin endpoint";

    public const string MetadataUrl = "http://169.254.169.254/latest/meta-data/";
    public const string ExfiltrationUrl = "https://stats.evil.example/collect";

    public static readonly IReadOnlyDictionary<string, Attack> All = new Dictionary<string, Attack>
    {
        [UnionRead] = new("GET /products/search", client =>
            client.GetAsync("/products/search?name=" + Escape("x' UNION SELECT Id, Email, 0 FROM Users --"))),

        [StackedDelete] = new("GET /products/search", client =>
            client.GetAsync("/products/search?name=" + Escape("x'; DELETE FROM Users; --"))),

        [LoginBypass] = new("POST /login", client =>
            client.PostAsJsonAsync("/login", new { email = "alice@example.com' --", password = "wrong" })),

        [BlindOrderBy] = new("GET /products", client => client.GetAsync(BlindOrderByUrl("alice"))),

        [MassAssignment] = new("PATCH /users/{id:int}", client =>
            client.PatchAsJsonAsync("/users/1", new Dictionary<string, object> { ["DisplayName"] = "Al", ["IsAdmin"] = true })),

        [DeleteAllBug] = new("DELETE /orders/{id:int}", client => client.DeleteAsync("/orders/0")),

        [DebugSwitch] = new("GET /orders/{id:int}", client => client.GetAsync("/orders/1?debug=true")),

        [SsrfMetadata] = new("GET /preview", client => client.GetAsync("/preview?url=" + Escape(MetadataUrl))),

        [SsrfOtherPort] = new("GET /preview", client =>
            client.GetAsync("/preview?url=" + Escape("https://hooks.example.com:8443/ping"))),

        [Exfiltration] = new("POST /orders", client =>
            client.PostAsJsonAsync("/orders", new { userId = 1, total = 5000 })),

        [WildcardDump] = new("GET /products/search", async client =>
        {
            // The statement has the legal shape. Only the number of rows shows the attack,
            // so the shop needs more products than a legal search returns.
            for (var i = 1; i <= 10; i++)
            {
                using var created = await client.PostAsJsonAsync("/products", new { name = $"Cable {i}", price = 5 });
                created.EnsureSuccessStatusCode();
            }

            return await client.GetAsync("/products/search?name=" + Escape("%"));
        }),

        [UserAsAdmin] = new("GET /admin/orders", client =>
            LegalRequests.GetAs(client, ApiKeyAuthentication.UserKey, "/admin/orders")),
    };

    /// <summary>
    /// Sorts by name when a user email starts with the prefix, and by price when not.
    /// The order of the result leaks one bit about the Users table.
    /// </summary>
    public static string BlindOrderByUrl(string emailPrefix) => "/products?sort=" + Escape(
        $"CASE WHEN (SELECT COUNT(*) FROM Users WHERE Email LIKE '{emailPrefix}%') > 0 THEN Name ELSE Price END");

    private static string Escape(string value) => Uri.EscapeDataString(value);
}
