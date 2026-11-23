using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace BehaviorLock.Tests;

/// <summary>
/// A change of the mode in the configuration applies while the app runs: the kill switch for a false positive.
/// Only Off, Audit and Enforce change into each other, and a change that would have stopped the start is refused.
/// </summary>
public sealed class ModeReloadTests
{
    [Fact]
    public async Task Change_from_enforce_to_audit_stops_the_blocking()
    {
        await using var api = await ModeTestApi.StartAsync(GuardMode.Enforce);
        await Send(api, "/default", HttpStatusCode.Forbidden);

        api.ChangeMode(GuardMode.Audit);

        await Send(api, "/default", HttpStatusCode.OK);
        Assert.Equal(
            [new GuardViolation("GET /default", ModeTestApi.Unknown, true), new GuardViolation("GET /default", ModeTestApi.Unknown, false)],
            api.Violations.Items);
    }

    [Fact]
    public async Task Change_from_audit_to_enforce_starts_the_blocking()
    {
        await using var api = await ModeTestApi.StartAsync(GuardMode.Audit);
        await Send(api, "/default", HttpStatusCode.OK);

        api.ChangeMode(GuardMode.Enforce);

        await Send(api, "/default", HttpStatusCode.Forbidden);
    }

    [Theory]
    [InlineData(GuardMode.Audit)]
    [InlineData(GuardMode.Enforce)]
    public async Task Change_to_off_turns_the_guard_off_also_for_an_endpoint_marked_enforce(GuardMode start)
    {
        await using var api = await ModeTestApi.StartAsync(start);
        await Send(api, "/enforce", HttpStatusCode.Forbidden);

        api.ChangeMode(GuardMode.Off);

        Assert.Equal("false", await Send(api, "/enforce", HttpStatusCode.OK));
        Assert.Equal("false", await Send(api, "/default", HttpStatusCode.OK));
        Assert.Single(api.Violations.Items);
    }

    [Fact]
    public async Task Change_from_off_to_enforce_starts_the_guard()
    {
        await using var api = await ModeTestApi.StartAsync(GuardMode.Off);
        Assert.Equal("false", await Send(api, "/default", HttpStatusCode.OK));

        api.ChangeMode(GuardMode.Enforce);

        await Send(api, "/default", HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task An_endpoint_marked_enforce_stays_blocked_when_the_global_mode_changes_to_audit()
    {
        // Audit is the mode for the endpoints without a mode of their own. Only Off stops every endpoint.
        await using var api = await ModeTestApi.StartAsync(GuardMode.Enforce);

        api.ChangeMode(GuardMode.Audit);

        await Send(api, "/enforce", HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_change_is_logged_as_a_warning()
    {
        await using var api = await ModeTestApi.StartAsync(GuardMode.Enforce);

        api.ChangeMode(GuardMode.Off);

        Assert.Contains("BehaviorLock mode changed from Enforce to Off", api.Logs.Warnings);
        Assert.Empty(api.Logs.Errors);
    }

    [Fact]
    public async Task Change_to_enforce_is_refused_without_a_profile()
    {
        await using var api = await ModeTestApi.StartAsync(GuardMode.Off, withProfile: false);

        api.ChangeMode(GuardMode.Enforce);

        Assert.Equal(GuardMode.Off, api.Mode);
        Assert.Equal("false", await Send(api, "/default", HttpStatusCode.OK));

        var error = Assert.Single(api.Logs.Errors);
        Assert.StartsWith("BehaviorLock stays in the mode Off. The change to Enforce was refused", error);
        Assert.Contains("is missing or empty", error);
    }

    [Fact]
    public async Task Change_to_audit_is_refused_without_a_profile_when_an_endpoint_is_marked_enforce()
    {
        // Like at the start: the endpoint marked Enforce would block every access.
        await using var api = await ModeTestApi.StartAsync(GuardMode.Off, withProfile: false);

        api.ChangeMode(GuardMode.Audit);

        Assert.Equal(GuardMode.Off, api.Mode);
        Assert.Equal("false", await Send(api, "/enforce", HttpStatusCode.OK));
        Assert.Contains("has the mode Enforce", Assert.Single(api.Logs.Errors));
    }

    [Fact]
    public async Task Change_to_audit_passes_without_a_profile_when_no_endpoint_is_marked_enforce()
    {
        await using var api = await TinyApi.StartAsync(GuardMode.Off, "{}");

        api.ChangeMode(GuardMode.Audit);
        api.ChangeMode(GuardMode.Enforce);

        Assert.Equal(GuardMode.Audit, api.Services.GetRequiredService<LiveSettings>().Mode);
    }

    [Fact]
    public async Task A_refused_change_is_logged_once_and_does_not_stop_the_next_change()
    {
        await using var api = await ModeTestApi.StartAsync(GuardMode.Enforce);

        // One edit of a file can give more than one reload.
        api.ChangeMode(GuardMode.Learn);
        api.ChangeMode(GuardMode.Learn);
        Assert.Single(api.Logs.Errors);
        Assert.Equal(GuardMode.Enforce, api.Mode);

        api.ChangeMode(GuardMode.Audit);
        Assert.Equal(GuardMode.Audit, api.Mode);
    }

    [Theory]
    [InlineData(GuardMode.Learn)]
    [InlineData((GuardMode)42)]
    public async Task Change_to_learn_or_to_an_unknown_mode_is_refused(GuardMode wanted)
    {
        await using var api = await ModeTestApi.StartAsync(GuardMode.Audit);

        api.ChangeMode(wanted);
        await Send(api, "/default", HttpStatusCode.OK);

        // Learn would have added the access to the profile without a report.
        Assert.Equal(GuardMode.Audit, api.Mode);
        Assert.False(api.LoadedProfile.IsAllowed("GET /default", ModeTestApi.Unknown));
        Assert.Single(api.Violations.Items);
        Assert.Contains("can only change between Off, Audit and Enforce", Assert.Single(api.Logs.Errors));
    }

    [Theory]
    [InlineData(GuardMode.Off)]
    [InlineData(GuardMode.Enforce)]
    public async Task Learn_mode_stays_until_the_app_stops(GuardMode wanted)
    {
        await using var api = await ModeTestApi.StartAsync(GuardMode.Learn);

        api.ChangeMode(wanted);

        Assert.Equal("true", await Send(api, "/default", HttpStatusCode.OK));
        Assert.Equal(GuardMode.Learn, api.Mode);
        Assert.True(api.LoadedProfile.IsAllowed("GET /default", ModeTestApi.Unknown));
        Assert.Contains("Learn mode records the profile until the app stops", Assert.Single(api.Logs.Errors));
    }

    [Fact]
    public async Task A_change_during_the_start_applies_when_the_app_has_started()
    {
        await using var api = await ModeTestApi.StartAsync(
            GuardMode.Audit, services: services => services.AddHostedService<ChangesModeOnStart>());

        Assert.Equal(GuardMode.Enforce, api.Mode);
        await Send(api, "/default", HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Change_from_off_starts_the_check_of_every_http_client()
    {
        await using var target = await TargetServer.StartAsync();
        await using var api = await TinyApi.StartAsync(GuardMode.Off, Profile());

        // The endpoint creates its own client, so only the listener of the guard can see the call.
        using var before = await Fetch(api, target);
        api.ChangeMode(GuardMode.Enforce);
        using var after = await Fetch(api, target);

        Assert.Equal("target", await before.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Forbidden, after.StatusCode);
        Assert.Equal(1, target.Served);
    }

    [Fact]
    public async Task The_approved_file_is_read_in_off_mode_for_a_later_change()
    {
        await using var target = await TargetServer.StartAsync();
        var directory = Directory.CreateTempSubdirectory("behaviorlock-").FullName;
        try
        {
            var approvedPath = Path.Combine(directory, "behavior.lock.approved.json");
            await File.WriteAllTextAsync(approvedPath, Profile($"http: GET {target.Authority}"));
            await using var api = await TinyApi.StartAsync(
                GuardMode.Off, Profile(), options => options.ApprovedPath = approvedPath);

            api.ChangeMode(GuardMode.Enforce);
            using var response = await Fetch(api, target);

            Assert.Equal("target", await response.Content.ReadAsStringAsync());
            Assert.Empty(api.Violations.Items);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>Sends a GET request, checks the status code and returns the body.</summary>
    private static async Task<string> Send(ModeTestApi api, string path, HttpStatusCode expected)
    {
        using var response = await api.Client.GetAsync(path);

        Assert.Equal(expected, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }

    private static Task<HttpResponseMessage> Fetch(TinyApi api, TargetServer target) =>
        api.GetAsync($"/fetch?url={Uri.EscapeDataString(target.Authority)}");

    /// <summary>A profile for the endpoint that calls out. The size limit is what a recorded profile has.</summary>
    private static string Profile(params string[] accesses) =>
        JsonSerializer.Serialize(new Dictionary<string, string[]>
        {
            ["GET /fetch"] = [.. accesses, "response: up to 1000 bytes"],
        });

    /// <summary>Starts after the guard, and before the app has started.</summary>
    private sealed class ChangesModeOnStart(IConfiguration configuration) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken)
        {
            ReloadableSettings.Change(configuration, GuardMode.Enforce);
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
