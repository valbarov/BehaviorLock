using Microsoft.Extensions.Options;

namespace BehaviorLock;

/// <summary>
/// Opens a guard context for code that runs outside a request, for example a background job or a
/// message handler. Inside the scope the guard checks SQL commands and outbound calls like in a request.
/// The name of the scope takes the place of the endpoint key in the profile.
/// </summary>
public sealed class GuardScopes(
    IGuardContextAccessor contextAccessor, EndpointProfile profile, IOptions<BehaviorLockOptions> options)
{
    /// <summary>
    /// Starts a scope. Dispose the result to end it; the context from before then applies again.
    /// <para>
    /// A blocked access throws <see cref="GuardViolationException"/> in the code of the scope.
    /// There is no response that could become a 403.
    /// </para>
    /// <para>
    /// Call it from the method that does the work, or from a method without <c>async</c>. A context that
    /// an <c>async</c> method sets is not visible to its caller.
    /// </para>
    /// </summary>
    /// <param name="name">
    /// The key in the profile, for example <c>job nightly-cleanup</c>. Use a name that no endpoint has:
    /// a scope with the key of an endpoint shares the profile of that endpoint.
    /// </param>
    /// <param name="mode">
    /// The mode of this scope: Off, Audit or Enforce. Null means the global mode. The global modes Off and
    /// Learn apply to every scope, like to every endpoint. See <see cref="GuardModes.Effective"/>.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">The mode is Learn.</exception>
    public IDisposable Begin(string name, GuardMode? mode = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (mode is { } own)
        {
            GuardModes.RequireEndpointMode(own, nameof(mode));
        }

        var effective = GuardModes.Effective(options.Value.Mode, mode);
        if (effective == GuardMode.Learn)
        {
            profile.AddEndpoint(name);
        }

        var scope = new Scope(contextAccessor, contextAccessor.Current);
        contextAccessor.Current = new GuardContext(name, effective);
        return scope;
    }

    private sealed class Scope(IGuardContextAccessor contextAccessor, GuardContext? outer) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                contextAccessor.Current = outer;
            }
        }
    }
}
