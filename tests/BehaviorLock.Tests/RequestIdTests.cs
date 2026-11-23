using System.Net;

namespace BehaviorLock.Tests;

/// <summary>
/// Each violation of a request has the id of that request, <c>HttpContext.TraceIdentifier</c>, also in the
/// log line. So support can find why a user got a 403. See <see cref="ModeTestApi"/>.
/// </summary>
public sealed class RequestIdTests
{
    [Theory]
    [InlineData(GuardMode.Audit, HttpStatusCode.OK)]
    [InlineData(GuardMode.Enforce, HttpStatusCode.Forbidden)]
    public async Task A_violation_has_the_id_of_its_request(GuardMode mode, HttpStatusCode expected)
    {
        await using var api = await ModeTestApi.StartAsync(mode);

        await Send(api, "/default", expected, requestId: "gateway-4711");

        Assert.Equal(["gateway-4711"], api.Violations.RequestIds);
    }

    [Fact]
    public async Task The_log_line_has_the_id_of_the_request()
    {
        await using var api = await ModeTestApi.StartAsync(GuardMode.Enforce);

        await Send(api, "/default", HttpStatusCode.Forbidden, requestId: "gateway-4711");

        Assert.Equal(
            ["BehaviorLock violation (blocked): endpoint 'GET /default' attempted 'sql: SELECT * FROM Users' in request gateway-4711"],
            api.Logs.Warnings);
    }

    [Fact]
    public async Task All_violations_of_one_request_have_the_same_id()
    {
        await using var api = await ModeTestApi.StartAsync(GuardMode.Audit);

        await Send(api, "/twice", HttpStatusCode.OK, requestId: "gateway-4711");

        Assert.Equal(["gateway-4711", "gateway-4711"], api.Violations.RequestIds);
    }

    [Fact]
    public async Task Without_an_id_from_the_app_each_request_has_its_own_id_from_the_server()
    {
        await using var api = await ModeTestApi.StartAsync(GuardMode.Audit);

        await Send(api, "/default", HttpStatusCode.OK);
        await Send(api, "/default", HttpStatusCode.OK);

        var ids = api.Violations.RequestIds.ToArray();
        Assert.Equal(2, ids.Length);
        Assert.All(ids, id => Assert.False(string.IsNullOrEmpty(id)));
        Assert.NotEqual(ids[0], ids[1]);
    }

    private static async Task Send(ModeTestApi api, string path, HttpStatusCode expected, string? requestId = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (requestId is not null)
        {
            request.Headers.Add(ModeTestApi.RequestIdHeader, requestId);
        }

        using var response = await api.Client.SendAsync(request);

        Assert.Equal(expected, response.StatusCode);
    }
}
