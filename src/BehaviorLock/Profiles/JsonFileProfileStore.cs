using System.Text.Encodings.Web;
using System.Text.Json;
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
public sealed class JsonFileProfileStore(IOptions<BehaviorLockOptions> options, IHostEnvironment environment) : IProfileStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        // Keeps quotes and angle brackets in SQL readable in the file.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    // One lock for the process. A host can run StopAsync twice in parallel
    // (WebApplicationFactory does), so two saves of the same file can overlap.
    private static readonly Lock FileLock = new();

    /// <summary>
    /// The full path of the profile file. A relative path is resolved against the content root, like
    /// appsettings.json. The current directory is not used, because it depends on how the process was started.
    /// </summary>
    public string Location => Path.GetFullPath(options.Value.ProfilePath, environment.ContentRootPath);

    public EndpointProfile Load()
    {
        lock (FileLock)
        {
            if (!File.Exists(Location))
            {
                return new EndpointProfile();
            }

            var endpoints = JsonSerializer.Deserialize<Dictionary<string, string[]>>(File.ReadAllText(Location), JsonOptions);
            return new EndpointProfile(endpoints ?? []);
        }
    }

    public void Save(EndpointProfile profile)
    {
        var json = JsonSerializer.Serialize(profile.ToDictionary(), JsonOptions) + Environment.NewLine;

        lock (FileLock)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Location)!);
            File.WriteAllText(Location, json);
        }
    }
}
