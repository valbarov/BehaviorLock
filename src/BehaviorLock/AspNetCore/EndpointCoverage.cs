using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace BehaviorLock;

/// <summary>Finds the endpoints that the profile does not cover.</summary>
public static class EndpointCoverage
{
    /// <summary>
    /// Returns the keys of all mapped endpoints that are not in the profile.
    /// Use it in a test to fail the build when an endpoint has no recorded profile.
    /// <para>
    /// The guard writes the same endpoints to the log when the app starts in Audit or Enforce mode,
    /// but there it leaves out the endpoints with the mode Off.
    /// </para>
    /// </summary>
    public static IReadOnlyList<string> FindUnprofiled(IServiceProvider services) =>
        FindUnprofiled(
            services.GetRequiredService<EndpointDataSource>().Endpoints,
            services.GetRequiredService<EndpointProfile>(),
            withModeOff: true);

    /// <param name="endpoints">The mapped endpoints.</param>
    /// <param name="profile">The loaded profile.</param>
    /// <param name="withModeOff">
    /// False leaves out the endpoints that have the mode Off of their own. The guard never checks them,
    /// so they need no profile.
    /// </param>
    internal static IReadOnlyList<string> FindUnprofiled(
        IEnumerable<Endpoint> endpoints, EndpointProfile profile, bool withModeOff) => endpoints
        .OfType<RouteEndpoint>()
        .Where(endpoint => withModeOff || endpoint.Metadata.GetMetadata<IGuardModeMetadata>()?.Mode != GuardMode.Off)
        .SelectMany(endpoint =>
            (endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? [])
            .Select(method => EndpointKey.Create(method, endpoint)))
        .Where(key => !profile.ContainsEndpoint(key))
        .Distinct()
        .Order(StringComparer.Ordinal)
        .ToList();

    /// <summary>Joins the keys for a log line. A long list ends with the number of keys that are left out.</summary>
    internal static string Describe(IReadOnlyList<string> endpoints, int limit = 20) =>
        endpoints.Count <= limit
            ? string.Join(", ", endpoints)
            : $"{string.Join(", ", endpoints.Take(limit))} and {endpoints.Count - limit} more";
}
