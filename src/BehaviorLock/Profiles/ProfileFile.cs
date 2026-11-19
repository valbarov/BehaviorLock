using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Extensions.Hosting;

namespace BehaviorLock;

/// <summary>
/// Reads and writes the JSON form of a profile: endpoint key → sorted list of accesses.
/// The recorded profile, the approved file and the pending file all use this form.
/// </summary>
internal static class ProfileFile
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
    /// Returns the full path. A relative path is resolved against the content root, like appsettings.json.
    /// The current directory is not used, because it depends on how the process was started.
    /// </summary>
    public static string Resolve(string path, IHostEnvironment environment) =>
        Path.GetFullPath(path, environment.ContentRootPath);

    /// <summary>Returns null when the file does not exist.</summary>
    public static EndpointProfile? Read(string path)
    {
        lock (FileLock)
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var endpoints = JsonSerializer.Deserialize<Dictionary<string, string[]>>(File.ReadAllText(path), JsonOptions);
            return new EndpointProfile(endpoints ?? []);
        }
    }

    public static void Write(string path, EndpointProfile profile)
    {
        var json = JsonSerializer.Serialize(profile.ToDictionary(), JsonOptions) + Environment.NewLine;

        lock (FileLock)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, json);
        }
    }
}
