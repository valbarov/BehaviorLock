namespace BehaviorLock;

/// <summary>
/// The global mode that applies now. It starts as <see cref="BehaviorLockOptions.Mode"/>.
/// <see cref="ProfileLifecycle"/> changes it when the configuration changes while the app runs.
/// <para>
/// Only Off, Audit and Enforce can change into each other. Learn never starts and never ends in a running
/// app, so code that only asks for Learn can read the options of the start.
/// </para>
/// </summary>
internal sealed class GlobalMode(GuardMode start)
{
    // Each access check reads the mode, and a change comes from another thread.
    private volatile GuardMode _current = start;

    public GuardMode Current => _current;

    /// <summary>Raised after each change, with the new mode.</summary>
    public event Action<GuardMode>? Changed;

    /// <summary>Sets the mode without a check. <see cref="ProfileLifecycle"/> decides whether a change is legal.</summary>
    public void Change(GuardMode mode)
    {
        _current = mode;
        Changed?.Invoke(mode);
    }
}
