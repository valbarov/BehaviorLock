using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Options;

namespace BehaviorLock;

public static class BehaviorLockExtensions
{
    /// <summary>
    /// Registers the guard. Outbound calls of every <c>HttpClient</c> are covered; see
    /// <see cref="BehaviorLockOptions.CheckAllHttpClients"/>.
    /// </summary>
    public static IServiceCollection AddBehaviorLock(
        this IServiceCollection services, Action<BehaviorLockOptions>? configure = null)
    {
        var options = services.AddOptions<BehaviorLockOptions>();
        if (configure is not null)
        {
            options.Configure(configure);
        }

        services.TryAddSingleton<IGuardContextAccessor, GuardContextAccessor>();
        services.TryAddSingleton<IProfileStore, JsonFileProfileStore>();
        services.TryAddSingleton<ApprovedAccesses>();
        services.TryAddSingleton(provider =>
        {
            var profile = provider.GetRequiredService<IProfileStore>().Load();
            provider.GetRequiredService<ApprovedAccesses>().AddTo(profile);
            return profile;
        });
        services.TryAddSingleton<ISqlNormalizer, SqlNormalizer>();
        services.TryAddSingleton<IAccessGuard, AccessGuard>();
        services.TryAddSingleton(provider => new GuardScopes(
            provider.GetRequiredService<IGuardContextAccessor>(),
            provider.GetRequiredService<EndpointProfile>(),
            provider.GetRequiredService<IOptions<BehaviorLockOptions>>()));
        services.TryAddSingleton<DatabaseCheck>();
        services.TryAddSingleton<GuardDbCommandInterceptor>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IViolationSink, LoggingViolationSink>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHttpMessageHandlerBuilderFilter, GuardHttpHandlerFilter>());
        services.AddHostedService<ProfileLifecycle>();
        services.AddHostedService<GuardHttpDiagnostics>();

        // One instance collects the pending accesses as a sink and writes them as a hosted service.
        services.TryAddSingleton<PendingAccesses>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IViolationSink, PendingAccesses>(
            provider => provider.GetRequiredService<PendingAccesses>()));
        services.AddHostedService(provider => provider.GetRequiredService<PendingAccesses>());

        return services;
    }

    /// <summary>Adds the guard middleware. Call it after <c>UseRouting</c> and before the endpoints run.</summary>
    public static IApplicationBuilder UseBehaviorLock(this IApplicationBuilder app) =>
        app.UseMiddleware<GuardMiddleware>();

    /// <summary>Makes the guard check every command of this <c>DbContext</c>.</summary>
    public static DbContextOptionsBuilder UseBehaviorLock(
        this DbContextOptionsBuilder builder, IServiceProvider services) =>
        builder.AddInterceptors(services.GetRequiredService<GuardDbCommandInterceptor>());

    /// <summary>
    /// Sets the guard mode of this endpoint, or of every endpoint in this route group.
    /// Only Off, Audit and Enforce are legal. The global mode Off or Learn still applies to every endpoint.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The mode is Learn.</exception>
    public static TBuilder WithBehaviorLock<TBuilder>(this TBuilder builder, GuardMode mode)
        where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(new BehaviorLockAttribute(mode));
}
