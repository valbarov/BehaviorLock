namespace BehaviorLock;

/// <summary>State of the request that is being guarded.</summary>
internal sealed class GuardContext(string endpoint, GuardMode? mode)
{
    /// <summary>A context without a mode of its own.</summary>
    public GuardContext(string endpoint)
        : this(endpoint, null)
    {
    }

    /// <summary>Endpoint key, for example <c>GET /products/{id}</c>.</summary>
    public string Endpoint { get; } = endpoint;

    /// <summary>
    /// The own mode of the endpoint or scope. Null means that it has none, so the global mode applies.
    /// The guard puts this mode and the global mode together for each access; see <see cref="GuardModes.Effective"/>.
    /// </summary>
    public GuardMode? Mode { get; } = mode;

    /// <summary>Set when an access of this request was blocked.</summary>
    public GuardViolation? Violation { get; set; }
}

internal interface IGuardContextAccessor
{
    /// <summary>The context of the current request, or null outside a guarded request.</summary>
    GuardContext? Current { get; set; }
}

internal sealed class GuardContextAccessor : IGuardContextAccessor
{
    private readonly AsyncLocal<GuardContext?> _current = new();

    public GuardContext? Current
    {
        get => _current.Value;
        set => _current.Value = value;
    }
}
