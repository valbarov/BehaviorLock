namespace BehaviorLock;

/// <summary>State of the request that is being guarded.</summary>
public sealed class GuardContext(string endpoint)
{
    /// <summary>Endpoint key, for example <c>GET /products/{id}</c>.</summary>
    public string Endpoint { get; } = endpoint;

    /// <summary>Set when an access of this request was blocked.</summary>
    public GuardViolation? Violation { get; set; }
}

public interface IGuardContextAccessor
{
    /// <summary>The context of the current request, or null outside a guarded request.</summary>
    GuardContext? Current { get; set; }
}

public sealed class GuardContextAccessor : IGuardContextAccessor
{
    private readonly AsyncLocal<GuardContext?> _current = new();

    public GuardContext? Current
    {
        get => _current.Value;
        set => _current.Value = value;
    }
}
