using System.Net;

namespace BehaviorLock.Tests;

/// <summary>The mode of one endpoint, set with metadata, against the global mode. See <see cref="ModeTestApi"/>.</summary>
public sealed class EndpointModeTests
{
    [Theory]
    [InlineData(GuardMode.Audit)]
    [InlineData(GuardMode.Enforce)]
    public async Task Endpoint_without_a_mode_uses_the_global_mode(GuardMode global)
    {
        await using var api = await ModeTestApi.StartAsync(global);

        await Send(api, "/default", global == GuardMode.Enforce ? HttpStatusCode.Forbidden : HttpStatusCode.OK);

        AssertReported(api, "GET /default", blocked: global == GuardMode.Enforce);
    }

    [Fact]
    public async Task Endpoint_marked_audit_is_reported_but_not_blocked_under_global_enforce()
    {
        await using var api = await ModeTestApi.StartAsync(GuardMode.Enforce);

        await Send(api, "/audit", HttpStatusCode.OK);

        AssertReported(api, "GET /audit", blocked: false);
    }

    [Fact]
    public async Task Endpoint_marked_enforce_is_blocked_under_global_audit()
    {
        await using var api = await ModeTestApi.StartAsync(GuardMode.Audit);

        using var response = await api.Client.GetAsync("/enforce");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsStringAsync());
        AssertReported(api, "GET /enforce", blocked: true);
    }

    [Theory]
    [InlineData(GuardMode.Audit)]
    [InlineData(GuardMode.Enforce)]
    public async Task Endpoint_marked_off_is_not_checked(GuardMode global)
    {
        await using var api = await ModeTestApi.StartAsync(global);

        var active = await Send(api, "/off", HttpStatusCode.OK);

        Assert.Equal("false", active);
        Assert.Empty(api.Violations.Items);
    }

    [Theory]
    [InlineData("/enforce")]
    [InlineData("/attribute")]
    [InlineData("/group/plain")]
    [InlineData("/mvc/controller")]
    public async Task Global_off_beats_an_endpoint_marked_enforce(string path)
    {
        await using var api = await ModeTestApi.StartAsync(GuardMode.Off);

        var active = await Send(api, path, HttpStatusCode.OK);

        Assert.Equal("false", active);
        Assert.Empty(api.Violations.Items);
    }

    [Fact]
    public async Task Global_learn_records_every_endpoint_also_those_with_their_own_mode()
    {
        await using var api = await ModeTestApi.StartAsync(GuardMode.Learn);

        foreach (var path in new[] { "/off", "/audit", "/enforce" })
        {
            var active = await Send(api, path, HttpStatusCode.OK);
            Assert.Equal("true", active);
            Assert.True(api.LoadedProfile.IsAllowed($"GET {path}", ModeTestApi.Unknown), path);
        }

        Assert.Empty(api.Violations.Items);
    }

    [Fact]
    public async Task Mode_of_a_route_group_applies_to_its_endpoints()
    {
        await using var api = await ModeTestApi.StartAsync(GuardMode.Audit);

        await Send(api, "/group/plain", HttpStatusCode.Forbidden);

        AssertReported(api, "GET /group/plain", blocked: true);
    }

    [Fact]
    public async Task Mode_of_an_endpoint_beats_the_mode_of_its_route_group()
    {
        await using var api = await ModeTestApi.StartAsync(GuardMode.Audit);

        await Send(api, "/group/audit", HttpStatusCode.OK);

        AssertReported(api, "GET /group/audit", blocked: false);
    }

    [Fact]
    public async Task Attribute_on_a_minimal_api_handler_sets_the_mode()
    {
        await using var api = await ModeTestApi.StartAsync(GuardMode.Audit);

        await Send(api, "/attribute", HttpStatusCode.Forbidden);

        AssertReported(api, "GET /attribute", blocked: true);
    }

    [Fact]
    public async Task Attribute_on_a_controller_sets_the_mode_of_its_actions()
    {
        await using var api = await ModeTestApi.StartAsync(GuardMode.Audit);

        await Send(api, "/mvc/controller", HttpStatusCode.Forbidden);

        AssertReported(api, "GET mvc/controller", blocked: true);
    }

    [Fact]
    public async Task Attribute_on_an_action_beats_the_attribute_on_its_controller()
    {
        await using var api = await ModeTestApi.StartAsync(GuardMode.Audit);

        var active = await Send(api, "/mvc/action", HttpStatusCode.OK);

        Assert.Equal("false", active);
        Assert.Empty(api.Violations.Items);
    }

    [Fact]
    public async Task Endpoint_marked_enforce_stops_the_start_when_the_profile_is_missing()
    {
        // The global mode Audit starts without a profile. An endpoint marked Enforce would block every access.
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ModeTestApi.StartAsync(GuardMode.Audit, withProfile: false));

        Assert.Contains("has the mode Enforce", exception.Message);
        Assert.Contains("behavior.lock.json", exception.Message);
    }

    /// <summary>Sends a GET request, checks the status code and returns the body.</summary>
    private static async Task<string> Send(ModeTestApi api, string path, HttpStatusCode expected)
    {
        using var response = await api.Client.GetAsync(path);

        Assert.Equal(expected, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }

    private static void AssertReported(ModeTestApi api, string endpoint, bool blocked) =>
        Assert.Equal([new GuardViolation(endpoint, ModeTestApi.Unknown, blocked)], api.Violations.Items);
}
