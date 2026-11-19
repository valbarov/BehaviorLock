using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BehaviorLock;

/// <summary>
/// Adds the accesses of the approved file to the loaded profile.
/// The approved file is written by a person, and the recorded profile by the tests.
/// </summary>
internal sealed class ApprovedAccesses(
    IOptions<BehaviorLockOptions> options,
    IHostEnvironment environment,
    ILogger<ApprovedAccesses> logger)
{
    public void AddTo(EndpointProfile profile)
    {
        // Learn mode saves the profile. Approved accesses must not get into the recorded file,
        // or nobody could see later that the tests do not cover them.
        if (options.Value.ApprovedPath is not { } path || options.Value.Mode is GuardMode.Off or GuardMode.Learn)
        {
            return;
        }

        var location = ProfileFile.Resolve(path, environment);
        var approved = ProfileFile.Read(location);

        if (approved is null)
        {
            logger.LogWarning("BehaviorLock found no approved file at {Location}. Only the profile is used", location);
            return;
        }

        var added = profile.Merge(approved);
        logger.LogInformation("BehaviorLock added {Count} approved accesses from {Location}", added, location);
    }
}
