namespace BehaviorLock;

/// <summary>Endpoint metadata that sets the guard mode of one endpoint. See <see cref="GuardModes.Effective"/>.</summary>
public interface IGuardModeMetadata
{
    GuardMode Mode { get; }
}

/// <summary>
/// Sets the guard mode of an endpoint, a minimal API handler, an MVC controller or an action.
/// Only Off, Audit and Enforce are legal. The global mode Off or Learn still applies to every endpoint.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class BehaviorLockAttribute(GuardMode mode) : Attribute, IGuardModeMetadata
{
    /// <exception cref="ArgumentOutOfRangeException">The mode is Learn.</exception>
    public GuardMode Mode { get; } = GuardModes.RequireEndpointMode(mode, nameof(mode));
}
