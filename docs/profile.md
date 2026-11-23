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

## Check the profile in CI

The diff of the profile is only worth a review when the committed profile is the one that the code gives.
So let CI record the profile again, and fail the build when the result is not the committed file. Then no
pull request can change what an endpoint touches without a change of `behavior.lock.json` in the same
pull request.

For GitHub Actions:

```yaml
steps:
  - uses: actions/checkout@v7

  - uses: actions/setup-dotnet@v6
    with:
      dotnet-version: 11.0.x

  # The tests record the profile again.
  - name: Test
    run: dotnet test

  - name: The recorded profile is the committed profile
    shell: bash
    run: |
      profile=src/Shop.Api/behavior.lock.json

      if ! git ls-files --error-unmatch -- "$profile" > /dev/null 2>&1; then
        echo "::error::The profile $profile is not committed."
        exit 1
      fi

      if ! git diff --exit-code -- "$profile"; then
        echo "::error::The tests recorded another profile than the committed one. Review the change above. Then commit the new profile, or correct the code."
        exit 1
      fi
```

The check is plain git, so the same two commands work on every CI system.

When the check fails, run the tests on your machine and look at `git diff`. The change is either legal, then
commit the new profile and the reviewer sees it. Or it is a mistake, then correct the code.

Things to know:

- **The recording must start without the old profile.** `Learn` mode adds to a profile that is there. So
  delete the file before the recording, as
  [`ProfileFixture`](../tests/BehaviorLock.Tests/ProfileFixture.cs) does. Else an access that the code
  no longer makes stays in the profile, and the check cannot see it.
- **Tell git that the profile is text.** The guard writes the file with the line ends of the system. Add
  the line `behavior.lock.json text` to `.gitattributes`, or use `* text=auto`. Without it, a profile that
  was committed on Windows is another file for git than the same profile from a Linux build.
- **The recording must give the same profile each time.** Use fixed test data. The limits help here: the
  profile keeps them rounded up to 10, 100, 1000 and so on.
- **A new version of the database provider changes the profile.** The `$database` entry has the version, and
  the SQL can change too. The check then fails, and that is wanted: record again and commit.
- **Record with one target framework.** A test project with several target frameworks runs once for each,
  and each run can use another provider version. Let only one of them write the committed file.

This repository uses the same check for the profile of the sample; see
[ci.yml](../.github/workflows/ci.yml).

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
