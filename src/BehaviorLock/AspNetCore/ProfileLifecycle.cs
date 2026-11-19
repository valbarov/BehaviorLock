using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BehaviorLock;

/// <summary>
/// Logs the guard state on start. Stops the start when Enforce mode has no profile,
/// or a profile that was recorded with another database provider.
/// Saves the profile on shutdown in Learn mode.
/// </summary>
internal sealed class ProfileLifecycle(
    EndpointProfile profile,
    IProfileStore store,
    IEnumerable<EndpointDataSource> endpointSources,
    DatabaseCheck databaseCheck,
    IServiceScopeFactory scopes,
    IOptions<BehaviorLockOptions> options,
    ILogger<ProfileLifecycle> logger) : IHostedLifecycleService
{
    private readonly Lock _saveLock = new();
    private bool _saved;

    // Why no endpoint can run in Enforce mode with this profile. Null when nothing is wrong.
    private string? _problem;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var mode = options.Value.Mode;
        var count = profile.EndpointCount;

        if (mode != GuardMode.Off)
        {
            _problem = FindProblem(mode);
        }

        if (_problem is not null && mode == GuardMode.Enforce)
        {
            throw new InvalidOperationException($"BehaviorLock mode is Enforce, but {_problem}");
        }

        // A wrong database provider is already in the log, as an error of the database check.
        if (_problem is not null && mode == GuardMode.Audit && count == 0)
        {
            logger.LogWarning("BehaviorLock mode is Audit, but {Problem}", _problem);
        }

        logger.LogInformation(
            "BehaviorLock mode is {Mode}; the profile at {Location} has {Count} endpoints",
            mode,
            store.Location,
            count);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Checks the endpoints that have their own mode. The endpoints are known only now,
    /// after the server has built the request pipeline.
    /// </summary>
    public Task StartedAsync(CancellationToken cancellationToken)
    {
        if (options.Value.Mode == GuardMode.Audit && _problem is not null && FindEnforcedEndpoint() is { } endpoint)
        {
            throw new InvalidOperationException($"BehaviorLock endpoint '{endpoint}' has the mode Enforce, but {_problem}");
        }

        return Task.CompletedTask;
    }

    public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

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

    private string? FindProblem(GuardMode mode)
    {
        // In Learn mode this call records the database. In the other modes it compares it.
        var database = databaseCheck.ObserveRegisteredContexts(scopes);

        if (mode == GuardMode.Learn)
        {
            return null;
        }

        // An empty profile allows nothing. Most often the file is missing or ProfilePath is wrong.
        if (profile.EndpointCount == 0)
        {
            return $"the profile at '{store.Location}' is missing or empty, so it allows no access. " +
                "Record the profile in Learn mode and deploy the file with the app, or correct ProfilePath.";
        }

        return database is { Match: DatabaseMatch.OtherProvider }
            ? $"the profile at '{store.Location}' does not fit the database of the app: {databaseCheck.Explain(database)}"
            : null;
    }

    private string? FindEnforcedEndpoint() => endpointSources
        .SelectMany(source => source.Endpoints)
        .FirstOrDefault(endpoint => endpoint.Metadata.GetMetadata<IGuardModeMetadata>()?.Mode == GuardMode.Enforce)
        ?.DisplayName;
}
