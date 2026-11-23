# Extension points

Back to the [overview](../README.md#documentation).

| Interface | Purpose | Default |
|---|---|---|
| `IViolationSink` | Receives violations | A sink that writes a log warning, and a sink that counts; see [Metrics](monitoring.md#metrics) |
| `IProfileStore` | Loads and saves the recorded profile with its `Databases`, and names its location | A JSON file at `ProfilePath` |
| `ISqlNormalizer` | Reduces SQL to its shape, with the rules of a `SqlDialect`. The result must depend only on the text and the dialect, because the guard keeps it | The scanner of the package; see [SQL dialects](checks.md#sql-dialects) |
| `IAccessGuard` | Checks one access. Call it from your own code to guard other resources. An access with `UpTo` is a limit | The guard of the package |
| `IGuardModeMetadata` | Endpoint metadata that sets the mode of one endpoint | `BehaviorLockAttribute` |

A sink of your own is added to the other sinks. Register your own `IProfileStore`, `ISqlNormalizer` or
`IAccessGuard` to replace the default. The classes behind the defaults are internal.

`GuardHttpHandler` is a public `DelegatingHandler` that checks each call. Put it in front of the primary handler
of a client that the guard does not cover by itself; see [Outbound calls](checks.md#outbound-calls).
