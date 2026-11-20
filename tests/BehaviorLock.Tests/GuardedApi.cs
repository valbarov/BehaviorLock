using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SampleApi;

namespace BehaviorLock.Tests;

/// <summary>The sample API with a private in-memory database, a stubbed outbound HTTP handler and a chosen guard mode.</summary>
public sealed class GuardedApi : WebApplicationFactory<Program>
{
    private readonly GuardMode _mode;
    private readonly string _profilePath;
    private readonly string _connectionString = $"Data Source=guard-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
    private readonly SqliteConnection _keepAlive;
    private readonly StartupFailure _startupFailure = new();

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

    /// <summary>Where the guard writes the accesses that are not in the profile. Null means no pending file.</summary>
    public string? PendingPath { get; init; }

    /// <summary>The file with reviewed accesses. Null means no approved file.</summary>
    public string? ApprovedPath { get; init; }

    /// <summary>Changes more options of the guard. Null means that the defaults are used.</summary>
    public Action<BehaviorLockOptions>? Configure { get; init; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:Shop", _connectionString);
        builder.ConfigureServices(services =>
        {
            services.Configure<BehaviorLockOptions>(options =>
            {
                options.Mode = _mode;
                options.ProfilePath = _profilePath;
                options.PendingPath = PendingPath;
                options.ApprovedPath = ApprovedPath;
                Configure?.Invoke(options);
            });
            services.AddSingleton<IViolationSink>(Violations);
            services.AddSingleton<ILoggerProvider>(_startupFailure);
            services.ConfigureHttpClientDefaults(http => http.ConfigurePrimaryHttpMessageHandler(() => Outbound));
        });
    }

    /// <summary>Reads the database directly. This runs outside a request, so the guard does not check it.</summary>
    public async Task<T> QueryDb<T>(Func<ShopDb, Task<T>> query)
    {
        using var scope = Services.CreateScope();
        return await query(scope.ServiceProvider.GetRequiredService<ShopDb>());
    }

    /// <summary>
    /// Makes a failed start of the app give the exception of the app, every time.
    /// <para>
    /// The app runs on its own thread. When its start fails, it disposes its services. This thread asks the
    /// same services for the lifetime of the app, to wait for the start. Most times it asks first, waits, and
    /// gets the exception of the app. When the app is faster, it gets an ObjectDisposedException in its place.
    /// The host has logged the exception of the app before it disposed the services, so it is known here.
    /// </para>
    /// </summary>
    protected override IHost CreateHost(IHostBuilder builder)
    {
        try
        {
            return base.CreateHost(builder);
        }
        catch (ObjectDisposedException) when (_startupFailure.Exception is { } failure)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
            throw;
        }
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

/// <summary>Keeps the exception that the host logs when the app fails at startup.</summary>
internal sealed class StartupFailure : ILoggerProvider, ILogger
{
    private Exception? _exception;

    public Exception? Exception => Volatile.Read(ref _exception);

    public ILogger CreateLogger(string categoryName) =>
        categoryName.StartsWith("Microsoft.Extensions.Hosting", StringComparison.Ordinal) ? this : NullLogger.Instance;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (logLevel >= LogLevel.Error && exception is not null)
        {
            Interlocked.CompareExchange(ref _exception, exception, null);
        }
    }

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public void Dispose()
    {
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
