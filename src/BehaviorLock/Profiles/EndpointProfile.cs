using System.Globalization;
using System.Text.RegularExpressions;

namespace BehaviorLock;

/// <summary>
/// The allowed resource accesses per endpoint. Thread-safe.
/// <para>
/// An access must be in the profile word for word. A limit is the exception: the profile keeps the
/// highest number of each limit, and every number up to it is allowed. See <see cref="ResourceAccess.UpTo"/>.
/// </para>
/// </summary>
public sealed partial class EndpointProfile
{
    private readonly Dictionary<string, Entries> _endpoints = new(StringComparer.Ordinal);
    private readonly SortedSet<string> _databases = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();

    /// <summary>Creates an empty profile.</summary>
    public EndpointProfile()
    {
    }

    /// <summary>Creates a profile from the entries of a profile file.</summary>
    public EndpointProfile(IReadOnlyDictionary<string, string[]> endpoints)
    {
        foreach (var (endpoint, accesses) in endpoints)
        {
            var entries = GetOrAdd(endpoint);
            foreach (var access in accesses)
            {
                entries.Add(access);
            }
        }
    }

    /// <summary>
    /// The databases that the profile was recorded with, for example
    /// <c>Microsoft.EntityFrameworkCore.Sqlite 11.0.0</c>. Empty when the recording made no SQL access.
    /// A profile store must keep them with the profile, because the guard compares them with the database
    /// of the app at startup.
    /// </summary>
    public IReadOnlyCollection<string> Databases
    {
        get
        {
            lock (_lock)
            {
                return _databases.ToArray();
            }
        }
    }

    /// <summary>Adds a database to <see cref="Databases"/>. A profile store calls it when it loads a profile.</summary>
    public void AddDatabase(string database)
    {
        lock (_lock)
        {
            _databases.Add(database);
        }
    }

    /// <summary>Registers an endpoint, also when it touches no resources.</summary>
    internal void AddEndpoint(string endpoint)
    {
        lock (_lock)
        {
            GetOrAdd(endpoint);
        }
    }

    /// <summary>Adds an access. A limit is raised to the number of the access when that number is higher.</summary>
    public void Allow(string endpoint, ResourceAccess access)
    {
        lock (_lock)
        {
            var entries = GetOrAdd(endpoint);
            if (access.UpTo is { } number)
            {
                entries.Raise((access.Kind, access.Key), number);
            }
            else
            {
                entries.Accesses.Add(access.ToString());
            }
        }
    }

    /// <summary>
    /// True when the access is in the profile. For a limit: true when the profile has this limit and the
    /// number of the access is not higher.
    /// </summary>
    public bool IsAllowed(string endpoint, ResourceAccess access)
    {
        lock (_lock)
        {
            if (!_endpoints.TryGetValue(endpoint, out var entries))
            {
                return false;
            }

            return access.UpTo is { } number
                ? entries.Limits.TryGetValue((access.Kind, access.Key), out var limit) && number <= limit
                : entries.Accesses.Contains(access.ToString());
        }
    }

    /// <summary>Returns the highest allowed number of this limit, or null when the profile does not have the limit.</summary>
    internal long? LimitOf(string endpoint, ResourceAccess limit)
    {
        lock (_lock)
        {
            return _endpoints.TryGetValue(endpoint, out var entries)
                && entries.Limits.TryGetValue((limit.Kind, limit.Key), out var number) ? number : null;
        }
    }

    /// <summary>
    /// True when the profile has an entry that this access can be compared with: for a limit the same limit
    /// with any number, for another access any access of the same kind.
    /// See <see cref="ResourceAccess.OnlyCheckedWhenRecorded"/>.
    /// </summary>
    internal bool HasEntryLike(string endpoint, ResourceAccess access)
    {
        lock (_lock)
        {
            if (!_endpoints.TryGetValue(endpoint, out var entries))
            {
                return false;
            }

            if (access.UpTo is not null)
            {
                return entries.Limits.ContainsKey((access.Kind, access.Key));
            }

            var prefix = access.Kind + ": ";
            foreach (var known in entries.Accesses)
            {
                if (known.StartsWith(prefix, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>The number of endpoints in the profile. Zero means that nothing was recorded or loaded.</summary>
    public int EndpointCount
    {
        get
        {
            lock (_lock)
            {
                return _endpoints.Count;
            }
        }
    }

    /// <summary>The number of accesses of all endpoints together. A limit counts as one access.</summary>
    public int AccessCount
    {
        get
        {
            lock (_lock)
            {
                return _endpoints.Values.Sum(entries => entries.Accesses.Count + entries.Limits.Count);
            }
        }
    }

    /// <summary>
    /// Adds all endpoints and accesses of the other profile. Of two limits for the same thing, the higher one stays.
    /// Returns the number of accesses that are new or higher.
    /// </summary>
    internal int Merge(EndpointProfile other)
    {
        var source = other.ToDictionary();

        lock (_lock)
        {
            var added = 0;
            foreach (var (endpoint, accesses) in source)
            {
                var target = GetOrAdd(endpoint);
                added += accesses.Count(target.Add);
            }

            return added;
        }
    }

    /// <summary>Returns a new profile with the accesses of this profile that the other profile does not allow.</summary>
    internal EndpointProfile Except(EndpointProfile other)
    {
        var rest = new Dictionary<string, string[]>(StringComparer.Ordinal);

        foreach (var (endpoint, accesses) in ToDictionary())
        {
            var left = accesses.Where(access => !other.Allows(endpoint, access)).ToArray();
            if (left.Length > 0)
            {
                rest[endpoint] = left;
            }
        }

        return new EndpointProfile(rest);
    }

    /// <summary>True when the profile has this endpoint, also when the endpoint has no accesses.</summary>
    public bool ContainsEndpoint(string endpoint)
    {
        lock (_lock)
        {
            return _endpoints.ContainsKey(endpoint);
        }
    }

    /// <summary>The endpoints and their accesses as a sorted copy, so that the saved file gives stable diffs.</summary>
    public SortedDictionary<string, string[]> ToDictionary()
    {
        lock (_lock)
        {
            var copy = new SortedDictionary<string, string[]>(StringComparer.Ordinal);
            foreach (var (endpoint, entries) in _endpoints)
            {
                var limits = entries.Limits.Select(limit =>
                    new ResourceAccess(limit.Key.Kind, limit.Key.Key) { UpTo = limit.Value }.ToString());

                copy[endpoint] = entries.Accesses.Concat(limits).Order(StringComparer.Ordinal).ToArray();
            }

            return copy;
        }
    }

    /// <summary>True when the profile allows this entry of a profile file.</summary>
    private bool Allows(string endpoint, string entry)
    {
        lock (_lock)
        {
            return _endpoints.TryGetValue(endpoint, out var entries) && entries.Allows(entry);
        }
    }

    private Entries GetOrAdd(string endpoint)
    {
        if (!_endpoints.TryGetValue(endpoint, out var entries))
        {
            _endpoints[endpoint] = entries = new Entries();
        }

        return entries;
    }

    // "rows: up to 100 from SELECT ..." gives the kind "rows", the number 100 and the key "from SELECT ...".
    // A command that is not normalized can have line breaks, so the key can have them too.
    [GeneratedRegex(@"\A(?<kind>[^:]+): up to (?<number>[0-9]+) (?<key>.+)\z", RegexOptions.Singleline)]
    private static partial Regex LimitEntry();

    /// <summary>The accesses of one endpoint.</summary>
    private sealed class Entries
    {
        /// <summary>The accesses that must match word for word, in the form of the profile file.</summary>
        public HashSet<string> Accesses { get; } = new(StringComparer.Ordinal);

        /// <summary>The highest number of each limit.</summary>
        public Dictionary<(string Kind, string Key), long> Limits { get; } = [];

        /// <summary>Adds an entry of a profile file. Returns true when it is new, or a higher limit.</summary>
        public bool Add(string entry) =>
            TryReadLimit(entry, out var limit, out var number) ? Raise(limit, number) : Accesses.Add(entry);

        public bool Allows(string entry) =>
            TryReadLimit(entry, out var limit, out var number)
                ? Limits.TryGetValue(limit, out var known) && number <= known
                : Accesses.Contains(entry);

        public bool Raise((string Kind, string Key) limit, long number)
        {
            if (Limits.TryGetValue(limit, out var known) && number <= known)
            {
                return false;
            }

            Limits[limit] = number;
            return true;
        }

        private static bool TryReadLimit(string entry, out (string Kind, string Key) limit, out long number)
        {
            limit = default;
            number = 0;

            // Most entries are not limits, and this test is much cheaper than the full match.
            if (!entry.Contains(": up to ", StringComparison.Ordinal))
            {
                return false;
            }

            var match = LimitEntry().Match(entry);
            if (!match.Success
                || !long.TryParse(match.Groups["number"].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out number))
            {
                return false;
            }

            limit = (match.Groups["kind"].Value, match.Groups["key"].Value);
            return true;
        }
    }
}
