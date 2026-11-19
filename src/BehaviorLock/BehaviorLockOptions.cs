namespace BehaviorLock;

public enum GuardMode
{
    /// <summary>The guard does nothing.</summary>
    Off,

    /// <summary>Every resource access is added to the profile. The profile is saved on shutdown.</summary>
    Learn,

    /// <summary>Accesses outside the profile are reported but allowed.</summary>
    Audit,

    /// <summary>Accesses outside the profile are reported and blocked.</summary>
    Enforce,
}

public sealed class BehaviorLockOptions
{
    public GuardMode Mode { get; set; } = GuardMode.Audit;

    /// <summary>
    /// Path of the profile file. A relative path is resolved against the content root of the app,
    /// the same directory that holds appsettings.json.
    /// </summary>
    public string ProfilePath { get; set; } = "behavior.lock.json";

    /// <summary>
    /// Path of the pending file, or null to write none. The guard writes every access that is not in the
    /// profile to this file, in the format of the profile. A person reviews the file and moves the legal
    /// accesses to the approved file. A relative path is resolved like <see cref="ProfilePath"/>.
    /// </summary>
    public string? PendingPath { get; set; }

    /// <summary>
    /// The largest number of accesses in the pending file. Each attack attempt can give a new access,
    /// so the file must not grow without end. Further accesses are still reported, but not written.
    /// </summary>
    public int PendingLimit { get; set; } = 1000;

    /// <summary>
    /// Path of the approved file, or null to use none. It lists accesses that a person reviewed and accepted.
    /// They are allowed like the accesses of the profile. The file has the format of the profile, and the
    /// recording never changes it. A relative path is resolved like <see cref="ProfilePath"/>.
    /// </summary>
    public string? ApprovedPath { get; set; }
}
