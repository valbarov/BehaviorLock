using System.Net.Http.Json;
using SampleApi;

namespace BehaviorLock.Tests;

/// <summary>
/// The legal requests of the sample API: one entry per legal path.
/// They are the source of the profile, and they must still pass in Enforce mode.
/// </summary>
public static class LegalRequests
{
    public static readonly IReadOnlyDictionary<string, Func<HttpClient, Task<HttpResponseMessage>>> All =
        new Dictionary<string, Func<HttpClient, Task<HttpResponseMessage>>>
        {
            ["health"] = client => client.GetAsync("/health"),

            ["get product"] = client => client.GetAsync("/products/1"),
            ["create product"] = client => client.PostAsJsonAsync("/products", new { name = "Tablet", price = 300 }),
            ["search products"] = client => client.GetAsync("/products/search?name=Lap"),

            // Two legal sort orders give two SQL shapes, so each one needs its own request.
            ["list products by price"] = client => client.GetAsync("/products?sort=Price"),
            ["list products by name"] = client => client.GetAsync("/products?sort=Name"),

            ["login"] = client => client.PostAsJsonAsync("/login", new { email = "alice@example.com", password = "correct-horse" }),
            ["rename user"] = client => client.PatchAsJsonAsync("/users/1", new Dictionary<string, object> { ["DisplayName"] = "Alice B" }),

            ["get order"] = client => client.GetAsync("/orders/1"),
            ["delete order"] = client => client.DeleteAsync("/orders/2"),
            ["place order"] = client => client.PostAsJsonAsync("/orders", new { userId = 1, total = 120 }),

            ["preview webhook"] = client => client.GetAsync("/preview?url=https://hooks.example.com/ping"),

            // The caller is part of the profile: only an admin calls this endpoint.
            ["admin lists orders"] = client => GetAs(client, ApiKeyAuthentication.AdminKey, "/admin/orders"),
        };

    /// <summary>Sends a GET request as the caller that owns this API key.</summary>
    public static Task<HttpResponseMessage> GetAs(HttpClient client, string apiKey, string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add(ApiKeyAuthentication.Header, apiKey);
        return client.SendAsync(request);
    }
}
