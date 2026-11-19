namespace BehaviorLock;

/// <summary>The allowed resource accesses per endpoint. Thread-safe.</summary>
public sealed class EndpointProfile
{
    private readonly Dictionary<string, HashSet<string>> _endpoints = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();

    public EndpointProfile()
    {
    }

    public EndpointProfile(IReadOnlyDictionary<string, string[]> endpoints)
    {
        foreach (var (endpoint, accesses) in endpoints)
        {
            _endpoints[endpoint] = new HashSet<string>(accesses, StringComparer.Ordinal);
        }
    }

    /// <summary>Registers an endpoint, also when it touches no resources.</summary>
    public void AddEndpoint(string endpoint)
    {
        lock (_lock)
        {
            GetOrAdd(endpoint);
        }
    }

    public void Allow(string endpoint, ResourceAccess access)
    {
        lock (_lock)
        {
            GetOrAdd(endpoint).Add(access.ToString());
        }
    }

    public bool IsAllowed(string endpoint, ResourceAccess access)
    {
        lock (_lock)
        {
            return _endpoints.TryGetValue(endpoint, out var accesses) && accesses.Contains(access.ToString());
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

    /// <summary>The number of accesses of all endpoints together.</summary>
    public int AccessCount
    {
        get
        {
            lock (_lock)
            {
                return _endpoints.Values.Sum(accesses => accesses.Count);
            }
        }
    }

    /// <summary>Adds all endpoints and accesses of the other profile. Returns the number of new accesses.</summary>
    public int Merge(EndpointProfile other)
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
    public EndpointProfile Except(EndpointProfile other)
    {
        var allowed = other.ToDictionary();
        var rest = new Dictionary<string, string[]>(StringComparer.Ordinal);

        foreach (var (endpoint, accesses) in ToDictionary())
        {
            var left = allowed.TryGetValue(endpoint, out var known) ? accesses.Except(known, StringComparer.Ordinal).ToArray() : accesses;
            if (left.Length > 0)
            {
                rest[endpoint] = left;
            }
        }

        return new EndpointProfile(rest);
    }

    public bool ContainsEndpoint(string endpoint)
    {
        lock (_lock)
        {
            return _endpoints.ContainsKey(endpoint);
        }
    }

    /// <summary>A sorted copy, so that the saved file gives stable diffs.</summary>
    public SortedDictionary<string, string[]> ToDictionary()
    {
        lock (_lock)
        {
            var copy = new SortedDictionary<string, string[]>(StringComparer.Ordinal);
            foreach (var (endpoint, accesses) in _endpoints)
            {
                copy[endpoint] = accesses.Order(StringComparer.Ordinal).ToArray();
            }

            return copy;
        }
    }

    private HashSet<string> GetOrAdd(string endpoint)
    {
        if (!_endpoints.TryGetValue(endpoint, out var accesses))
        {
            _endpoints[endpoint] = accesses = new HashSet<string>(StringComparer.Ordinal);
        }

        return accesses;
    }
}
