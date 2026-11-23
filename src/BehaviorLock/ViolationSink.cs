using Microsoft.Extensions.Logging;

namespace BehaviorLock;

/// <summary>Receives every violation. Register more sinks to send alerts to other systems.</summary>
public interface IViolationSink
{
    /// <summary>
    /// Called for each access that is not in the profile, before a blocked access throws. It runs on the
    /// thread of the request, so it must be fast and must not throw.
    /// </summary>
    void Report(GuardViolation violation);
}

internal sealed class LoggingViolationSink(ILogger<LoggingViolationSink> logger) : IViolationSink
{
    public void Report(GuardViolation violation)
    {
        var action = violation.Blocked ? "blocked" : "audit only";

        // A guard scope has no request.
        if (violation.RequestId is null)
        {
            logger.LogWarning(
                "BehaviorLock violation ({Action}): endpoint '{Endpoint}' attempted '{Access}'",
                action,
                violation.Endpoint,
                violation.Access);
            return;
        }

        // With the id, support can find the line for a user who got a 403.
        logger.LogWarning(
            "BehaviorLock violation ({Action}): endpoint '{Endpoint}' attempted '{Access}' in request {RequestId}",
            action,
            violation.Endpoint,
            violation.Access,
            violation.RequestId);
    }
}
