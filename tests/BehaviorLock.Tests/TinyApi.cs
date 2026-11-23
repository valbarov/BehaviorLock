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
/// It also has endpoints that call another server with a real HTTP handler; see <see cref="TargetServer"/>.
/// It needs no database. The profile file is in a temporary directory.
/// <para>
/// The header <c>X-User</c> signs the caller in. The header <c>X-Roles</c> gives the roles of that caller,
/// and the header <c>X-Scope</c> its <c>scope</c> claim.
/// </para>
/// </summary>
public sealed class TinyApi : IAsyncDisposable
{
    public const string UserHeader = "X-User";
    public const string RolesHeader = "X-Roles";
    public const string ScopeHeader = "X-Scope";

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

    public IServiceProvider Services => _app.Services;

    /// <summary>How often the endpoint <c>/whoami</c> ran.</summary>
    public int Calls { get; private set; }

    /// <summary>Changes the mode in the configuration of the running API, like an edit of appsettings.json does.</summary>
    public void ChangeMode(GuardMode mode) => ModeSetting.Change(_app.Configuration, mode);

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
        ModeSetting.Add(builder.Configuration, mode);
        builder.Services.AddBehaviorLock(builder.Configuration.GetSection(ModeSetting.Section), options =>
        {
            options.ProfilePath = profilePath;
            configure?.Invoke(options);
        });
        builder.Services.AddSingleton<IViolationSink>(violations);
        builder.Services.AddHttpClient();

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

                // One claim with all scopes, divided by spaces, as a token has it.
                if (http.Request.Headers.TryGetValue(ScopeHeader, out var scope))
                {
                    claims = claims.Append(new Claim("scope", scope.ToString()));
                }

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

        // Stands for a package that creates its own client. IHttpClientFactory does not know this client.
        app.MapGet("/fetch", async (string url) =>
        {
            using var client = new HttpClient();
            return await client.GetStringAsync(url);
        });

        // The same, but the package catches every error of its call.
        app.MapGet("/fetch-quiet", async (string url) =>
        {
            try
            {
                using var client = new HttpClient();
                return await client.GetStringAsync(url);
            }
            catch (Exception)
            {
                return "no answer";
            }
        });

        // A client of the factory, with the real handler of .NET below the handler of the guard.
        app.MapGet("/fetch-factory", async (string url, IHttpClientFactory factory) =>
            await factory.CreateClient().GetStringAsync(url));

        await app.StartAsync();
        api.Client = app.GetTestClient();
        return api;
    }

    /// <summary>Sends a GET request. Null roles mean an anonymous caller, and an empty text a caller without a role.</summary>
    public Task<HttpResponseMessage> GetAsync(string url, string? roles = null, string? scope = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (roles is not null)
        {
            request.Headers.Add(UserHeader, "test");
        }

        if (scope is not null)
        {
            request.Headers.Add(ScopeHeader, scope);
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
