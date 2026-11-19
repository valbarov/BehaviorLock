using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BehaviorLock;

/// <summary>
/// Logs the guard state on start and stops the start when Enforce mode has no profile.
/// Saves the profile on shutdown in Learn mode.
/// </summary>
internal sealed class ProfileLifecycle(
    EndpointProfile profile,
    IProfileStore store,
    IOptions<BehaviorLockOptions> options,
    ILogger<ProfileLifecycle> logger) : IHostedService
{
    private readonly Lock _saveLock = new();
    private bool _saved;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var mode = options.Value.Mode;
        var count = profile.EndpointCount;

        // An empty profile allows nothing. Most often the file is missing or ProfilePath is wrong.
        if (count == 0 && mode == GuardMode.Enforce)
        {
            throw new InvalidOperationException(
                $"BehaviorLock mode is Enforce, but the profile at '{store.Location}' is missing or empty. " +
                "Every request that touches a resource would be blocked. " +
                "Record the profile in Learn mode and deploy the file with the app, or correct ProfilePath.");
        }

        if (count == 0 && mode == GuardMode.Audit)
        {
            logger.LogWarning(
                "BehaviorLock mode is Audit, but the profile at {Location} is missing or empty. " +
                "Every resource access will be reported as a violation",
                store.Location);
        }

        logger.LogInformation(
            "BehaviorLock mode is {Mode}; the profile at {Location} has {Count} endpoints",
            mode,
            store.Location,
            count);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        if (options.Value.Mode == GuardMode.Learn)
        {
            // A host can run StopAsync twice in parallel (WebApplicationFactory does). Save once,
            // and let the second call wait, so that the file is complete when any of the calls returns.
            lock (_saveLock)
            {
                if (!_saved)
                {
                    store.Save(profile);
                    _saved = true;
                    logger.LogInformation("BehaviorLock saved the profile to {Location}", store.Location);
                }
            }
        }

        return Task.CompletedTask;
    }
}
