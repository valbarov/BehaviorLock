using Microsoft.Extensions.Logging;

namespace BehaviorLock;

/// <summary>Receives every violation. Register more sinks to send alerts to other systems.</summary>
public interface IViolationSink
{
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
