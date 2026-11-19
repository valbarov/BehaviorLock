namespace BehaviorLock;

/// <summary>Rules for the mode of one endpoint.</summary>
public static class GuardModes
{
    /// <summary>
    /// Returns the mode that applies to one endpoint.
    /// Global <see cref="GuardMode.Off"/> turns the guard off for every endpoint.
    /// Global <see cref="GuardMode.Learn"/> records every endpoint, so the profile covers all of them.
    /// Global <see cref="GuardMode.Audit"/> or <see cref="GuardMode.Enforce"/> gives the mode of the endpoint
    /// when it has one, else the global mode.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The global mode is Audit or Enforce and the endpoint mode is not Off, Audit or Enforce.</exception>
    public static GuardMode Effective(GuardMode global, GuardMode? endpoint) => global switch
    {
        GuardMode.Off => GuardMode.Off,
        GuardMode.Learn => GuardMode.Learn,
        _ when endpoint is { } mode => RequireEndpointMode(mode, nameof(endpoint)),
        _ => global,
    };

    /// <summary>
    /// Only Off, Audit and Enforce are legal for one endpoint. Learn on one endpoint in production
    /// would allow every access there without a report.
    /// </summary>
    internal static GuardMode RequireEndpointMode(GuardMode mode, string parameterName) =>
        mode is GuardMode.Off or GuardMode.Audit or GuardMode.Enforce
            ? mode
            : throw new ArgumentOutOfRangeException(
                parameterName, mode, "The mode of one endpoint must be Off, Audit or Enforce. Learn is only legal as the global mode.");
}
