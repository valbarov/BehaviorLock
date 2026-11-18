using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http;

namespace BehaviorLock;

public static class BehaviorLockExtensions
{
    /// <summary>Registers the guard. Outbound calls of all <c>IHttpClientFactory</c> clients are covered.</summary>
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
        services.TryAddSingleton(provider => provider.GetRequiredService<IProfileStore>().Load());
        services.TryAddSingleton<ISqlNormalizer, SqlNormalizer>();
        services.TryAddSingleton<IAccessGuard, AccessGuard>();
        services.TryAddSingleton<GuardDbCommandInterceptor>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IViolationSink, LoggingViolationSink>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHttpMessageHandlerBuilderFilter, GuardHttpHandlerFilter>());
        services.AddHostedService<ProfileLifecycle>();

        return services;
    }

    /// <summary>Adds the guard middleware. Call it after <c>UseRouting</c> and before the endpoints run.</summary>
    public static IApplicationBuilder UseBehaviorLock(this IApplicationBuilder app) =>
        app.UseMiddleware<GuardMiddleware>();

    /// <summary>Makes the guard check every command of this <c>DbContext</c>.</summary>
    public static DbContextOptionsBuilder UseBehaviorLock(
        this DbContextOptionsBuilder builder, IServiceProvider services) =>
        builder.AddInterceptors(services.GetRequiredService<GuardDbCommandInterceptor>());
}
