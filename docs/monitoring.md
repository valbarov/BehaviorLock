# Alerts and metrics

Back to the [overview](../README.md#documentation).

## Alerts

Every violation goes to all registered `IViolationSink` services. The default sink writes a log warning:

```
BehaviorLock violation (blocked): endpoint 'GET /products' attempted 'sql: SELECT * FROM Users' in request 0HNG5V2L8QK3T:00000002
```

The id at the end is `HttpContext.TraceIdentifier`. ASP.NET Core adds the same id as `RequestId` to the log
scope of the request. So when a user got a 403, search the log for the id of that request to see why.
A violation in a [background job](checks.md#background-jobs) has no request and no id.

Add your own sink to send alerts to another system:

```csharp
public sealed class PagerSink : IViolationSink
{
    public void Report(GuardViolation violation)
    {
        // violation.Endpoint, violation.Access, violation.Blocked, violation.RequestId
    }
}

builder.Services.AddSingleton<IViolationSink, PagerSink>();
```

## Metrics

The guard counts every violation with `System.Diagnostics.Metrics`. A dashboard then shows how often each
endpoint leaves its profile. Use it to see when `Audit` mode is quiet enough for `Enforce` mode: the count of
an endpoint stays at zero while it gets its normal traffic.

| | |
|---|---|
| Meter | `BehaviorLock` |
| Counter | `behaviorlock.violations` |
| Tag `behaviorlock.endpoint` | The endpoint key or the name of the scope, for example `GET /products/{id}` |
| Tag `behaviorlock.kind` | The kind of the access: `sql`, `http`, `caller`, `rows` or `response` |
| Tag `behaviorlock.blocked` | `true` when the access was blocked, `false` when it was only reported |

With OpenTelemetry, add the meter by its name:

```csharp
builder.Services.AddOpenTelemetry().WithMetrics(metrics => metrics.AddMeter("BehaviorLock"));
```

To watch the counter of a running app from a terminal:

```
dotnet-counters monitor --name SampleApi --counters BehaviorLock
```

The SQL shape or the host of a violation is not a tag, because an attacker chooses it and it has no end of
values. It is in the log and in the pending file. An access of your own kind through `IAccessGuard` is
counted with that kind.
