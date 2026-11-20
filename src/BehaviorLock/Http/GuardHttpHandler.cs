using Microsoft.Extensions.Http;

namespace BehaviorLock;

/// <summary>
/// Checks every outbound HTTP call of an <c>IHttpClientFactory</c> client before it is sent.
/// The access key is method + scheme + host + port.
/// <para>
/// The guard also checks the clients that the factory did not create, and each redirect hop, in another
/// way; see <see cref="BehaviorLockOptions.CheckAllHttpClients"/>. This handler is still needed for a
/// client with a primary handler that is not from .NET, because such a handler does not write the event.
/// </para>
/// </summary>
public sealed class GuardHttpHandler(IAccessGuard guard) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (guard.IsActive && request.RequestUri is { IsAbsoluteUri: true } uri)
        {
            var access = ResourceAccess.Http(request.Method.Method, uri.GetLeftPart(UriPartial.Authority));

            // Tells the second check that this address is done, so that it is not reported two times.
            request.Options.Set(GuardHttpDiagnostics.Checked, access.Key);
            guard.Check(access);
        }

        return base.SendAsync(request, cancellationToken);
    }
}

/// <summary>Adds <see cref="GuardHttpHandler"/> to every client that <c>IHttpClientFactory</c> creates.</summary>
internal sealed class GuardHttpHandlerFilter(IAccessGuard guard) : IHttpMessageHandlerBuilderFilter
{
    public Action<HttpMessageHandlerBuilder> Configure(Action<HttpMessageHandlerBuilder> next) => builder =>
    {
        next(builder);
        builder.AdditionalHandlers.Add(new GuardHttpHandler(guard));
    };
}
