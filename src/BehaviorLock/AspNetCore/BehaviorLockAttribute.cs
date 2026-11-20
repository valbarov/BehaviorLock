namespace BehaviorLock;

/// <summary>
/// Endpoint metadata that sets the guard mode of one endpoint. The global mode Off or Learn applies to every
/// endpoint. With the global mode Audit or Enforce, the mode of the endpoint applies.
/// </summary>
public interface IGuardModeMetadata
{
    /// <summary>The mode of the endpoint: Off, Audit or Enforce.</summary>
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
