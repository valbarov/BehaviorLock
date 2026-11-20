using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace BehaviorLock;

public interface IProfileStore
{
    /// <summary>Where the profile is stored, for example a full file path. Used in log and error messages.</summary>
    string Location { get; }

    /// <summary>Loads the profile. Returns an empty profile when none is stored.</summary>
    EndpointProfile Load();

    void Save(EndpointProfile profile);
}

/// <summary>Stores the profile as a readable JSON file: endpoint key → sorted list of accesses.</summary>
internal sealed class JsonFileProfileStore(IOptions<BehaviorLockOptions> options, IHostEnvironment environment) : IProfileStore
{
    /// <summary>The full path of the profile file. A relative path is resolved against the content root.</summary>
    public string Location => ProfileFile.Resolve(options.Value.ProfilePath, environment);

    public EndpointProfile Load() => ProfileFile.Read(Location) ?? new EndpointProfile();

    public void Save(EndpointProfile profile) => ProfileFile.Write(Location, profile);
}
