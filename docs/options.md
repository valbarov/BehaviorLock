# Options

Back to the [overview](../README.md#documentation).

| Option | Default | Meaning |
|---|---|---|
| `Mode` | `Audit` | The global mode. It can change while the app runs. See [Modes](modes.md) |
| `ProfilePath` | `behavior.lock.json` | The recorded profile. See [Deploy the profile](profile.md#deploy-the-profile) |
| `ApprovedPath` | Not set | The file with reviewed accesses. See [Find the gaps in production](profile.md#find-the-gaps-in-production) |
| `PendingPath` | Not set | The file where the guard writes unknown accesses. Not set means that no file is written. It can have the placeholders `{machine}` and `{pid}`. See [Find the gaps in production](profile.md#find-the-gaps-in-production) |
| `PendingLimit` | `1000` | The largest number of accesses in the pending file |
| `CheckCallers` | `true` | Records and checks the kind of caller. See [Callers](checks.md#callers) |
| `CallerClaims` | Empty | More claim types that are part of the kind of caller, for example `scope`. See [Callers](checks.md#callers) |
| `CheckAllHttpClients` | `true` | Checks every `HttpClient` and each redirect hop. `false` checks only the clients of `IHttpClientFactory`. See [Outbound calls](checks.md#outbound-calls) |
| `CheckLimits` | `true` | Records and checks the rows and the response size. See [Limits](checks.md#limits) |
| `EnforceLimits` | `true` | `false` means that a number over a limit is reported, but never blocked. It can change while the app runs. See [Limits](checks.md#limits) |
| `SqlShapeCacheSize` | `1000` | The number of SQL command texts whose shapes the guard keeps. `0` turns the cache off. See [Performance](performance.md) |

A relative path is resolved against the content root of the app.
