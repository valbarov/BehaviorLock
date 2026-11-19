# BehaviorLock

Active security for ASP.NET Core APIs. BehaviorLock records what each endpoint does when it handles legal
requests. In production it blocks every request that makes an endpoint do something else.

It does not look for known attack patterns. It only knows the legal behavior, so it also stops attacks and
bugs that nobody has seen before, as long as they change what the endpoint touches.

> Status: MVP. It targets .NET 11 and covers EF Core and `IHttpClientFactory`. Read [Limits](#limits) before you use it.

## How it works

1. **Record.** Your tests send one legal request per legal path. In `Learn` mode the guard writes down every
   resource access of each endpoint. The result is the *profile*, stored in `behavior.lock.json`.
   It works like a package lock file, but it locks behavior instead of versions.
2. **Review.** You commit the profile. When an endpoint starts to touch something new, the profile diff shows it
   in the pull request.
3. **Enforce.** In production the guard checks each access *before* it runs. An access that is not in the
   profile never runs. The request gets a 403 and an alert is raised.

An access is one of:

| Kind | What is recorded | Example |
|---|---|---|
| `sql` | The statement shape: the SQL text with all literals and parameters replaced by `?` | `SELECT * FROM Products WHERE Name LIKE ?` |
| `http` | Method, scheme, host and port of an outbound call | `GET https://hooks.example.com` |

A profile looks like this (see the full one in [samples/SampleApi/behavior.lock.json](samples/SampleApi/behavior.lock.json)):

```json
{
  "GET /products/search": [
    "sql: SELECT * FROM Products WHERE Name LIKE ?"
  ],
  "POST /orders": [
    "http: POST https://payments.example.com",
    "sql: INSERT INTO \"Orders\" (\"Total\", \"UserId\") VALUES (?) RETURNING \"Id\""
  ]
}
```

## Quick start

```csharp
using BehaviorLock;

var builder = WebApplication.CreateBuilder(args);

// 1. Register the guard. This also covers all IHttpClientFactory clients.
builder.Services.AddBehaviorLock(options => builder.Configuration.GetSection("BehaviorLock").Bind(options));

// 2. Let the guard check the commands of your DbContext.
builder.Services.AddDbContext<ShopDb>((services, options) => options
    .UseSqlite(connectionString)
    .UseBehaviorLock(services));

var app = builder.Build();

// 3. Add the middleware after routing and before the endpoints.
app.UseBehaviorLock();
```

```json
"BehaviorLock": {
  "Mode": "Enforce",
  "ProfilePath": "behavior.lock.json"
}
```

## Modes

| Mode | Access that is not in the profile |
|---|---|
| `Off` | Not checked |
| `Learn` | Added to the profile. The profile is saved when the host stops |
| `Audit` | Reported, but allowed. This is the default |
| `Enforce` | Reported and blocked. The response is 403 with an empty body |

Recommended rollout: record with tests, run `Audit` in production until the alerts stay quiet, then switch to `Enforce`.
You can also switch one endpoint at a time. See [Mode per endpoint](#mode-per-endpoint).

In `Enforce` mode an endpoint that has no profile cannot touch anything.

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
  See [Deploy the profile](#deploy-the-profile).

## Deploy the profile

The profile file must be deployed with the app. A relative `ProfilePath` is resolved against the content
root of the app, the same directory that holds `appsettings.json`. The current directory is not used.

A missing or empty profile allows nothing, so the guard makes it visible at startup:

| Mode | Profile is missing or empty |
|---|---|
| `Enforce` | The app does not start. The error names the full path of the profile |
| `Audit` | The app starts and logs a warning. It does not start when an endpoint has its own mode `Enforce` |
| `Learn`, `Off` | The app starts |

## Record the profile from tests

The profile comes from your integration tests, not from manual use. The test project in this repository shows the pattern:

- [`LegalRequests`](tests/BehaviorLock.Tests/LegalRequests.cs) lists one legal request per legal path.
- [`ProfileFixture`](tests/BehaviorLock.Tests/ProfileFixture.cs) deletes the old profile, replays the legal
  requests in `Learn` mode and saves the new profile. This happens on every test run.
- The same requests must then pass in `Enforce` mode.
- `EndpointCoverage.FindUnprofiled` returns the endpoints that have no profile, so a test can fail the build.

One request per endpoint is not enough. Each legal path that touches different resources needs its own
request. For example, `GET /products?sort=Price` and `GET /products?sort=Name` give two SQL shapes.

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
- Each instance of the app writes its own pending file. Give each instance its own path.
- `Learn` mode ignores the approved file, so the recording never copies approved accesses into the profile.
- A missing approved file gives a warning at startup. The app starts, and only the profile is used.

## Alerts

Every violation goes to all registered `IViolationSink` services. The default sink writes a log warning.
Add your own sink to send alerts to another system:

```csharp
public sealed class PagerSink : IViolationSink
{
    public void Report(GuardViolation violation) { /* violation.Endpoint, violation.Access, violation.Blocked */ }
}

builder.Services.AddSingleton<IViolationSink, PagerSink>();
```

## What the sample shows

[samples/SampleApi](samples/SampleApi) is a small shop API with deliberate weaknesses. The tests run each
attack twice: with the guard off, to show the harm, and in `Enforce` mode, to show that it is stopped.

| Weakness | Attack | Harm without the guard | What the guard sees |
|---|---|---|---|
| SQL injection in a string | `UNION SELECT ... FROM Users` | User emails leak | New SQL shape |
| SQL injection in a string | `'; DELETE FROM Users; --` | All users are deleted | A second statement |
| SQL injection in login | `alice@example.com' --` | Login without the password | New SQL shape |
| SQL injection in `ORDER BY` | `CASE WHEN (SELECT ...) THEN Name ELSE Price END` | One bit of the `Users` table leaks per request | New SQL shape |
| Mass assignment | `PATCH /users/1` with `"IsAdmin": true` | The user becomes an admin | `UPDATE` sets a new column |
| Plain bug | `DELETE /orders/0` | Every order is deleted | `DELETE` without `WHERE` |
| Forgotten debug switch | `GET /orders/1?debug=true` | Passwords leak | The endpoint reads a new table |
| SSRF | `url=http://169.254.169.254/...` | The server calls an internal address | New outbound host |
| SSRF | Other port on a legal host | The server calls an internal service | New outbound port |
| Compromised dependency | An order with a high total | The order is copied to a foreign host | New outbound host |

Two attacks are **not** stopped. [`KnownLimitTests`](tests/BehaviorLock.Tests/KnownLimitTests.cs) keeps them visible:

- **IDOR.** Reading the order of another user has the same SQL shape as a legal read.
- **Injection that keeps the shape.** A `%` wildcard in a `LIKE` value changes the result, but not the statement.

## Limits

- **Attacks inside the profile pass.** Broken authorization, business logic errors, XSS and DoS do not change
  what an endpoint touches.
- **Only EF Core and `IHttpClientFactory` are covered.** Dapper, raw ADO.NET, a directly created `HttpClient`,
  file access and process start are not checked.
- **HTTP redirects are not checked.** A legal host that redirects to another host passes.
- **Code outside a request is not checked**, for example startup code and background jobs.
- **No request-wide rollback.** A blocked access never runs, but earlier writes of the same request stay,
  unless the app uses its own transaction.
- **The profile records what the code does, not what it should do.** If a legal request triggers a harmful
  path during recording, that path becomes legal. Review the profile diff.
- **Missing test coverage blocks legal traffic.** A legal path that no test covers gets a 403 in `Enforce` mode.
  Run `Audit` mode with a pending file first; see [Find the gaps in production](#find-the-gaps-in-production).
- **The guard runs inside the app process.** An attacker with full code execution can turn it off.
- **The SQL normalizer is a small scanner, not a parser.** It is tested with SQLite only.

## Extension points

| Interface | Purpose | Default |
|---|---|---|
| `IViolationSink` | Receives violations | `LoggingViolationSink` |
| `IProfileStore` | Loads and saves the profile | `JsonFileProfileStore` |
| `ISqlNormalizer` | Reduces SQL to its shape | `SqlNormalizer` |
| `IAccessGuard` | Checks one access. Call it from your own code to guard other resources | `AccessGuard` |

## Repository layout

```
src/BehaviorLock            The library
samples/SampleApi            Shop API with deliberate weaknesses, plus its recorded profile
tests/BehaviorLock.Tests    Unit tests, legal requests, attacks
perf/BehaviorLock.LoadTest   Load test that compares the API with and without the guard
```

## Build and test

```
dotnet test
```

The test run rewrites `samples/SampleApi/behavior.lock.json`. A diff in that file after a test run
means the behavior of an endpoint changed.

## Performance

[perf/BehaviorLock.LoadTest](perf/BehaviorLock.LoadTest) measures what the guard costs. It starts the sample
API four ways on real Kestrel ports (without the package, and in `Off`, `Audit` and `Enforce` mode) and
sends the same load to each one.

```
dotnet run -c Release --project perf/BehaviorLock.LoadTest
dotnet run -c Release --project perf/BehaviorLock.LoadTest -- --filter preview --rounds 8
```

Results from one machine (16 logical cores, Windows 11, .NET 11, local SQLite file, 16 connections,
8 rounds of 4 s). Throughput in `Enforce` mode, compared with the same API without the package:

| Scenario | Requests/s without the package | Change in `Enforce` mode |
|---|---:|---|
| No resource access | 215,000 | No difference found |
| One EF Core query | 53,000 | About 2% to 3% lower |
| One raw SQL query | 56,000 | Between 0% and 2% lower |
| EF Core query + `UPDATE`, one connection | 1,200 | No clear difference (noise is ±4%) |
| One outbound HTTP call | 213,000 | About 1% lower |
| SQL injection, blocked with a 403 | 54,000 | About 2% higher: blocking costs less than running the attack query |

The latency percentiles (p50, p95, p99) differ by 0.04 ms or less in every scenario.

The guard's own work per SQL command is small: 0.1 to 0.5 µs to normalize the statement and 0.1 µs to check
it, with about 770 bytes allocated.

How to read these numbers:

- A local SQLite file answers in about 0.2 ms. That makes the relative cost of the guard as high as it gets.
  A database on the network is slower, so the same cost is a smaller share of the request.
- In `Off` mode, and for code outside a request, the guard skips its work: the SQL is not normalized and
  the outbound call is not inspected. Only the call into the EF Core interceptor and the HTTP handler remains.
- The load client runs in the same process as the servers, and the machine speed changes during a run.
  The test compares each variant with the baseline run of the same round to reduce that effect.
  Differences below about 1% are not reliable.

## License

BehaviorLock is open source under the [Apache License 2.0](LICENSE).

Copyright 2026 Vadim Albarov
