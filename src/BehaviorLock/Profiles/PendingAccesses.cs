using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BehaviorLock;

/// <summary>
/// Collects the accesses that are not in the profile and writes them to the pending file.
/// A person reviews that file. A pending access is either a legal path that the tests do not cover,
/// or an attack. Does nothing when <see cref="BehaviorLockOptions.PendingPath"/> is not set.
/// </summary>
internal sealed class PendingAccesses(
    IOptions<BehaviorLockOptions> options,
    IHostEnvironment environment,
    EndpointProfile profile,
    ILogger<PendingAccesses> logger) : BackgroundService, IViolationSink
{
    /// <summary>The file is written at most once per interval, and once more when the host stops.</summary>
    private static readonly TimeSpan WriteInterval = TimeSpan.FromSeconds(10);

    private readonly Lock _lock = new();
    private EndpointProfile _pending = new();
    private bool _changed;
    private bool _limitReported;
    private bool _writeFailureReported;

    /// <summary>The full path of the pending file, or null when no pending file is written.</summary>
    public string? Location { get; } =
        options.Value.PendingPath is { } path ? ProfileFile.Resolve(path, environment) : null;

    public void Report(GuardViolation violation)
    {
        if (Location is null)
        {
            return;
        }

        lock (_lock)
        {
            if (_pending.IsAllowed(violation.Endpoint, violation.Access))
            {
                return;
            }

            if (_pending.AccessCount >= options.Value.PendingLimit)
            {
                if (!_limitReported)
                {
                    _limitReported = true;
                    logger.LogWarning(
                        "The BehaviorLock pending file {Location} is full ({Limit} accesses). " +
                        "New accesses are not written. Review the file and remove its entries",
                        Location,
                        options.Value.PendingLimit);
                }

                return;
            }

            _pending.Allow(violation.Endpoint, violation.Access);
            _changed = true;
        }
    }

    /// <summary>Writes the pending file now, if it has new content.</summary>
    public void Write()
    {
        if (Location is null)
        {
            return;
        }

        lock (_lock)
        {
            if (!_changed)
            {
                return;
            }

            try
            {
                ProfileFile.Write(Location, _pending);
                _changed = false;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A read-only file system must not stop the app. The next interval tries again.
                if (!_writeFailureReported)
                {
                    _writeFailureReported = true;
                    logger.LogError(exception, "BehaviorLock cannot write the pending file {Location}", Location);
                }
            }
        }
    }

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        if (Location is not null)
        {
            logger.LogInformation("BehaviorLock writes accesses that are not in the profile to {Location}", Location);
            ReadEarlierEntries();
        }

        return base.StartAsync(cancellationToken);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        Write();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (Location is null)
        {
            return;
        }

        using var timer = new PeriodicTimer(WriteInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                Write();
            }
        }
        catch (OperationCanceledException)
        {
            // The host stops. StopAsync writes the file one more time.
        }
    }

    /// <summary>
    /// Keeps the entries of an earlier run, so that a restart does not lose them.
    /// Entries that the profile allows now are dropped: somebody approved them or added a test.
    /// </summary>
    private void ReadEarlierEntries()
    {
        EndpointProfile? earlier;
        try
        {
            earlier = ProfileFile.Read(Location!);
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "BehaviorLock cannot read the pending file {Location}. It starts a new one", Location);
            return;
        }

        if (earlier is null)
        {
            return;
        }

        lock (_lock)
        {
            _pending = earlier.Except(profile);
            _changed = _pending.AccessCount != earlier.AccessCount;
        }
    }
}
