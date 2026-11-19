using Microsoft.Extensions.Options;

namespace BehaviorLock;

public interface IAccessGuard
{
    /// <summary>
    /// False when <see cref="Check"/> would ignore every access: the mode is <see cref="GuardMode.Off"/>
    /// or the code runs outside a guarded request. Callers can then skip the work of building the access.
    /// </summary>
    bool IsActive { get; }

    /// <summary>
    /// Call this before a resource is touched.
    /// Throws <see cref="GuardViolationException"/> when the access must be blocked.
    /// </summary>
    void Check(ResourceAccess access);
}

public sealed class AccessGuard(
    IGuardContextAccessor contextAccessor,
    EndpointProfile profile,
    IEnumerable<IViolationSink> sinks,
    IOptions<BehaviorLockOptions> options) : IAccessGuard
{
    // No context means the access is outside a guarded request, for example startup code.
    public bool IsActive => options.Value.Mode != GuardMode.Off && contextAccessor.Current is not null;

    public void Check(ResourceAccess access)
    {
        var mode = options.Value.Mode;
        var context = contextAccessor.Current;

        if (mode == GuardMode.Off || context is null)
        {
            return;
        }

        if (mode == GuardMode.Learn)
        {
            profile.Allow(context.Endpoint, access);
            return;
        }

        if (profile.IsAllowed(context.Endpoint, access))
        {
            return;
        }

        var violation = new GuardViolation(context.Endpoint, access, Blocked: mode == GuardMode.Enforce);
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
}
