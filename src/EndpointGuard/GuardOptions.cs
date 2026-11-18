namespace EndpointGuard;

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

public sealed class GuardOptions
{
    public GuardMode Mode { get; set; } = GuardMode.Audit;

    /// <summary>Path of the profile file. A relative path is resolved against the current directory.</summary>
    public string ProfilePath { get; set; } = "endpointguard.profile.json";
}
