using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace BehaviorLock.Tests;

/// <summary>
/// The guard checks the calls of every <see cref="HttpClient"/>, also of a client that a package creates
/// by itself, and each redirect hop. The calls go to real local ports; see <see cref="TargetServer"/>.
/// </summary>
public sealed class EveryHttpClientTests : IAsyncLifetime
{
    private TargetServer _legal = null!;
    private TargetServer _foreign = null!;

    public async Task InitializeAsync()
    {
        _legal = await TargetServer.StartAsync();
        _foreign = await TargetServer.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _legal.DisposeAsync();
        await _foreign.DisposeAsync();
    }

    [Theory]
    [InlineData("/fetch")]
    [InlineData("/fetch-factory")]
    public async Task Learn_mode_records_the_host_once(string path)
    {
        await using var api = await TinyApi.StartAsync(GuardMode.Learn, "{}");

        using var response = await Fetch(api, path, _legal.Authority);

        Assert.Equal("target", await response.Content.ReadAsStringAsync());
        Assert.Equal([Get(_legal)], HttpEntries(api, path));
    }

    [Theory]
    [InlineData("/fetch")]
    [InlineData("/fetch-factory")]
    public async Task A_recorded_host_passes_in_enforce_mode(string path)
    {
        await using var api = await TinyApi.StartAsync(GuardMode.Enforce, Profile(_legal));

        using var response = await Fetch(api, path, _legal.Authority);

        Assert.Equal("target", await response.Content.ReadAsStringAsync());
        Assert.Empty(api.Violations.Items);
    }

    [Theory]
    [InlineData("/fetch")]
    [InlineData("/fetch-quiet")]
    [InlineData("/fetch-factory")]
    public async Task Another_host_is_blocked_before_the_call_is_sent(string path)
    {
        await using var api = await TinyApi.StartAsync(GuardMode.Enforce, Profile(_legal));

        using var response = await Fetch(api, path, _foreign.Authority);

        // Also when the package catches the exception of its call: the request is marked as blocked.
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsStringAsync());
        Assert.Equal(0, _foreign.Served);
        Assert.Equal([Violation(path, _foreign, blocked: true)], api.Violations.Items);
    }

    [Theory]
    [InlineData("/fetch")]
    [InlineData("/fetch-factory")]
    public async Task A_redirect_to_another_host_is_blocked(string path)
    {
        await using var api = await TinyApi.StartAsync(GuardMode.Enforce, Profile(_legal));

        // The first address is legal. Its answer sends the client to a host that the profile does not have.
        using var response = await Fetch(api, path, _legal.RedirectTo(_foreign.Authority));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, _foreign.Served);
        Assert.Equal([Violation(path, _foreign, blocked: true)], api.Violations.Items);
    }

    [Theory]
    [InlineData("/fetch")]
    [InlineData("/fetch-factory")]
    public async Task A_redirect_between_recorded_hosts_passes(string path)
    {
        await using var api = await TinyApi.StartAsync(GuardMode.Enforce, Profile(_legal, _foreign));

        using var response = await Fetch(api, path, _legal.RedirectTo(_foreign.Authority));

        Assert.Equal("target", await response.Content.ReadAsStringAsync());
        Assert.Equal(1, _foreign.Served);
        Assert.Empty(api.Violations.Items);
    }

    [Fact]
    public async Task Learn_mode_records_each_host_of_a_redirect()
    {
        await using var api = await TinyApi.StartAsync(GuardMode.Learn, "{}");

        using var response = await Fetch(api, "/fetch", _legal.RedirectTo(_foreign.Authority));

        Assert.Equal(new[] { Get(_legal), Get(_foreign) }.Order(), HttpEntries(api, "/fetch"));
    }

    [Theory]
    [InlineData("/fetch")]
    [InlineData("/fetch-factory")]
    public async Task Audit_mode_reports_another_host_once_and_lets_the_call_pass(string path)
    {
        await using var api = await TinyApi.StartAsync(GuardMode.Audit, Profile(_legal));

        using var response = await Fetch(api, path, _foreign.Authority);

        // A client of the factory passes two checks. The second one must not report the same call again.
        Assert.Equal("target", await response.Content.ReadAsStringAsync());
        Assert.Equal([Violation(path, _foreign, blocked: false)], api.Violations.Items);
    }

    [Fact]
    public async Task The_option_turns_the_check_of_other_clients_off()
    {
        await using var api = await TinyApi.StartAsync(
            GuardMode.Enforce, Profile(_legal), options => options.CheckAllHttpClients = false);

        using var own = await Fetch(api, "/fetch", _foreign.Authority);
        using var redirected = await Fetch(api, "/fetch-factory", _legal.RedirectTo(_foreign.Authority));
        using var factory = await Fetch(api, "/fetch-factory", _foreign.Authority);

        // The handler of the factory still checks the first address of its clients.
        Assert.Equal(HttpStatusCode.OK, own.StatusCode);
        Assert.Equal(HttpStatusCode.OK, redirected.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, factory.StatusCode);
        Assert.Equal([Violation("/fetch-factory", _foreign, blocked: true)], api.Violations.Items);
    }

    [Fact]
    public async Task A_call_outside_a_request_is_not_checked()
    {
        await using var api = await TinyApi.StartAsync(GuardMode.Enforce, Profile(_legal));
        using var client = new HttpClient();

        Assert.Equal("target", await client.GetStringAsync(_foreign.Authority));

        // No activity was made for this call, so it carries no trace header.
        Assert.Empty(_foreign.TraceParent);
        Assert.Empty(api.Violations.Items);
    }

    [Fact]
    public async Task A_call_inside_a_scope_is_checked()
    {
        const string job = "job sync";
        var profile = JsonSerializer.Serialize(new Dictionary<string, string[]> { [job] = [Get(_legal)] });
        await using var api = await TinyApi.StartAsync(GuardMode.Enforce, profile);
        using var client = new HttpClient();

        using var scope = api.Services.GetRequiredService<GuardScopes>().Begin(job);
        var legal = await client.GetStringAsync(_legal.Authority);
        var exception = await Assert.ThrowsAsync<GuardViolationException>(() => client.GetStringAsync(_foreign.Authority));

        Assert.Equal("target", legal);
        Assert.Equal(new GuardViolation(job, new ResourceAccess("http", $"GET {_foreign.Authority}"), Blocked: true), exception.Violation);
        Assert.Equal(0, _foreign.Served);
    }

    [Fact]
    public async Task Each_app_of_the_process_sees_only_its_own_calls()
    {
        // The event of a call reaches every guard of the process. A guard must ignore the calls of another app.
        await using var recording = await TinyApi.StartAsync(GuardMode.Learn, "{}");
        await using var api = await TinyApi.StartAsync(GuardMode.Enforce, Profile(_legal));

        using var response = await Fetch(api, "/fetch", _legal.Authority);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, recording.Profile.EndpointCount);
    }

    [Fact]
    public async Task A_stopped_app_checks_no_more_calls()
    {
        var api = await TinyApi.StartAsync(GuardMode.Enforce, Profile(_legal));
        var scopes = api.Services.GetRequiredService<GuardScopes>();
        await api.DisposeAsync();
        using var client = new HttpClient();

        using var scope = scopes.Begin("job after the stop");

        Assert.Equal("target", await client.GetStringAsync(_foreign.Authority));
    }

    private static Task<HttpResponseMessage> Fetch(TinyApi api, string path, string url) =>
        api.GetAsync($"{path}?url={Uri.EscapeDataString(url)}");

    private static string Get(TargetServer server) => $"http: GET {server.Authority}";

    private static GuardViolation Violation(string path, TargetServer server, bool blocked) =>
        new($"GET {path}", new ResourceAccess("http", $"GET {server.Authority}"), blocked);

    private static IEnumerable<string> HttpEntries(TinyApi api, string path) =>
        api.Profile.ToDictionary()[$"GET {path}"].Where(entry => entry.StartsWith("http:"));

    /// <summary>A profile that allows these servers for every endpoint that calls out.</summary>
    private static string Profile(params TargetServer[] servers)
    {
        // The size limit is what a recorded profile has. With it, the guard also stops the answer of an
        // endpoint that goes on after a blocked call.
        string[] accesses = [.. servers.Select(Get), "response: up to 1000 bytes"];

        return JsonSerializer.Serialize(new Dictionary<string, string[]>
        {
            ["GET /fetch"] = accesses,
            ["GET /fetch-quiet"] = accesses,
            ["GET /fetch-factory"] = accesses,
        });
    }
}
