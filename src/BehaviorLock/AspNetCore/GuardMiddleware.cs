using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;

namespace BehaviorLock;

/// <summary>
/// Opens a <see cref="GuardContext"/> for each routed request and turns a blocked access into a 403 response.
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
        var mode = options.Value.Mode;
        var endpoint = EndpointKey.For(http);

        if (mode == GuardMode.Off || endpoint is null)
        {
            await next(http);
            return;
        }

        var context = new GuardContext(endpoint);
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
