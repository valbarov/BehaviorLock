using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SampleApi;

namespace BehaviorLock.LoadTest;

/// <summary>How the sample API is built for one measurement.</summary>
public enum Variant
{
    /// <summary>The package is not registered at all. This is the baseline.</summary>
    NoPackage,
    Off,
    Audit,
    Enforce,
}

/// <summary>The sample API on a real Kestrel port, with its own database file and an HTTP client for the load.</summary>
public sealed class ApiHost : IAsyncDisposable
{
    private readonly WebApplication _app;

    private ApiHost(Variant variant, WebApplication app, HttpClient client)
    {
        Variant = variant;
        _app = app;
        Client = client;
    }

    public Variant Variant { get; }

    public HttpClient Client { get; }

    public static async Task<ApiHost> StartAsync(Variant variant, string profilePath, string databasePath, int connections)
    {
        var guarded = variant != Variant.NoPackage;

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        if (guarded)
        {
            builder.Services.AddBehaviorLock(options =>
            {
                options.Mode = Enum.Parse<GuardMode>(variant.ToString());
                options.ProfilePath = profilePath;
            });
        }

        builder.Services.AddDbContext<ShopDb>((services, options) =>
        {
            options.UseSqlite($"Data Source={databasePath}");
            if (guarded)
            {
                options.UseBehaviorLock(services);
            }
        });

        // Outbound calls get an immediate answer, so the network is not part of the measurement.
        builder.Services.AddHttpClient();
        builder.Services.AddHttpClient<PaymentClient>();
        builder.Services.ConfigureHttpClientDefaults(http => http.ConfigurePrimaryHttpMessageHandler(() => new InstantHandler()));

        builder.Services.AddShopAuthentication();

        var app = builder.Build();

        using (var scope = app.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<ShopDb>().Database.EnsureCreated();
        }

        app.UseAuthentication();
        app.UseAuthorization();

        if (guarded)
        {
            app.UseBehaviorLock();
        }

        app.MapShopApi();
        await app.StartAsync();

        var client = new HttpClient(new SocketsHttpHandler { MaxConnectionsPerServer = connections })
        {
            BaseAddress = new Uri(app.Urls.First()),
        };

        return new ApiHost(variant, app, client);
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    private sealed class InstantHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));

        protected override void Dispose(bool disposing)
        {
            // The factory reuses this handler.
        }
    }
}
