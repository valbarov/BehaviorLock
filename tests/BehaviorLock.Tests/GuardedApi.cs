using System.Collections.Concurrent;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using SampleApi;

namespace BehaviorLock.Tests;

/// <summary>The sample API with a private in-memory database, a stubbed outbound HTTP handler and a chosen guard mode.</summary>
public sealed class GuardedApi : WebApplicationFactory<Program>
{
    private readonly GuardMode _mode;
    private readonly string _profilePath;
    private readonly string _connectionString = $"Data Source=guard-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
    private readonly SqliteConnection _keepAlive;

    public GuardedApi(GuardMode mode, string profilePath)
    {
        _mode = mode;
        _profilePath = profilePath;

        // A shared in-memory database lives as long as one connection is open.
        _keepAlive = new SqliteConnection(_connectionString);
        _keepAlive.Open();
    }

    public StubHttpHandler Outbound { get; } = new();

    public CollectingSink Violations { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:Shop", _connectionString);
        builder.ConfigureServices(services =>
        {
            services.Configure<BehaviorLockOptions>(options =>
            {
                options.Mode = _mode;
                options.ProfilePath = _profilePath;
            });
            services.AddSingleton<IViolationSink>(Violations);
            services.ConfigureHttpClientDefaults(http => http.ConfigurePrimaryHttpMessageHandler(() => Outbound));
        });
    }

    /// <summary>Reads the database directly. This runs outside a request, so the guard does not check it.</summary>
    public async Task<T> QueryDb<T>(Func<ShopDb, Task<T>> query)
    {
        using var scope = Services.CreateScope();
        return await query(scope.ServiceProvider.GetRequiredService<ShopDb>());
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            _keepAlive.Dispose();
        }
    }
}

/// <summary>Answers 200 to every outbound call and remembers the URLs.</summary>
public sealed class StubHttpHandler : HttpMessageHandler
{
    public ConcurrentQueue<string> Urls { get; } = new();

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Urls.Enqueue(request.RequestUri!.ToString());
        return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
    }

    protected override void Dispose(bool disposing)
    {
        // The factory reuses this instance, so it must stay usable.
    }
}

public sealed class CollectingSink : IViolationSink
{
    public ConcurrentQueue<GuardViolation> Items { get; } = new();

    public void Report(GuardViolation violation) => Items.Enqueue(violation);
}
