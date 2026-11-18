using Microsoft.Extensions.Options;

namespace EndpointGuard;

public interface IAccessGuard
{
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
    IOptions<GuardOptions> options) : IAccessGuard
{
    public void Check(ResourceAccess access)
    {
        var mode = options.Value.Mode;
        var context = contextAccessor.Current;

        // No context means the access is outside a guarded request, for example startup code.
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
