using System.Collections.Concurrent;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace BehaviorLock.Tests;

/// <summary>
/// Keeps the global mode in the configuration of a test API, so that a test can change it and
/// <c>EnforceLimits</c> while the API runs. An app gets the same when it edits appsettings.json.
/// </summary>
internal static class ReloadableSettings
{
    public const string Section = "BehaviorLock";

    private const string ModeKey = $"{Section}:Mode";
    private const string EnforceLimitsKey = $"{Section}:EnforceLimits";

    /// <summary>Adds a source that keeps its values over a reload.</summary>
    public static void Add(IConfigurationBuilder configuration, GuardMode mode) =>
        configuration.AddInMemoryCollection(new Dictionary<string, string?> { [ModeKey] = mode.ToString() });

    /// <summary>
    /// Sets the given values and tells the listeners, on this thread, that the configuration has changed.
    /// Null keeps a value as it is.
    /// </summary>
    public static void Change(IConfiguration configuration, GuardMode? mode = null, bool? enforceLimits = null)
    {
        if (mode is { } newMode)
        {
            configuration[ModeKey] = newMode.ToString();
        }

        if (enforceLimits is { } newValue)
        {
            configuration[EnforceLimitsKey] = newValue.ToString();
        }

        ((IConfigurationRoot)configuration).Reload();
    }
}

/// <summary>Keeps the log lines of the guard that are warnings or errors.</summary>
public sealed class CollectingLogs : ILoggerProvider, ILogger
{
    public ConcurrentQueue<(LogLevel Level, string Text)> Items { get; } = new();

    public IEnumerable<string> Errors => Items.Where(item => item.Level == LogLevel.Error).Select(item => item.Text);

    public IEnumerable<string> Warnings => Items.Where(item => item.Level == LogLevel.Warning).Select(item => item.Text);

    public ILogger CreateLogger(string categoryName) =>
        categoryName.StartsWith("BehaviorLock", StringComparison.Ordinal)
            ? this
            : Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (IsEnabled(logLevel))
        {
            Items.Enqueue((logLevel, formatter(state, exception)));
        }
    }

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public void Dispose()
    {
    }
}
