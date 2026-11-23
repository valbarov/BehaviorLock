# The profile

Back to the [overview](../README.md#documentation).

## Record the profile from tests

The profile comes from your integration tests, not from manual use. The test project in this repository shows the pattern:

- [`LegalRequests`](../tests/BehaviorLock.Tests/LegalRequests.cs) lists one legal request per legal path.
- [`ProfileFixture`](../tests/BehaviorLock.Tests/ProfileFixture.cs) deletes the old profile, replays the legal
  requests in `Learn` mode and saves the new profile. This happens on every test run.
- The same requests must then pass in `Enforce` mode.
- `EndpointCoverage.FindUnprofiled` returns the endpoints that have no profile, so a test can fail the build.

The guard also names these endpoints in a log warning when the app starts in `Audit` or `Enforce` mode. So you
see the gap before the first 403. An endpoint with the mode `Off` is left out there, because the guard never
checks it.

```
BehaviorLock: the profile at /app/behavior.lock.json does not have 2 of the endpoints of the app: GET /reports, POST /orders/{id}/refund. Enforce mode blocks every SQL command and every outbound call of such an endpoint. Add a legal request for each of them to the recording
```

One request per endpoint is not enough. Each legal path that touches different resources needs its own
request. For example, `GET /products?sort=Price` and `GET /products?sort=Name` give two SQL shapes.

## Deploy the profile

The profile file must be deployed with the app. A relative `ProfilePath` is resolved against the content
root of the app, the same directory that holds `appsettings.json`. The current directory is not used.

A missing or empty profile allows nothing, so the guard makes it visible at startup:

| Mode | Profile is missing or empty |
|---|---|
| `Enforce` | The app does not start. The error names the full path of the profile |
| `Audit` | The app starts and logs a warning. It does not start when an endpoint has its own mode `Enforce` |
| `Learn`, `Off` | The app starts |

### Record with the database of production

Each database provider writes its own SQL. A profile that was recorded with SQLite matches nothing on
PostgreSQL or SQL Server. So `Learn` mode writes the provider and its version into the profile, and the
other modes compare them with the provider that the app uses:

| The app uses | `Enforce` | `Audit` |
|---|---|---|
| Another provider | The app does not start. The error names both providers | The app starts and logs an error |
| Another version of the same provider | The app starts and logs a warning | The app starts and logs a warning |
| The same provider and version | The app starts | The app starts |

Run the recording tests against the database that production uses, for example in a container.
A test database of another kind gives a profile that production cannot use.

- The check at startup covers the contexts that are registered with `AddDbContext`. Any other context is
  checked when its first command runs. A wrong provider there gives an error in the log, and its accesses
  are reported or blocked like every unknown access.
- With the global mode `Audit`, the app does not start when an endpoint has its own mode `Enforce` and the
  provider is another one.
- A profile without a `$database` entry is not checked. This is the case for a profile from an older
  version, and for a recording that made no SQL access.

## Find the gaps in production

Tests never cover every legal path. The pending file shows what they miss, before `Enforce` mode blocks a legal user.

```json
"BehaviorLock": {
  "Mode": "Audit",
  "ProfilePath": "behavior.lock.json",
  "ApprovedPath": "behavior.lock.approved.json",
  "PendingPath": "/var/lib/shop/behavior.lock.pending.json"
}
```

1. **Collect.** In `Audit` and `Enforce` mode the guard writes every access that is not in the profile to the
   pending file. The file is written at most every 10 seconds, and when the host stops.
2. **Review.** A person reads each entry. An entry is a legal path that the tests do not cover, or it is an
   attack. Do not accept an entry that you cannot explain.
3. **Accept.** For a legal entry, add a legal request to the tests. The entry then moves into the recorded
   profile. When a test is not possible soon, copy the entry to the approved file.
4. **Deploy.** Commit the change, so the pull request shows it. The guard allows the accesses of the profile
   and of the approved file. Entries that are allowed now leave the pending file at the next start.

The three files have the same format:

| File | Written by | In git | Content |
|---|---|---|---|
| `ProfilePath` | The tests, in `Learn` mode | Yes | What the code does on the tested paths |
| `ApprovedPath` | A person | Yes | Reviewed accesses that no test covers |
| `PendingPath` | The guard, in production | No | Accesses that wait for a review |

Things to know:

- The pending file also collects attack attempts. It contains text that an attacker chose, for example SQL
  comments. Never copy it to the approved file without a review.
- The pending file holds at most `PendingLimit` accesses (default 1000), because each attack attempt can
  give a new access. Accesses over the limit are still reported, but not written.
- A limit has one entry in the pending file, with the highest number that a request needed.
  See [Limits](checks.md#limits).
- `Enforce` mode stops a request at its first unknown access, so the pending file gets one entry per
  request. `Audit` mode lets the request finish and writes all its unknown accesses.
- Each instance of the app writes its own pending file. Give each instance its own path. When the instances
  share one directory, use a placeholder in `PendingPath`, for example
  `/var/lib/shop/behavior.lock.pending.{machine}.json`:

  | Placeholder | Value | Use it for |
  |---|---|---|
  | `{machine}` | The name of the machine. In a container this is the name of the container or pod | Containers that write to one shared volume |
  | `{pid}` | The id of the process. In a container it is often `1` for every instance | Several processes on one machine |

  A new container or a new process then starts a new file, and the old files stay. Review them and delete
  them, so that the directory does not grow without end.
- `Learn` mode ignores the approved file, so the recording never copies approved accesses into the profile.
- A missing approved file gives a warning at startup. The app starts, and only the profile is used.
