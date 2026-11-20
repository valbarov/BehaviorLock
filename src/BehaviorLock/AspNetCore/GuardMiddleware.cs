using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;

namespace BehaviorLock;

/// <summary>
/// Opens a <see cref="GuardContext"/> for each routed request and turns a blocked access into a 403 response.
/// The mode of the request comes from the global mode and the <see cref="IGuardModeMetadata"/> of the endpoint.
/// It also checks the caller before the endpoint runs, and the size of the response body.
/// Must run after routing, and after authentication and authorization.
/// </summary>
public sealed class GuardMiddleware(
    RequestDelegate next,
    IGuardContextAccessor contextAccessor,
    EndpointProfile profile,
    IAccessGuard guard,
    IOptions<BehaviorLockOptions> options)
{
    // The number does not matter here: it only names the limit to look up.
    private static readonly ResourceAccess ResponseSizeLimit = ResourceAccess.ResponseSize(0);

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

        var serverBody = http.Features.GetRequiredFeature<IHttpResponseBodyFeature>();
        var body = CountResponseBody(http, context, serverBody);

        try
        {
            // A caller that the profile does not know is stopped here, before the endpoint runs.
            if (options.Value.CheckCallers)
            {
                guard.Check(ResourceAccess.Caller(http.User, options.Value.CallerClaims));
            }

            await next(http);

            if (body is not null)
            {
                await body.FinishAsync();
                guard.Check(ResourceAccess.ResponseSize(body.Written));
            }
        }
        catch (Exception) when (context.Violation is not null)
        {
            // The guard blocked an access. The exception may be wrapped by other code.
        }
        finally
        {
            if (body is not null)
            {
                http.Features.Set(serverBody);
            }
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

    /// <summary>
    /// Puts a counter in front of the response body of the server. Returns null when the size is not needed:
    /// limits are off, or the profile has no size limit for this endpoint and the mode is not Learn.
    /// </summary>
    private GuardedResponseBody? CountResponseBody(HttpContext http, GuardContext context, IHttpResponseBodyFeature serverBody)
    {
        if (!options.Value.CheckLimits)
        {
            return null;
        }

        var limit = profile.LimitOf(context.Endpoint, ResponseSizeLimit);
        if (limit is null && context.Mode != GuardMode.Learn)
        {
            return null;
        }

        // Only a request that blocks limits stops a write. The others report the size when they have finished.
        var stopsWrites = context.Mode == GuardMode.Enforce && options.Value.EnforceLimits;
        var body = new GuardedResponseBody(serverBody, guard, context, stopsWrites ? limit : null);
        http.Features.Set<IHttpResponseBodyFeature>(body);
        return body;
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
