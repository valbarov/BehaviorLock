using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BehaviorLock.Tests;

/// <summary>
/// A tiny API on a test server with one endpoint for each way to set a mode. Every endpoint makes one SQL access
/// that is not in the profile, so no database is needed. The profile file is in a temporary directory.
/// </summary>
public sealed class ModeTestApi : IAsyncDisposable
{
    public static readonly ResourceAccess Unknown = ResourceAccess.Sql("SELECT * FROM Users");

    // Enforce mode does not start with an empty profile, so the profile has one endpoint that no test calls.
    private const string Profile = """{ "GET /other": [ "sql: SELECT * FROM Products" ] }""";

    private readonly string _directory;
    private readonly WebApplication _app;

    private ModeTestApi(string directory, WebApplication app, CollectingSink violations, CollectingLogs logs)
    {
        _directory = directory;
        _app = app;
        Violations = violations;
        Logs = logs;
        Client = app.GetTestClient();
    }

    public HttpClient Client { get; }

    public CollectingSink Violations { get; }

    /// <summary>The log lines of the guard.</summary>
    public CollectingLogs Logs { get; }

    /// <summary>The global mode that the guard uses now.</summary>
    public GuardMode Mode => _app.Services.GetRequiredService<LiveSettings>().Mode;

    public EndpointProfile LoadedProfile => _app.Services.GetRequiredService<EndpointProfile>();

    /// <summary>Changes the mode in the configuration of the running API, like an edit of appsettings.json does.</summary>
    public void ChangeMode(GuardMode mode) => ReloadableSettings.Change(_app.Configuration, mode);

    /// <summary>Starts the API. Without a profile, no profile file is written.</summary>
    public static async Task<ModeTestApi> StartAsync(
        GuardMode global, bool withProfile = true, Action<IServiceCollection>? services = null)
    {
        var directory = Directory.CreateTempSubdirectory("behaviorlock-").FullName;
        var profilePath = Path.Combine(directory, "behavior.lock.json");
        if (withProfile)
        {
            await File.WriteAllTextAsync(profilePath, Profile);
        }

        var violations = new CollectingSink();
        var logs = new CollectingLogs();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = directory });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(logs);
        ReloadableSettings.Add(builder.Configuration, global);
        builder.Services.AddBehaviorLock(
            builder.Configuration.GetSection(ReloadableSettings.Section), options => options.ProfilePath = profilePath);
        builder.Services.AddSingleton<IViolationSink>(violations);
        builder.Services.AddControllers().AddApplicationPart(typeof(ModeTestController).Assembly);
        services?.Invoke(builder.Services);

        var app = builder.Build();
        app.UseBehaviorLock();

        app.MapGet("/default", Touch);
        app.MapGet("/off", Touch).WithBehaviorLock(GuardMode.Off);
        app.MapGet("/audit", Touch).WithBehaviorLock(GuardMode.Audit);
        app.MapGet("/enforce", Touch).WithBehaviorLock(GuardMode.Enforce);
        app.MapGet("/attribute", [BehaviorLock(GuardMode.Enforce)] (IAccessGuard guard) => Touch(guard));

        var group = app.MapGroup("/group").WithBehaviorLock(GuardMode.Enforce);
        group.MapGet("/plain", Touch);
        group.MapGet("/audit", Touch).WithBehaviorLock(GuardMode.Audit);

        app.MapControllers();

        try
        {
            await app.StartAsync();
        }
        catch
        {
            await app.DisposeAsync();
            Directory.Delete(directory, recursive: true);
            throw;
        }

        return new ModeTestApi(directory, app, violations, logs);
    }

    /// <summary>Makes one access that is not in the profile. Returns true when the guard was active.</summary>
    public static bool Touch(IAccessGuard guard)
    {
        var active = guard.IsActive;
        guard.Check(Unknown);
        return active;
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
        Directory.Delete(_directory, recursive: true);
    }
}

/// <summary>The mode of the controller applies to its actions. The mode of an action beats it.</summary>
[BehaviorLock(GuardMode.Enforce)]
[Route("mvc")]
public sealed class ModeTestController(IAccessGuard guard) : ControllerBase
{
    [HttpGet("controller")]
    public bool FromController() => ModeTestApi.Touch(guard);

    [HttpGet("action")]
    [BehaviorLock(GuardMode.Off)]
    public bool FromAction() => ModeTestApi.Touch(guard);
}
