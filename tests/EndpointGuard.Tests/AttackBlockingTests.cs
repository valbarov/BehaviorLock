using System.Net;

namespace EndpointGuard.Tests;

/// <summary>Every attack gets the same answer from the guard, whatever the weakness behind it is.</summary>
[Collection(ApiCollection.Name)]
public sealed class AttackBlockingTests(ProfileFixture fixture)
{
    public static IEnumerable<object[]> Names => Attacks.All.Keys.Select(name => new object[] { name });

    [Theory]
    [MemberData(nameof(Names))]
    public async Task Attack_is_blocked_and_reported_in_enforce_mode(string name)
    {
        await using var api = fixture.Create(GuardMode.Enforce);
        var attack = Attacks.All[name];

        using var response = await attack.Send(api.CreateClient());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsStringAsync());
        var violation = Assert.Single(api.Violations.Items);
        Assert.Equal(attack.Endpoint, violation.Endpoint);
        Assert.True(violation.Blocked);
    }

    [Theory]
    [MemberData(nameof(Names))]
    public async Task Attack_is_reported_but_not_blocked_in_audit_mode(string name)
    {
        await using var api = fixture.Create(GuardMode.Audit);

        using var response = await Attacks.All[name].Send(api.CreateClient());

        Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.NotEmpty(api.Violations.Items);
        Assert.All(api.Violations.Items, violation => Assert.False(violation.Blocked));
    }
}
