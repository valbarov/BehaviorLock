using Microsoft.Extensions.Http;

namespace BehaviorLock;

/// <summary>Checks every outbound HTTP call before it is sent. The access key is method + scheme + host + port.</summary>
public sealed class GuardHttpHandler(IAccessGuard guard) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri is { IsAbsoluteUri: true } uri)
        {
            guard.Check(ResourceAccess.Http(request.Method.Method, uri.GetLeftPart(UriPartial.Authority)));
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
