using System.Net;
using System.Security.Claims;

namespace BehaviorLock.Tests;

/// <summary>The guard records the kind of caller of each endpoint, and it stops every other kind. See <see cref="TinyApi"/>.</summary>
public sealed class CallerTests
{
    private const string Endpoint = "GET /whoami";
    private const string AdminOnly = """{ "GET /whoami": [ "caller: signed in, role admin" ] }""";

    // An older version recorded this profile: it has no caller.
    private const string WithoutCaller = """{ "GET /whoami": [ "sql: SELECT * FROM Products" ] }""";

    [Theory]
    [InlineData(null, "anonymous")]
    [InlineData("", "signed in, no role")]
    [InlineData("admin", "signed in, role admin")]
    [InlineData("support, admin, support", "signed in, role admin, role support")]
    public async Task Learn_mode_records_the_kind_of_caller(string? roles, string expected)
    {
        await using var api = await TinyApi.StartAsync(GuardMode.Learn, "{}");

        using var response = await api.GetAsync("/whoami", roles);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains($"caller: {expected}", api.Profile.ToDictionary()[Endpoint]);
    }

    [Fact]
    public async Task A_recorded_caller_passes_in_enforce_mode()
    {
        await using var api = await TinyApi.StartAsync(GuardMode.Enforce, AdminOnly);

        using var response = await api.GetAsync("/whoami", "admin");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(api.Violations.Items);
    }

    // The whole list of roles must match. A caller with one more role is another kind of caller.
    [Theory]
    [InlineData(null, "anonymous")]
    [InlineData("", "signed in, no role")]
    [InlineData("support", "signed in, role support")]
    [InlineData("admin, support", "signed in, role admin, role support")]
    public async Task Another_caller_is_blocked_before_the_endpoint_runs(string? roles, string caller)
    {
        await using var api = await TinyApi.StartAsync(GuardMode.Enforce, AdminOnly);

        using var response = await api.GetAsync("/whoami", roles);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsStringAsync());
        Assert.Equal(0, api.Calls);
        Assert.Equal([new GuardViolation(Endpoint, new ResourceAccess("caller", caller), Blocked: true)], api.Violations.Items);
    }

    [Fact]
    public async Task Another_caller_is_reported_but_not_blocked_in_audit_mode()
    {
        await using var api = await TinyApi.StartAsync(GuardMode.Audit, AdminOnly);

        using var response = await api.GetAsync("/whoami");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, api.Calls);
        Assert.Equal([new GuardViolation(Endpoint, new ResourceAccess("caller", "anonymous"), Blocked: false)], api.Violations.Items);
    }

    [Fact]
    public async Task An_endpoint_without_a_recorded_caller_is_not_checked()
    {
        await using var api = await TinyApi.StartAsync(GuardMode.Enforce, WithoutCaller);

        using var response = await api.GetAsync("/whoami", "support");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(api.Violations.Items);
    }

    [Fact]
    public async Task The_option_turns_the_check_off()
    {
        await using var api = await TinyApi.StartAsync(GuardMode.Enforce, AdminOnly, options => options.CheckCallers = false);

        using var response = await api.GetAsync("/whoami");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(api.Violations.Items);
    }

    [Fact]
    public async Task The_option_turns_the_recording_off()
    {
        await using var api = await TinyApi.StartAsync(GuardMode.Learn, "{}", options => options.CheckCallers = false);

        using var response = await api.GetAsync("/whoami", "admin");

        Assert.DoesNotContain(api.Profile.ToDictionary()[Endpoint], entry => entry.StartsWith("caller:"));
    }

    [Fact]
    public void An_identity_that_is_not_signed_in_gives_an_anonymous_caller()
    {
        // No authentication type: the identity carries claims, but nobody proved them.
        var user = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, "admin")]));

        Assert.Equal("caller: anonymous", ResourceAccess.Caller(user).ToString());
        Assert.Equal("caller: anonymous", ResourceAccess.Caller(null).ToString());
    }

    [Fact]
    public void Roles_come_from_the_role_claim_of_each_identity()
    {
        // Token handlers often use a short claim name for roles.
        var token = new ClaimsIdentity([new Claim("roles", "billing"), new Claim("other", "x")], "Bearer", "name", "roles");
        var cookie = new ClaimsIdentity([new Claim(ClaimTypes.Role, "admin")], "Cookie");

        var caller = ResourceAccess.Caller(new ClaimsPrincipal([token, cookie]));

        Assert.Equal("caller: signed in, role admin, role billing", caller.ToString());
    }
}
