namespace BehaviorLock;

/// <summary>
/// The settings that can change while the app runs: the global mode and
/// <see cref="BehaviorLockOptions.EnforceLimits"/>. They start with the values of the options.
/// <see cref="ProfileLifecycle"/> changes them when the configuration changes. Every other option is read
/// once, when the app starts.
/// <para>
/// Only Off, Audit and Enforce can change into each other. Learn never starts and never ends in a running
/// app, so code that only asks for Learn can read the options of the start.
/// </para>
/// <para>
/// The guard does not ask the options of .NET for each access. After a reload with a value that cannot be
/// read, they throw, and every request would fail. This class keeps the last good values.
/// </para>
/// </summary>
internal sealed class LiveSettings(GuardMode mode, bool enforceLimits = true)
{
    // Each access check reads the settings, and a change comes from another thread.
    private volatile GuardMode _mode = mode;
    private volatile bool _enforceLimits = enforceLimits;

    /// <summary>The global mode that applies now.</summary>
    public GuardMode Mode => _mode;

    /// <summary>False means that a number over a limit is reported, but not blocked.</summary>
    public bool EnforceLimits => _enforceLimits;

    /// <summary>Raised after each change of the mode, with the new mode.</summary>
    public event Action<GuardMode>? ModeChanged;

    /// <summary>Sets the mode without a check. <see cref="ProfileLifecycle"/> decides whether a change is legal.</summary>
    public void ChangeMode(GuardMode mode)
    {
        _mode = mode;
        ModeChanged?.Invoke(mode);
    }

    public void ChangeEnforceLimits(bool enforceLimits) => _enforceLimits = enforceLimits;
}
