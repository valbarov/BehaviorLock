# Modes

Back to the [overview](../README.md#documentation).

| Mode | Access that is not in the profile |
|---|---|
| `Off` | Not checked |
| `Learn` | Added to the profile. The profile is saved when the host stops |
| `Audit` | Reported, but allowed. This is the default |
| `Enforce` | Reported and blocked. The response is 403 with an empty body |

Recommended rollout: record with tests, then run `Audit` in production with a pending file. When the pending
file stays empty, switch to `Enforce`. You can also switch one endpoint at a time.
See [Mode per endpoint](#mode-per-endpoint).

In `Enforce` mode an endpoint that has no profile and no approved accesses cannot touch anything.

## Change the mode while the app runs

A false positive in `Enforce` mode blocks a legal request. You can stop the blocking without a new deploy:
change `Mode` in the configuration of the running app, for example in `appsettings.json`.

```json
"BehaviorLock": {
  "Mode": "Audit"
}
```

- Register the guard with `AddBehaviorLock(configuration)`, as in the quick start. The guard then follows each
  reload of that configuration. ASP.NET Core reloads `appsettings.json` when the file changes. It reads
  environment variables only at the start.
- The change applies to the next access, also in a request or a background scope that is already running.
- Only `Off`, `Audit` and `Enforce` can change into each other. `Learn` must be set at the start, and an app in
  `Learn` mode stays there until it stops.
- A change that would have stopped the start is refused, for example `Enforce` without a profile. The mode
  stays as it is, and the log has an error with the reason.
- `Audit` does not change an endpoint that has its own mode `Enforce`. `Off` turns the guard off for every
  endpoint. See [Mode per endpoint](#mode-per-endpoint).
- Each change is logged as a warning, for example `BehaviorLock mode changed from Enforce to Audit`. Look for
  this line after you change the file. A value that is not a mode changes nothing and gives no such line.
- `EnforceLimits` follows a reload in the same way. See [Limits](checks.md#limits). Every other option is read once,
  at the start.

Who can write the configuration can turn the guard off. Protect it like the files of the app.

## Mode per endpoint

The global mode applies to every endpoint. You can give one endpoint or one route group its own mode.
For example, the global mode is `Audit` and the stable endpoints already use `Enforce`.
Or the global mode is `Enforce` and one complex search endpoint stays in `Audit`.

```csharp
// The global mode is Audit.
app.MapGet("/products/{id}", GetProduct).WithBehaviorLock(GuardMode.Enforce);

var orders = app.MapGroup("/orders").WithBehaviorLock(GuardMode.Enforce);
orders.MapPost("/", CreateOrder);

// The attribute works on minimal API handlers, MVC controllers and MVC actions.
[BehaviorLock(GuardMode.Enforce)]
public sealed class AccountController : ControllerBase { /* ... */ }
```

| Global mode | Mode that applies to an endpoint |
|---|---|
| `Off` | `Off` for every endpoint. This turns the guard off everywhere, whatever the endpoints say |
| `Learn` | `Learn` for every endpoint, so the profile covers all endpoints |
| `Audit` or `Enforce` | The mode of the endpoint if it has one, else the global mode. It can be stricter or weaker |

- Only `Off`, `Audit` and `Enforce` are legal for one endpoint. `Learn` throws an `ArgumentOutOfRangeException`,
  because `Learn` on one endpoint in production would allow everything there without a report.
- The mode of an endpoint beats the mode of its route group. The mode of an action beats the mode of its controller.
- An endpoint with the mode `Enforce` needs the profile, also when the global mode is `Audit`.
  See [Deploy the profile](profile.md#deploy-the-profile).
