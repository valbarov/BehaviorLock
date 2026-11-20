using System.Net;

namespace BehaviorLock.Tests;

/// <summary>The guard records the size of the response body of each endpoint, and it stops a larger one. See <see cref="TinyApi"/>.</summary>
public sealed class ResponseSizeTests
{
    private const string UpTo100 = """
        {
          "GET /text": [ "response: up to 100 bytes" ],
          "GET /json": [ "response: up to 100 bytes" ],
          "GET /chunks": [ "response: up to 100 bytes" ],
          "GET /unflushed": [ "response: up to 100 bytes" ]
        }
        """;

    // An approved access without a limit, or a profile that an older version recorded.
    private const string WithoutLimit = """{ "GET /text": [ "sql: SELECT * FROM Products" ] }""";

    private static readonly ResourceAccess UpTo1000 = ResourceAccess.ResponseSize(1000);

    [Theory]
    [InlineData("/text?size=0", 0)]
    [InlineData("/text?size=1", 1)]
    [InlineData("/text?size=7", 10)]
    [InlineData("/text?size=100", 100)]
    [InlineData("/text?size=101", 1000)]
    [InlineData("/chunks?size=40&count=3", 1000)]
    [InlineData("/unflushed?size=250", 1000)]
    public async Task Learn_mode_records_the_size_rounded_up(string url, long expected)
    {
        await using var api = await TinyApi.StartAsync(GuardMode.Learn, "{}");

        using var response = await api.GetAsync(url);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(expected, api.Profile.LimitOf(EndpointOf(url), ResourceAccess.ResponseSize(0)));
    }

    [Fact]
    public async Task Learn_mode_keeps_the_largest_size()
    {
        await using var api = await TinyApi.StartAsync(GuardMode.Learn, "{}");

        foreach (var size in new[] { 50, 5000, 7 })
        {
            using var response = await api.GetAsync($"/text?size={size}");
        }

        Assert.Equal(["caller: anonymous", "response: up to 10000 bytes"], api.Profile.ToDictionary()["GET /text"]);
    }

    [Theory]
    [InlineData("/text?size=100")]
    [InlineData("/chunks?size=50&count=2")]
    [InlineData("/unflushed?size=100")]
    public async Task A_response_up_to_the_limit_passes_in_enforce_mode(string url)
    {
        await using var api = await TinyApi.StartAsync(GuardMode.Enforce, UpTo100);

        using var response = await api.GetAsync(url);

        Assert.Equal(new string('x', 100), await response.Content.ReadAsStringAsync());
        Assert.Empty(api.Violations.Items);
    }

    [Fact]
    public async Task A_json_response_up_to_the_limit_passes_in_enforce_mode()
    {
        await using var api = await TinyApi.StartAsync(GuardMode.Enforce, UpTo100);

        using var response = await api.GetAsync("/json?count=3");

        Assert.Equal("[\"x\",\"x\",\"x\"]", await response.Content.ReadAsStringAsync());
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Empty(api.Violations.Items);
    }

    [Theory]
    [InlineData("/text?size=101")]
    [InlineData("/json?count=50")]
    [InlineData("/unflushed?size=101")]
    public async Task A_larger_response_is_blocked_in_enforce_mode(string url)
    {
        await using var api = await TinyApi.StartAsync(GuardMode.Enforce, UpTo100);

        using var response = await api.GetAsync(url);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsStringAsync());
        Assert.Equal([new GuardViolation(EndpointOf(url), UpTo1000, Blocked: true)], api.Violations.Items);
    }

    [Fact]
    public async Task A_response_that_has_started_is_cut_off_at_the_limit()
    {
        await using var api = await TinyApi.StartAsync(GuardMode.Enforce, UpTo100);

        // The first 80 bytes are on their way when the second write passes the limit. The status line
        // cannot change any more, so the guard closes the connection. The client can lose the first bytes too.
        using var response = await api.Client.GetAsync("/chunks?size=80&count=3", HttpCompletionOption.ResponseHeadersRead);
        await using var body = await response.Content.ReadAsStreamAsync();

        var received = 0;
        var buffer = new byte[1024];
        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            int read;
            while ((read = await body.ReadAsync(buffer)) > 0)
            {
                received += read;
            }
        });

        Assert.InRange(received, 0, 80);
        Assert.Equal([new GuardViolation("GET /chunks", UpTo1000, Blocked: true)], api.Violations.Items);
    }

    [Theory]
    [InlineData("/text?size=101", 101)]
    [InlineData("/chunks?size=80&count=3", 240)]
    public async Task A_larger_response_is_reported_once_but_not_blocked_in_audit_mode(string url, int size)
    {
        await using var api = await TinyApi.StartAsync(GuardMode.Audit, UpTo100);

        using var response = await api.GetAsync(url);

        Assert.Equal(new string('x', size), await response.Content.ReadAsStringAsync());
        Assert.Equal([new GuardViolation(EndpointOf(url), UpTo1000, Blocked: false)], api.Violations.Items);
    }

    [Theory]
    [InlineData("/text?size=101", 101)]
    [InlineData("/json?count=50", 201)]
    [InlineData("/chunks?size=80&count=3", 240)]
    [InlineData("/unflushed?size=101", 101)]
    public async Task A_larger_response_is_only_reported_when_limits_are_not_enforced(string url, int size)
    {
        await using var api = await TinyApi.StartAsync(GuardMode.Enforce, UpTo100, options => options.EnforceLimits = false);

        using var response = await api.GetAsync(url);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(size, (await response.Content.ReadAsByteArrayAsync()).Length);
        Assert.Equal([new GuardViolation(EndpointOf(url), UpTo1000, Blocked: false)], api.Violations.Items);
    }

    [Fact]
    public async Task A_limit_written_by_a_person_needs_no_round_number()
    {
        await using var api = await TinyApi.StartAsync(GuardMode.Enforce, """{ "GET /text": [ "response: up to 250 bytes" ] }""");

        using var allowed = await api.GetAsync("/text?size=250");
        using var blocked = await api.GetAsync("/text?size=251");

        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, blocked.StatusCode);
    }

    [Fact]
    public async Task An_endpoint_without_a_recorded_limit_is_not_checked()
    {
        await using var api = await TinyApi.StartAsync(GuardMode.Enforce, WithoutLimit);

        using var response = await api.GetAsync("/text?size=50000");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(api.Violations.Items);
    }

    [Fact]
    public async Task The_option_turns_the_check_off()
    {
        await using var api = await TinyApi.StartAsync(GuardMode.Enforce, UpTo100, options => options.CheckLimits = false);

        using var response = await api.GetAsync("/text?size=50000");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(api.Violations.Items);
    }

    [Fact]
    public async Task The_option_turns_the_recording_off()
    {
        await using var api = await TinyApi.StartAsync(GuardMode.Learn, "{}", options => options.CheckLimits = false);

        using var response = await api.GetAsync("/text?size=50");

        Assert.Equal(["caller: anonymous"], api.Profile.ToDictionary()["GET /text"]);
    }

    private static string EndpointOf(string url) => "GET " + url.Split('?')[0];
}
