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

    [Theory]
    [InlineData("", "orders.read", "signed in, no role, scope orders.read")]
    [InlineData("admin", "orders.write  orders.read", "signed in, role admin, scope orders.read, scope orders.write")]
    [InlineData("admin", null, "signed in, role admin")]
    public async Task Learn_mode_records_the_claims_that_the_option_names(string roles, string? scope, string expected)
    {
        await using var api = await TinyApi.StartAsync(GuardMode.Learn, "{}", options => options.CallerClaims.Add("scope"));

        using var response = await api.GetAsync("/whoami", roles, scope);

        Assert.Contains($"caller: {expected}", api.Profile.ToDictionary()[Endpoint]);
    }

    [Fact]
    public async Task A_claim_is_not_part_of_the_caller_without_the_option()
    {
        await using var api = await TinyApi.StartAsync(GuardMode.Learn, "{}");

        using var response = await api.GetAsync("/whoami", "admin", "orders.read");

        Assert.Contains("caller: signed in, role admin", api.Profile.ToDictionary()[Endpoint]);
    }

    [Theory]
    [InlineData("orders.read", HttpStatusCode.OK)]
    [InlineData("orders.read orders.write", HttpStatusCode.Forbidden)]
    [InlineData("orders.write", HttpStatusCode.Forbidden)]
    [InlineData(null, HttpStatusCode.Forbidden)]
    public async Task A_caller_with_other_claim_values_is_blocked(string? scope, HttpStatusCode expected)
    {
        const string readOnly = """{ "GET /whoami": [ "caller: signed in, no role, scope orders.read" ] }""";
        await using var api = await TinyApi.StartAsync(GuardMode.Enforce, readOnly, options => options.CallerClaims.Add("scope"));

        using var response = await api.GetAsync("/whoami", "", scope);

        Assert.Equal(expected, response.StatusCode);
        Assert.Equal(expected == HttpStatusCode.OK ? 1 : 0, api.Calls);
    }

    [Fact]
    public void Claims_of_several_types_are_sorted_by_type_and_value()
    {
        var identity = new ClaimsIdentity(
            [
                new Claim("tenant", "b"),
                new Claim("scope", "write read"),
                new Claim("scope", "read"),
                new Claim("tenant", "a"),
                new Claim("email", "alice@example.com"),
                new Claim(ClaimTypes.Role, "admin"),
            ],
            "Bearer");

        var caller = ResourceAccess.Caller(new ClaimsPrincipal(identity), ["tenant", "scope"]);

        Assert.Equal("caller: signed in, role admin, scope read, scope write, tenant a, tenant b", caller.ToString());
    }

    [Fact]
    public void Claims_of_an_identity_that_is_not_signed_in_do_not_count()
    {
        var user = new ClaimsPrincipal(new ClaimsIdentity([new Claim("scope", "orders.read")]));

        Assert.Equal("caller: anonymous", ResourceAccess.Caller(user, ["scope"]).ToString());
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
