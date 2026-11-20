using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace BehaviorLock;

/// <summary>Finds the endpoints that the profile does not cover.</summary>
public static class EndpointCoverage
{
    /// <summary>
    /// Returns the keys of all mapped endpoints that are not in the profile.
    /// Use it in a test to fail the build when an endpoint has no recorded profile.
    /// </summary>
    public static IReadOnlyList<string> FindUnprofiled(IServiceProvider services)
    {
        var profile = services.GetRequiredService<EndpointProfile>();

        return services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .SelectMany(endpoint =>
                (endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? [])
                .Select(method => EndpointKey.Create(method, endpoint)))
            .Where(key => !profile.ContainsEndpoint(key))
            .Distinct()
            .Order(StringComparer.Ordinal)
            .ToList();
    }
}
