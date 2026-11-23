using System.Net;
using Microsoft.Extensions.DependencyInjection;

namespace BehaviorLock.Tests;

/// <summary>
/// A change of <c>EnforceLimits</c> in the configuration applies while the app runs. So a limit that blocks
/// legal requests can become a report without a new deploy. See <see cref="TinyApi"/>.
/// </summary>
public sealed class LimitReloadTests
{
    private const string Endpoint = "GET /text";
    private const string Large = "/text?size=500";
    private const string UpTo100 = """{ "GET /text": [ "response: up to 100 bytes" ] }""";

    private static readonly ResourceAccess UpTo1000 = ResourceAccess.ResponseSize(1000);

    [Fact]
    public async Task Change_to_false_lets_a_larger_response_pass_with_a_report()
    {
        await using var api = await TinyApi.StartAsync(GuardMode.Enforce, UpTo100);
        Assert.Equal(HttpStatusCode.Forbidden, await Send(api));

        api.Change(enforceLimits: false);

        Assert.Equal(HttpStatusCode.OK, await Send(api));
        Assert.Equal(
            [new GuardViolation(Endpoint, UpTo1000, Blocked: true), new GuardViolation(Endpoint, UpTo1000, Blocked: false)],
            api.Violations.Items);
    }

    [Fact]
    public async Task Change_back_to_true_blocks_a_larger_response_again()
    {
        await using var api = await TinyApi.StartAsync(GuardMode.Enforce, UpTo100);

        api.Change(enforceLimits: false);
        api.Change(enforceLimits: true);

        Assert.Equal(HttpStatusCode.Forbidden, await Send(api));
    }

    [Fact]
    public async Task Each_change_is_logged_once_as_a_warning()
    {
        await using var api = await TinyApi.StartAsync(GuardMode.Enforce, UpTo100);

        // One edit of a file can give more than one reload.
        api.Change(enforceLimits: false);
        api.Change(enforceLimits: false);
        api.Change(enforceLimits: true);

        Assert.Equal(
            ["BehaviorLock EnforceLimits changed from True to False", "BehaviorLock EnforceLimits changed from False to True"],
            api.Logs.Warnings);
        Assert.Empty(api.Logs.Errors);
    }

    [Fact]
    public async Task A_refused_mode_in_the_same_reload_does_not_hold_the_limits_back()
    {
        await using var api = await TinyApi.StartAsync(GuardMode.Enforce, UpTo100);

        api.Change(GuardMode.Learn, enforceLimits: false);

        Assert.Equal(GuardMode.Enforce, api.Services.GetRequiredService<LiveSettings>().Mode);
        Assert.Equal(HttpStatusCode.OK, await Send(api));
        Assert.Single(api.Logs.Errors);
    }

    [Fact]
    public async Task A_change_of_the_mode_keeps_the_limits_as_they_are()
    {
        await using var api = await TinyApi.StartAsync(GuardMode.Audit, UpTo100);

        api.Change(enforceLimits: false);
        api.Change(GuardMode.Enforce);

        Assert.Equal(HttpStatusCode.OK, await Send(api));
        Assert.All(api.Violations.Items, violation => Assert.False(violation.Blocked));
    }

    private static async Task<HttpStatusCode> Send(TinyApi api)
    {
        using var response = await api.GetAsync(Large);
        return response.StatusCode;
    }
}
