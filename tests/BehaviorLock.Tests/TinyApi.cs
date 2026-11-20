using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BehaviorLock.Tests;

/// <summary>
/// A tiny API on a test server for the checks of the middleware: the caller and the size of the response.
/// It needs no database. The profile file is in a temporary directory.
/// <para>
/// The header <c>X-User</c> signs the caller in. The header <c>X-Roles</c> gives the roles of that caller.
/// </para>
/// </summary>
public sealed class TinyApi : IAsyncDisposable
{
    public const string UserHeader = "X-User";
    public const string RolesHeader = "X-Roles";

    private readonly string _directory;
    private readonly WebApplication _app;

    private TinyApi(string directory, WebApplication app, CollectingSink violations)
    {
        _directory = directory;
        _app = app;
        Violations = violations;
    }

    /// <summary>A client of the test server. It exists when the API has started.</summary>
    public HttpClient Client { get; private set; } = null!;

    public CollectingSink Violations { get; }

    public EndpointProfile Profile => _app.Services.GetRequiredService<EndpointProfile>();

    /// <summary>How often the endpoint <c>/whoami</c> ran.</summary>
    public int Calls { get; private set; }

    /// <summary>Starts the API with this content of the profile file.</summary>
    public static async Task<TinyApi> StartAsync(GuardMode mode, string profile, Action<BehaviorLockOptions>? configure = null)
    {
        var directory = Directory.CreateTempSubdirectory("behaviorlock-").FullName;
        var profilePath = Path.Combine(directory, "behavior.lock.json");
        await File.WriteAllTextAsync(profilePath, profile);

        var violations = new CollectingSink();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = directory });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddBehaviorLock(options =>
        {
            options.Mode = mode;
            options.ProfilePath = profilePath;
            configure?.Invoke(options);
        });
        builder.Services.AddSingleton<IViolationSink>(violations);

        var app = builder.Build();
        var api = new TinyApi(directory, app, violations);

        // Stands for the authentication of a real app. It runs before the guard.
        app.Use(async (http, next) =>
        {
            if (http.Request.Headers.ContainsKey(UserHeader))
            {
                var claims = http.Request.Headers[RolesHeader].ToString()
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(role => new Claim(ClaimTypes.Role, role));
                http.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
            }

            await next(http);
        });

        app.UseBehaviorLock();

        app.MapGet("/whoami", () =>
        {
            api.Calls++;
            return "ok";
        });

        // One write with the whole body.
        app.MapGet("/text", (int size) => new string('x', size));

        // A JSON body, written the way that most endpoints write it.
        app.MapGet("/json", (int count) => Enumerable.Range(0, count).Select(_ => "x"));

        // Several writes, each one sent to the client before the next one.
        app.MapGet("/chunks", async (int size, int count, HttpResponse response) =>
        {
            var chunk = new byte[size];
            Array.Fill(chunk, (byte)'x');

            for (var i = 0; i < count; i++)
            {
                await response.Body.WriteAsync(chunk);
                await response.Body.FlushAsync();
            }
        });

        // Bytes that stay in the writer: the endpoint does not flush them.
        app.MapGet("/unflushed", (int size, HttpResponse response) =>
        {
            response.BodyWriter.GetSpan(size)[..size].Fill((byte)'x');
            response.BodyWriter.Advance(size);
        });

        await app.StartAsync();
        api.Client = app.GetTestClient();
        return api;
    }

    /// <summary>Sends a GET request. Null roles mean an anonymous caller, and an empty text a caller without a role.</summary>
    public Task<HttpResponseMessage> GetAsync(string url, string? roles = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (roles is not null)
        {
            request.Headers.Add(UserHeader, "test");
        }

        if (!string.IsNullOrEmpty(roles))
        {
            request.Headers.Add(RolesHeader, roles);
        }

        return Client.SendAsync(request);
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
        Directory.Delete(_directory, recursive: true);
    }
}
