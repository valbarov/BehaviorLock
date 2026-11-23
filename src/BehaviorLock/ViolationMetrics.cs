using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace BehaviorLock;

/// <summary>
/// Counts every violation in the counter <c>behaviorlock.violations</c> of the meter <c>BehaviorLock</c>.
/// A dashboard then shows how often each endpoint leaves its profile, for example to see when Audit mode
/// is quiet enough for Enforce mode.
/// <para>
/// The tags are the endpoint, the kind of the access and whether it was blocked. Each of them has few
/// values. The key of the access is not a tag: an attacker chooses it, so it has no end of values.
/// </para>
/// </summary>
internal sealed class ViolationMetrics : IViolationSink
{
    public const string MeterName = "BehaviorLock";
    public const string CounterName = "behaviorlock.violations";
    public const string EndpointTag = "behaviorlock.endpoint";
    public const string KindTag = "behaviorlock.kind";
    public const string BlockedTag = "behaviorlock.blocked";

    private readonly Counter<long> _violations;

    // The factory owns the meter and disposes it with the services of the app.
    public ViolationMetrics(IMeterFactory meters) =>
        _violations = meters.Create(MeterName).CreateCounter<long>(
            CounterName, unit: "{violation}", description: "Accesses that are not in the profile of their endpoint.");

    public void Report(GuardViolation violation)
    {
        // Without a listener this costs one check. The guard also calls it for each violation in Audit mode.
        if (!_violations.Enabled)
        {
            return;
        }

        _violations.Add(1, new TagList
        {
            { EndpointTag, violation.Endpoint },
            { KindTag, violation.Access.Kind },
            { BlockedTag, violation.Blocked },
        });
    }
}
