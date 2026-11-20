using Microsoft.Extensions.Options;

namespace BehaviorLock;

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
    /// A limit is recorded and reported with its number rounded up; see <see cref="ResourceAccess.RoundedUp"/>.
    /// A number over a limit is not blocked when <see cref="BehaviorLockOptions.EnforceLimits"/> is false.
    /// </summary>
    void Check(ResourceAccess access);
}

public sealed class AccessGuard(
    IGuardContextAccessor contextAccessor,
    EndpointProfile profile,
    IEnumerable<IViolationSink> sinks,
    IOptions<BehaviorLockOptions> options) : IAccessGuard
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
        var blocked = mode == GuardMode.Enforce && (access.UpTo is null || options.Value.EnforceLimits);
        var violation = new GuardViolation(context.Endpoint, access.RoundedUp(), blocked);
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
    // The middleware puts the mode of the endpoint into the context. A context without a mode uses the global mode.
    // The global rules are applied here again, so that global Off turns the guard off for every context.
    private GuardMode ModeOf(GuardContext? context) =>
        context is null ? GuardMode.Off : GuardModes.Effective(options.Value.Mode, context.Mode);
}
