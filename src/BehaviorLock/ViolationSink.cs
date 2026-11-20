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
    public void Report(GuardViolation violation) =>
        logger.LogWarning(
            "BehaviorLock violation ({Action}): endpoint '{Endpoint}' attempted '{Access}'",
            violation.Blocked ? "blocked" : "audit only",
            violation.Endpoint,
            violation.Access);
}
