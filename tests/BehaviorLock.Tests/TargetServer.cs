using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace BehaviorLock.Tests;

/// <summary>
/// A server on a real local port that outbound calls can reach. The handlers of .NET write their
/// events only for a real call, so a stub handler is not enough for these tests.
/// </summary>
public sealed class TargetServer : IAsyncDisposable
{
    private readonly WebApplication _app;
    private int _served;

    private TargetServer(WebApplication app) => _app = app;

    /// <summary>Scheme, host and port, for example <c>http://127.0.0.1:50123</c>.</summary>
    public string Authority { get; private set; } = "";

    /// <summary>How often the server answered a request for <c>/</c>.</summary>
    public int Served => _served;

    /// <summary>The <c>traceparent</c> header of the last request for <c>/</c>, or an empty text.</summary>
    public string TraceParent { get; private set; } = "";

    /// <summary>An address of this server that redirects to another address.</summary>
    public string RedirectTo(string url) => $"{Authority}/redirect?to={Uri.EscapeDataString(url)}";

    public static async Task<TargetServer> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        var app = builder.Build();
        var server = new TargetServer(app);

        app.MapGet("/", (HttpContext http) =>
        {
            Interlocked.Increment(ref server._served);
            server.TraceParent = http.Request.Headers["traceparent"].ToString();
            return "target";
        });
        app.MapGet("/redirect", (string to) => Results.Redirect(to));

        await app.StartAsync();
        server.Authority = app.Urls.Single();
        return server;
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
