using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;

namespace BehaviorLock;

/// <summary>
/// Opens a <see cref="GuardContext"/> for each routed request and turns a blocked access into a 403 response.
/// The mode of the request comes from the global mode and the <see cref="IGuardModeMetadata"/> of the endpoint.
/// A change of the global mode applies to the next access, also in a request that is already running.
/// It also checks the caller before the endpoint runs, and the size of the response body.
/// Must run after routing, and after authentication and authorization.
/// </summary>
internal sealed class GuardMiddleware(
    RequestDelegate next,
    IGuardContextAccessor contextAccessor,
    EndpointProfile profile,
    IAccessGuard guard,
    LiveSettings settings,
    IOptions<BehaviorLockOptions> options)
{
    // The number does not matter here: it only names the limit to look up.
    private static readonly ResourceAccess ResponseSizeLimit = ResourceAccess.ResponseSize(0);

    public async Task InvokeAsync(HttpContext http)
    {
        var global = settings.Mode;

        if (global == GuardMode.Off || http.GetEndpoint() is not RouteEndpoint routeEndpoint)
        {
            await next(http);
            return;
        }

        var ownMode = routeEndpoint.Metadata.GetMetadata<IGuardModeMetadata>()?.Mode;
        var mode = GuardModes.Effective(global, ownMode);
        if (mode == GuardMode.Off)
        {
            await next(http);
            return;
        }

        var endpoint = EndpointKey.Create(http.Request.Method, routeEndpoint);
        var context = new GuardContext(endpoint, ownMode) { RequestId = http.TraceIdentifier };
        contextAccessor.Current = context;

        if (mode == GuardMode.Learn)
        {
            profile.AddEndpoint(endpoint);
        }

        var serverBody = http.Features.GetRequiredFeature<IHttpResponseBodyFeature>();
        var body = CountResponseBody(http, context, mode, serverBody);

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
    private GuardedResponseBody? CountResponseBody(
        HttpContext http, GuardContext context, GuardMode mode, IHttpResponseBodyFeature serverBody)
    {
        if (!options.Value.CheckLimits)
        {
            return null;
        }

        var limit = profile.LimitOf(context.Endpoint, ResponseSizeLimit);
        if (limit is null && mode != GuardMode.Learn)
        {
            return null;
        }

        // Only a request that blocks limits stops a write. The others report the size when they have finished.
        var stopsWrites = mode == GuardMode.Enforce && settings.EnforceLimits;
        var body = new GuardedResponseBody(serverBody, guard, context, stopsWrites ? limit : null);
        http.Features.Set<IHttpResponseBodyFeature>(body);
        return body;
    }
}

internal static class EndpointKey
{
    /// <summary>Returns for example <c>GET /products/{id}</c>, or null when no route matched.</summary>
    public static string? For(HttpContext http) =>
        http.GetEndpoint() is RouteEndpoint endpoint ? Create(http.Request.Method, endpoint) : null;

    public static string Create(string method, RouteEndpoint endpoint) =>
        $"{method.ToUpperInvariant()} {endpoint.RoutePattern.RawText}";
}
