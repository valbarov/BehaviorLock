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
/// <para>
/// It also follows <see cref="BehaviorLockOptions.Mode"/> while the app runs, so that a change of the
/// configuration can turn the blocking off without a new deploy. Only Off, Audit and Enforce can change
/// into each other. A change that would have stopped the start is refused, and the mode stays as it is.
/// </para>
/// </summary>
internal sealed class ProfileLifecycle(
    EndpointProfile profile,
    IProfileStore store,
    IEnumerable<EndpointDataSource> endpointSources,
    DatabaseCheck databaseCheck,
    IServiceScopeFactory scopes,
    GlobalMode globalMode,
    IOptionsMonitor<BehaviorLockOptions> options,
    ILogger<ProfileLifecycle> logger) : IHostedLifecycleService
{
    private readonly Lock _saveLock = new();
    private bool _saved;

    // Guards the fields below. A change of the options comes on a thread of the configuration.
    private readonly Lock _modeLock = new();
    private IDisposable? _changes;
    private bool _started;

    // The mode of a change that came before the app had started.
    private GuardMode? _early;

    // The mode of the last refused change. One change of a file can come more than once, and is logged once.
    private GuardMode? _refused;

    // Why no endpoint can run in Enforce mode with this profile. Null when nothing is wrong, or not known yet.
    private string? _problem;
    private bool _problemKnown;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var mode = globalMode.Current;

        // Off mode does not touch the database. The check is made when the mode changes.
        var problem = mode == GuardMode.Off ? null : Problem(mode);

        if (problem is not null && mode == GuardMode.Enforce)
        {
            throw new InvalidOperationException($"BehaviorLock mode is Enforce, but {problem}");
        }

        WarnAboutEmptyProfile(mode);

        logger.LogInformation(
            "BehaviorLock mode is {Mode}; the profile at {Location} has {Count} endpoints",
            mode,
            store.Location,
            profile.EndpointCount);

        // Only the options without a name are used. A change is applied when the app has started.
        _changes = options.OnChange((changed, name) =>
        {
            if (string.IsNullOrEmpty(name))
            {
                OnOptionsChanged(changed.Mode);
            }
        });
        return Task.CompletedTask;
    }

    /// <summary>
    /// Checks the endpoints that have their own mode. The endpoints are known only now,
    /// after the server has built the request pipeline.
    /// </summary>
    public Task StartedAsync(CancellationToken cancellationToken)
    {
        if (globalMode.Current == GuardMode.Audit && _problem is not null && FindEnforcedEndpoint() is { } endpoint)
        {
            throw new InvalidOperationException($"BehaviorLock endpoint '{endpoint}' has the mode Enforce, but {_problem}");
        }

        lock (_modeLock)
        {
            _started = true;
            if (_early is { } wanted)
            {
                Apply(wanted);
            }
        }

        return Task.CompletedTask;
    }

    public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _changes?.Dispose();

        if (globalMode.Current == GuardMode.Learn)
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

    private void OnOptionsChanged(GuardMode wanted)
    {
        lock (_modeLock)
        {
            // The endpoints are not known before the start has ended. StartedAsync applies the last change.
            if (!_started)
            {
                _early = wanted;
                return;
            }

            Apply(wanted);
        }
    }

    /// <summary>
    /// Changes the global mode, or logs why not. It runs inside the reload of the configuration,
    /// so no exception may leave it.
    /// </summary>
    private void Apply(GuardMode wanted)
    {
        var current = globalMode.Current;
        try
        {
            if (wanted == current)
            {
                _refused = null;
                return;
            }

            if (WhyNot(current, wanted) is { } reason)
            {
                if (_refused != wanted)
                {
                    _refused = wanted;
                    logger.LogError(
                        "BehaviorLock stays in the mode {Mode}. The change to {Wanted} was refused: {Reason}",
                        current,
                        wanted,
                        reason);
                }

                return;
            }

            _refused = null;
            globalMode.Change(wanted);

            // A change of the mode changes what an attacker can do, so it must be easy to find in the log.
            logger.LogWarning("BehaviorLock mode changed from {Old} to {New}", current, wanted);
            WarnAboutEmptyProfile(wanted);
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "BehaviorLock failed in the change of the mode to {Wanted}. The mode is now {Mode}",
                wanted,
                globalMode.Current);
        }
    }

    /// <summary>Returns why the mode cannot change, or null when it can.</summary>
    private string? WhyNot(GuardMode current, GuardMode wanted)
    {
        if (current == GuardMode.Learn)
        {
            return "Learn mode records the profile until the app stops. Start the app again with the new mode.";
        }

        // Learn in a running app would allow every access without a report, and it would save the approved
        // accesses into the recorded profile.
        if (wanted is not (GuardMode.Off or GuardMode.Audit or GuardMode.Enforce))
        {
            return "a running app can only change between Off, Audit and Enforce. Start the app again for Learn mode.";
        }

        // The same checks as on start. Off needs none, so the way to Off is always open.
        if (wanted == GuardMode.Off || Problem(wanted) is not { } problem)
        {
            return null;
        }

        if (wanted == GuardMode.Enforce)
        {
            return problem;
        }

        return FindEnforcedEndpoint() is { } endpoint
            ? $"endpoint '{endpoint}' has the mode Enforce, but {problem}"
            : null;
    }

    // A wrong database provider is already in the log, as an error of the database check.
    private void WarnAboutEmptyProfile(GuardMode mode)
    {
        if (mode == GuardMode.Audit && _problem is not null && profile.EndpointCount == 0)
        {
            logger.LogWarning("BehaviorLock mode is Audit, but {Problem}", _problem);
        }
    }

    /// <summary>
    /// Finds the problem once. Outside Learn mode the profile does not change while the app runs,
    /// and the app keeps its database. So a later change of the mode costs nothing here.
    /// </summary>
    private string? Problem(GuardMode mode)
    {
        if (!_problemKnown)
        {
            _problem = FindProblem(mode);
            _problemKnown = true;
        }

        return _problem;
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
