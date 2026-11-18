using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace EndpointGuard;

public interface IProfileStore
{
    /// <summary>Loads the profile. Returns an empty profile when none is stored.</summary>
    EndpointProfile Load();

    void Save(EndpointProfile profile);
}

/// <summary>Stores the profile as a readable JSON file: endpoint key → sorted list of accesses.</summary>
public sealed class JsonFileProfileStore(IOptions<GuardOptions> options) : IProfileStore
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

    private string Path => System.IO.Path.GetFullPath(options.Value.ProfilePath);

    public EndpointProfile Load()
    {
        lock (FileLock)
        {
            if (!File.Exists(Path))
            {
                return new EndpointProfile();
            }

            var endpoints = JsonSerializer.Deserialize<Dictionary<string, string[]>>(File.ReadAllText(Path), JsonOptions);
            return new EndpointProfile(endpoints ?? []);
        }
    }

    public void Save(EndpointProfile profile)
    {
        var json = JsonSerializer.Serialize(profile.ToDictionary(), JsonOptions) + Environment.NewLine;

        lock (FileLock)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            File.WriteAllText(Path, json);
        }
    }
}
