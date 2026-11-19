using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;

namespace BehaviorLock;

/// <summary>
/// Opens a <see cref="GuardContext"/> for each routed request and turns a blocked access into a 403 response.
/// The mode of the request comes from the global mode and the <see cref="IGuardModeMetadata"/> of the endpoint.
/// Must run after routing.
/// </summary>
public sealed class GuardMiddleware(
    RequestDelegate next,
    IGuardContextAccessor contextAccessor,
    EndpointProfile profile,
    IOptions<BehaviorLockOptions> options)
{
    public async Task InvokeAsync(HttpContext http)
    {
        var global = options.Value.Mode;

        if (global == GuardMode.Off || http.GetEndpoint() is not RouteEndpoint routeEndpoint)
        {
            await next(http);
            return;
        }

        var mode = GuardModes.Effective(global, routeEndpoint.Metadata.GetMetadata<IGuardModeMetadata>()?.Mode);
        if (mode == GuardMode.Off)
        {
            await next(http);
            return;
        }

        var endpoint = EndpointKey.Create(http.Request.Method, routeEndpoint);
        var context = new GuardContext(endpoint, mode);
        contextAccessor.Current = context;

        if (mode == GuardMode.Learn)
        {
            profile.AddEndpoint(endpoint);
        }

        try
        {
            await next(http);
        }
        catch (Exception) when (context.Violation is not null)
        {
            // The guard blocked an access. The exception may be wrapped by other code.
        }

        if (context.Violation is null)
        {
            return;
        }

        if (http.Response.HasStarted)
        {
            http.Abort();
        }
        else
        {
            http.Response.Clear();
            http.Response.StatusCode = StatusCodes.Status403Forbidden;
        }
    }
}

public static class EndpointKey
{
    /// <summary>Returns for example <c>GET /products/{id}</c>, or null when no route matched.</summary>
    public static string? For(HttpContext http) =>
        http.GetEndpoint() is RouteEndpoint endpoint ? Create(http.Request.Method, endpoint) : null;

    public static string Create(string method, RouteEndpoint endpoint) =>
        $"{method.ToUpperInvariant()} {endpoint.RoutePattern.RawText}";
}
