namespace BehaviorLock;

/// <summary>
/// Checks one resource access against the profile of the current endpoint or scope.
/// Call it from your own code to guard a resource that the package does not cover.
/// </summary>
public interface IAccessGuard
{
    /// <summary>
    /// False when <see cref="Check"/> would ignore every access: the mode of the current request is
    /// <see cref="GuardMode.Off"/> or the code runs outside a guarded request. Callers can then skip the work
    /// of building the access.
    /// </summary>
    bool IsActive { get; }

    /// <summary>
    /// Call this before a resource is touched.
    /// Throws <see cref="GuardViolationException"/> when the access must be blocked.
    /// A limit is recorded and reported with its number rounded up to 0, 1, 10, 100 and so on.
    /// A number over a limit is not blocked when <see cref="BehaviorLockOptions.EnforceLimits"/> is false.
    /// </summary>
    void Check(ResourceAccess access);
}

internal sealed class AccessGuard(
    IGuardContextAccessor contextAccessor,
    EndpointProfile profile,
    IEnumerable<IViolationSink> sinks,
    LiveSettings settings) : IAccessGuard
{
    public bool IsActive => ModeOf(contextAccessor.Current) != GuardMode.Off;

    public void Check(ResourceAccess access)
    {
        var context = contextAccessor.Current;
        var mode = ModeOf(context);

        if (mode == GuardMode.Off || context is null)
        {
            return;
        }

        if (mode == GuardMode.Learn)
        {
            profile.Allow(context.Endpoint, access.RoundedUp());
            return;
        }

        if (profile.IsAllowed(context.Endpoint, access))
        {
            return;
        }

        // A caller or a limit needs an entry to compare it with. Without one, there is nothing to check.
        if (access.OnlyCheckedWhenRecorded && !profile.HasEntryLike(context.Endpoint, access))
        {
            return;
        }

        // A limit can be report-only: see BehaviorLockOptions.EnforceLimits.
        var blocked = mode == GuardMode.Enforce && (access.UpTo is null || settings.EnforceLimits);
        var violation = new GuardViolation(context.Endpoint, access.RoundedUp(), blocked)
        {
            RequestId = context.RequestId,
        };
        foreach (var sink in sinks)
        {
            sink.Report(violation);
        }

        if (violation.Blocked)
        {
            context.Violation = violation;
            throw new GuardViolationException(violation);
        }
    }

    // No context means the access is outside a guarded request, for example startup code.
    // The context has the own mode of the endpoint or scope. The global mode is read for each access,
    // so a change of it also reaches a request or a scope that is already running.
    private GuardMode ModeOf(GuardContext? context) =>
        context is null ? GuardMode.Off : GuardModes.Effective(settings.Mode, context.Mode);
}
