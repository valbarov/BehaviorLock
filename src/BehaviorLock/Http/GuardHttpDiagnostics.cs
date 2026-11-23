using System.Diagnostics;
using System.Reflection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BehaviorLock;

/// <summary>
/// Checks the requests of every <see cref="HttpClient"/> of the process before they are sent, also of a
/// client that a package creates by itself. <see cref="GuardHttpHandler"/> sees only the clients of
/// <c>IHttpClientFactory</c>.
/// <para>
/// The .NET handlers (<see cref="SocketsHttpHandler"/> and <see cref="HttpClientHandler"/>) write an event
/// to a <see cref="DiagnosticListener"/> at the start of each request, and of each redirect hop. The
/// observer runs on the flow of the caller, so the guard context is there, and an exception of the observer
/// stops the request.
/// </para>
/// <para>
/// One side effect: the handler makes an <see cref="Activity"/> for a request that the guard wants to see.
/// So a request that has no activity around it, for example in a background job, now carries a
/// <c>traceparent</c> header. A request inside an ASP.NET Core request with logging on has it already.
/// </para>
/// </summary>
internal sealed class GuardHttpDiagnostics(
    IAccessGuard guard,
    LiveSettings settings,
    IOptions<BehaviorLockOptions> options,
    ILogger<GuardHttpDiagnostics> logger)
    : IHostedService, IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>, IDisposable
{
    /// <summary>
    /// The access that the guard has already checked for this request, in the form of <see cref="ResourceAccess.Key"/>.
    /// A redirect changes the address of the request, so the next hop has another access and is checked again.
    /// </summary>
    internal static readonly HttpRequestOptionsKey<string> Checked = new("BehaviorLock.Checked");

    private const string ListenerName = "HttpHandlerDiagnosticListener";
    private const string ActivityName = "System.Net.Http.HttpRequestOut";
    private const string StartName = "System.Net.Http.HttpRequestOut.Start";
    private const string PropagationSwitch = "System.Net.Http.EnableActivityPropagation";

    private readonly Lock _lock = new();
    private IDisposable? _allListeners;
    private IDisposable? _httpListener;
    private PropertyInfo? _requestProperty;
    private bool _cannotRead;
    private bool _stopped;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!options.Value.CheckAllHttpClients)
        {
            return Task.CompletedTask;
        }

        // Off mode listens to nothing. The listening starts when the mode changes while the app runs.
        settings.ModeChanged += ListenUnlessOff;
        ListenUnlessOff(settings.Mode);
        return Task.CompletedTask;
    }

    /// <summary>Starts the listening once. It then goes on in every mode: in Off mode the guard is not active.</summary>
    private void ListenUnlessOff(GuardMode mode)
    {
        if (mode == GuardMode.Off)
        {
            return;
        }

        lock (_lock)
        {
            if (_stopped || _allListeners is not null)
            {
                return;
            }

            if (EventIsOff())
            {
                logger.LogWarning(
                    "The .NET setting {Setting} is off, so the HTTP handlers write no event. " +
                    "BehaviorLock checks only the clients of IHttpClientFactory, and no redirect hop.",
                    PropagationSwitch);
            }

            _allListeners = DiagnosticListener.AllListeners.Subscribe(this);
        }
    }

    /// <summary>An app can turn the event off for the whole process, with a switch or an environment variable.</summary>
    private static bool EventIsOff()
    {
        if (AppContext.TryGetSwitch(PropagationSwitch, out var on))
        {
            return !on;
        }

        var variable = Environment.GetEnvironmentVariable("DOTNET_SYSTEM_NET_HTTP_ENABLEACTIVITYPROPAGATION");
        return variable is "0" || string.Equals(variable, "false", StringComparison.OrdinalIgnoreCase);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Dispose();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        settings.ModeChanged -= ListenUnlessOff;

        lock (_lock)
        {
            _stopped = true;
            _httpListener?.Dispose();
            _allListeners?.Dispose();
            _httpListener = null;
            _allListeners = null;
        }
    }

    void IObserver<DiagnosticListener>.OnNext(DiagnosticListener listener)
    {
        if (listener.Name != ListenerName)
        {
            return;
        }

        lock (_lock)
        {
            // Only the start of a request is needed, and only where the guard would check it.
            _httpListener ??= listener.Subscribe(this, (name, _, _) => name is ActivityName or StartName && guard.IsActive);
        }
    }

    void IObserver<KeyValuePair<string, object?>>.OnNext(KeyValuePair<string, object?> diagnostic)
    {
        if (diagnostic.Key != StartName || !guard.IsActive || RequestOf(diagnostic.Value) is not { RequestUri: { IsAbsoluteUri: true } uri } request)
        {
            return;
        }

        var access = ResourceAccess.Http(request.Method.Method, uri.GetLeftPart(UriPartial.Authority));

        // GuardHttpHandler has checked this address already. Without this, Audit mode would report it two times.
        if (request.Options.TryGetValue(Checked, out var known) && known == access.Key)
        {
            return;
        }

        request.Options.Set(Checked, access.Key);
        guard.Check(access);
    }

    void IObserver<DiagnosticListener>.OnCompleted()
    {
    }

    void IObserver<DiagnosticListener>.OnError(Exception error)
    {
    }

    void IObserver<KeyValuePair<string, object?>>.OnCompleted()
    {
    }

    void IObserver<KeyValuePair<string, object?>>.OnError(Exception error)
    {
    }

    /// <summary>The event carries an object of an internal type of the runtime, with the request in a property.</summary>
    private HttpRequestMessage? RequestOf(object? payload)
    {
        if (payload is null)
        {
            return null;
        }

        var property = _requestProperty;
        if (property is null || property.DeclaringType != payload.GetType())
        {
            property = payload.GetType().GetProperty("Request");
            _requestProperty = property;
        }

        if (property?.GetValue(payload) is HttpRequestMessage request)
        {
            return request;
        }

        // A later runtime could change the event. Then these requests pass without a check, so say it loudly.
        if (!_cannotRead)
        {
            _cannotRead = true;
            logger.LogError(
                "BehaviorLock cannot read the request from the event {Event} of this .NET runtime. " +
                "Only the clients of IHttpClientFactory are checked.",
                StartName);
        }

        return null;
    }
}
