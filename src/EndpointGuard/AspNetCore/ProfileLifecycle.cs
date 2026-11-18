using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EndpointGuard;

/// <summary>Logs the guard state on start. Saves the profile on shutdown in Learn mode.</summary>
internal sealed class ProfileLifecycle(
    EndpointProfile profile,
    IProfileStore store,
    IOptions<GuardOptions> options,
    ILogger<ProfileLifecycle> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "EndpointGuard mode is {Mode}; the profile has {Count} endpoints",
            options.Value.Mode,
            profile.ToDictionary().Count);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        if (options.Value.Mode == GuardMode.Learn)
        {
            store.Save(profile);
            logger.LogInformation("EndpointGuard saved the profile to {Path}", options.Value.ProfilePath);
        }

        return Task.CompletedTask;
    }
}
