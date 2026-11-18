using System.Net;

namespace BehaviorLock.Tests;

/// <summary>Attacks that stay inside the profile. The guard does not see them, and these tests keep that visible.</summary>
[Collection(ApiCollection.Name)]
public sealed class KnownLimitTests(ProfileFixture fixture)
{
    [Fact]
    public async Task Reading_another_users_order_is_not_detected()
    {
        await using var api = fixture.Create(GuardMode.Enforce);

        // Order 2 belongs to another user. The SQL shape is the same as for a legal read (IDOR).
        using var response = await api.CreateClient().GetAsync("/orders/2");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(api.Violations.Items);
    }

    [Fact]
    public async Task Injection_that_keeps_the_shape_is_not_detected()
    {
        await using var api = fixture.Create(GuardMode.Enforce);

        // A LIKE wildcard changes the result, but not the statement shape.
        var body = await api.CreateClient().GetStringAsync("/products/search?name=" + Uri.EscapeDataString("%"));

        Assert.Contains("Laptop", body);
        Assert.Contains("Phone", body);
        Assert.Empty(api.Violations.Items);
    }
}
