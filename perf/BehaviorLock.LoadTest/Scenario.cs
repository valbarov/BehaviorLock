using System.Net.Http.Json;

namespace BehaviorLock.LoadTest;

/// <summary>One kind of request to repeat, and the status code it must get.</summary>
public sealed record Scenario(string Name, Func<HttpRequestMessage> CreateRequest, Func<Variant, int> ExpectedStatus)
{
    private static int _counter;

    private static readonly string Injection =
        "/products/search?name=" + Uri.EscapeDataString("x' UNION SELECT Id, Email, 0 FROM Users --");

    /// <summary>An upper limit for the parallel connections of this scenario, or null for no limit.</summary>
    public int? MaxConnections { get; init; }

    public static readonly IReadOnlyList<Scenario> All =
    [
        new("GET /health (no resource)", () => Get("/health"), _ => 200),
        new("GET /products/1 (1 EF query)", () => Get("/products/1"), _ => 200),
        new("GET /products/search (raw SQL with a literal)", () => Get("/products/search?name=Lap"), _ => 200),

        // A new name each time, so that EF Core really sends an UPDATE.
        // SQLite allows one writer at a time, so parallel writes would only measure its lock.
        new("PATCH /users/1 (EF query + UPDATE, 1 connection)", () => new HttpRequestMessage(HttpMethod.Patch, "/users/1")
        {
            Content = JsonContent.Create(new Dictionary<string, object>
            {
                ["DisplayName"] = "Name " + Interlocked.Increment(ref _counter),
            }),
        }, _ => 204) { MaxConnections = 1 },

        new("GET /preview (1 outbound HTTP call)", () => Get("/preview?url=https://hooks.example.com/ping"), _ => 200),

        // The cost of a violation: reported in Audit mode, blocked with a 403 in Enforce mode.
        new("GET /products/search with injection", () => Get(Injection), variant => variant == Variant.Enforce ? 403 : 200),
    ];

    private static HttpRequestMessage Get(string url) => new(HttpMethod.Get, url);
}
