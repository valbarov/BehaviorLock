# BehaviorLock

[![CI](https://github.com/valbarov/BehaviorLock/actions/workflows/ci.yml/badge.svg)](https://github.com/valbarov/BehaviorLock/actions/workflows/ci.yml)

Active security for ASP.NET Core APIs. BehaviorLock records what each endpoint does when it handles legal
requests. In production it blocks every request that makes an endpoint do something else.

It does not look for known attack patterns. It only knows the legal behavior, so it also stops attacks and
bugs that nobody has seen before, as long as they change what the endpoint touches, who calls it, or how
much it returns.

> Status: MVP. It targets .NET 11 and covers EF Core and `HttpClient`. Read [Known limits](#known-limits) before you use it.

## How it works

1. **Record.** Your tests send one legal request per legal path. In `Learn` mode the guard writes down every
   resource access of each endpoint. The result is the *profile*, stored in `behavior.lock.json`.
   It works like a package lock file, but it locks behavior instead of versions.
2. **Review.** You commit the profile. When an endpoint starts to touch something new, the profile diff shows it
   in the pull request.
3. **Enforce.** In production the guard checks each access *before* it runs. An access that is not in the
   profile never runs. The request gets a 403 and an alert is raised. Only the row limit is checked after
   the command has run; see [Limits](#limits).
4. **Close the gaps.** Tests never cover every legal path. In production the guard writes each unknown access
   to a pending file. A person reviews it and accepts the legal ones.
   See [Find the gaps in production](#find-the-gaps-in-production).

You do not have to enforce everything at once. Each endpoint can have its own mode.
See [Mode per endpoint](#mode-per-endpoint).

An access is one of:

| Kind | What is recorded | Example |
|---|---|---|
| `sql` | The statement shape: the SQL text with all literals and parameters replaced by `?` | `SELECT * FROM Products WHERE Name LIKE ?` |
| `http` | Method, scheme, host and port of an outbound call, also of each redirect hop. See [Outbound calls](#outbound-calls) | `GET https://hooks.example.com` |
| `caller` | The kind of caller: anonymous, or signed in with a list of roles and of chosen claims. See [Callers](#callers) | `signed in, role admin` |
| `rows` | The largest number of rows that one command returns. See [Limits](#limits) | `up to 10 from SELECT * FROM Products WHERE Name LIKE ?` |
| `response` | The largest size of the response body. See [Limits](#limits) | `up to 1000 bytes` |

A profile looks like this (see the full one in [samples/SampleApi/behavior.lock.json](samples/SampleApi/behavior.lock.json)):

```json
{
  "$database": [
    "Microsoft.EntityFrameworkCore.Sqlite 11.0.0"
  ],
  "GET /admin/orders": [
    "caller: signed in, role admin",
    "response: up to 100 bytes",
    "rows: up to 10 from SELECT \"o\".\"Id\", \"o\".\"Total\", \"o\".\"UserId\" FROM \"Orders\" AS \"o\"",
    "sql: SELECT \"o\".\"Id\", \"o\".\"Total\", \"o\".\"UserId\" FROM \"Orders\" AS \"o\""
  ],
  "GET /products/search": [
    "caller: anonymous",
    "response: up to 100 bytes",
    "rows: up to 10 from SELECT * FROM Products WHERE Name LIKE ?",
    "sql: SELECT * FROM Products WHERE Name LIKE ?"
  ]
}
```

The `$database` entry names the database provider that the profile was recorded with.
See [Record with the database of production](#record-with-the-database-of-production).

## Quick start

```csharp
using BehaviorLock;

var builder = WebApplication.CreateBuilder(args);

// 1. Register the guard. This also covers the outbound calls of every HttpClient.
builder.Services.AddBehaviorLock(options => builder.Configuration.GetSection("BehaviorLock").Bind(options));

// 2. Let the guard check the commands of your DbContext.
builder.Services.AddDbContext<ShopDb>((services, options) => options
    .UseSqlite(connectionString)
    .UseBehaviorLock(services));

var app = builder.Build();

// 3. Add the middleware after routing, authentication and authorization, and before the endpoints.
app.UseAuthentication();
app.UseAuthorization();
app.UseBehaviorLock();
```

```json
"BehaviorLock": {
  "Mode": "Enforce",
  "ProfilePath": "behavior.lock.json"
}
```

## Options

| Option | Default | Meaning |
|---|---|---|
| `Mode` | `Audit` | The global mode. See [Modes](#modes) |
| `ProfilePath` | `behavior.lock.json` | The recorded profile. See [Deploy the profile](#deploy-the-profile) |
| `ApprovedPath` | Not set | The file with reviewed accesses. See [Find the gaps in production](#find-the-gaps-in-production) |
| `PendingPath` | Not set | The file where the guard writes unknown accesses. Not set means that no file is written |
| `PendingLimit` | `1000` | The largest number of accesses in the pending file |
| `CheckCallers` | `true` | Records and checks the kind of caller. See [Callers](#callers) |
| `CallerClaims` | Empty | More claim types that are part of the kind of caller, for example `scope`. See [Callers](#callers) |
| `CheckAllHttpClients` | `true` | Checks every `HttpClient` and each redirect hop. `false` checks only the clients of `IHttpClientFactory`. See [Outbound calls](#outbound-calls) |
| `CheckLimits` | `true` | Records and checks the rows and the response size. See [Limits](#limits) |
| `EnforceLimits` | `true` | `false` means that a number over a limit is reported, but never blocked. See [Limits](#limits) |
| `SqlShapeCacheSize` | `1000` | The number of SQL command texts whose shapes the guard keeps. `0` turns the cache off. See [Performance](#performance) |

A relative path is resolved against the content root of the app.

## Modes

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

## Outbound calls

The guard checks each outbound HTTP call before it is sent. The entry has the method, the scheme, the host
and the port, for example `http: GET https://hooks.example.com`. The path is not part of it.

- **Every `HttpClient` is covered**, also a client that a package creates by itself with `new HttpClient()`.
  A package that starts to call a new host is stopped, whatever client it uses.
- **Each redirect hop is checked.** A legal host can answer with a redirect to another host, for example to
  an internal address. The second host must be in the profile too, or the call stops there.
- **A blocked call is never sent.** The code that made the call gets a `GuardViolationException`. The
  request is marked as blocked, also when the package catches the exception: it ends with a 403, or with a
  closed connection when the answer has already started.

How it works: the HTTP handlers of .NET (`SocketsHttpHandler` and `HttpClientHandler`) write an event at the
start of each request and of each redirect hop. The guard listens to it. The clients of
`IHttpClientFactory` get a handler of the guard as well, so they are covered with every primary handler.

Things to know:

- **Not covered:** a client that `IHttpClientFactory` did not create and that has a primary handler which
  is not from .NET, for example `WinHttpHandler` or a handler of your own. Raw sockets and other protocols
  are not covered either. See [Known limits](#known-limits).
- **A `traceparent` header can appear.** The event needs an activity for each request. A call inside an
  ASP.NET Core request with logging on has one already. A call without an activity around it, for example in
  a [background job](#background-jobs), now carries a `traceparent` header.
- **The .NET setting `System.Net.Http.EnableActivityPropagation` must stay on.** When an app turns it off,
  the handlers write no event. The guard then logs a warning at startup and checks only the clients of
  `IHttpClientFactory`, without redirect hops.
- A call outside a request and outside a scope is not checked.
- Set `CheckAllHttpClients` to `false` to check only the clients of `IHttpClientFactory`, and only their
  first address.

## Callers

The guard records which kind of caller uses each endpoint. In production it stops a caller of another kind
before the endpoint runs.

| Caller | Entry in the profile |
|---|---|
| Not signed in | `caller: anonymous` |
| Signed in, without a role | `caller: signed in, no role` |
| Signed in, with roles | `caller: signed in, role admin, role support` |
| Signed in, with the claims of `CallerClaims` | `caller: signed in, no role, scope orders.read, scope orders.write` |

This finds an endpoint that forgot its own check. In the sample, `GET /admin/orders` only asks for a
signed-in caller. The tests call it as an admin, so the profile has `caller: signed in, role admin`.
A normal user gets a 403 from the guard.

- **The whole list of roles must match.** A recording with a caller that has the roles `admin` and `support`
  does not allow a caller that has only `support`. Send one legal request for each legal kind of caller.
- **The roles are the role claims of the signed-in user**, sorted by name.
- **Other claims can be part of the caller.** Many APIs use the `scope` claim of a token in place of roles.
  Name such claim types in `CallerClaims`. Their values follow the roles, sorted by type and value. A value
  with spaces counts as a list, so `orders.read orders.write` gives two parts. Like the roles, the whole
  list must match.

  ```json
  "BehaviorLock": { "CallerClaims": [ "scope" ] }
  ```

  Use only claims with few values. A claim that differs for each user, such as the user id or the email,
  gives one kind of caller per user. A change of `CallerClaims` changes the entries, so record the profile again.
- **The guard does not replace authorization.** It runs after it, and it sees only the requests that the
  app lets through.
- **Add the middleware after `UseAuthentication` and `UseAuthorization`.** Before authentication, every
  caller is anonymous. Before authorization, the guard answers 403 where the app answers 401.
- **Record with the callers of production.** Tests that run without sign-in, or with one user for every
  request, record callers that do not fit production. Run `Audit` mode with a pending file first, or set
  `CheckCallers` to `false`.
- An endpoint without a `caller:` entry is not checked for its caller. This is the case for a profile from
  an older version.

## Limits

A request can keep the legal shape and still take far too much. A `%` in a search value returns the whole
table with the same statement. The guard records two numbers for this, and it stops a request that passes them.

| Limit | Entry in the profile | When it is checked |
|---|---|---|
| The rows that one command returns | `rows: up to 10 from SELECT * FROM Products WHERE Name LIKE ?` | When the reader of the command closes |
| The size of the response body | `response: up to 1000 bytes` | At each write to the body |

`Learn` mode rounds each number up to 0, 1, 10, 100, 1000 and so on, and it keeps the highest step that it
saw. So a small change of the test data does not change the profile.

**The limits come from your test data.** A test database is small, so the recorded limits are often lower
than the legal numbers of production. Do this before the limits block requests:

1. Run `Audit` mode with a pending file. See [Find the gaps in production](#find-the-gaps-in-production).
2. The pending file gets one entry per limit, with the highest step that a request needed, for example
   `rows: up to 1000 from SELECT * FROM Products WHERE Name LIKE ?`.
3. Decide what is legal and copy the entry to the approved file. You can write any number, for example
   `up to 5000`. Of two limits for the same thing, the higher one applies.

**You can enforce everything else first.** Set `EnforceLimits` to `false`. In `Enforce` mode the guard then
still blocks a new SQL shape, a new host and a new kind of caller. A number over a limit is only reported
and written to the pending file, and the request goes on. Set the option back to `true` when the pending
file gets no more limits.

```json
"BehaviorLock": {
  "Mode": "Enforce",
  "EnforceLimits": false,
  "PendingPath": "/var/lib/shop/behavior.lock.pending.json"
}
```

What a blocked request looks like:

- **Rows.** The guard knows the number only when the app has read the rows, so the command has already run.
  The guard then throws, and the endpoint cannot use or send the rows. The response is 403 with an empty body.
- **Response size.** The guard stops the first write that would pass the limit. When nothing was sent yet,
  the response is 403 with an empty body. When a part was sent, the guard closes the connection. The client
  never gets more bytes than the limit.

Things to know:

- **No entry means no limit.** An approved `sql:` line has no row limit until you add a `rows:` line. A
  profile from an older version has no limits at all.
- **The row count is the number of read calls.** It is one higher than the number of rows when the app reads
  to the end. So a query for one row gets the entry `up to 10`.
- **A command with several statements has one count.** It belongs to all its statements together.
- **A row limit does not protect the database or the memory of the app.** A blocked command has run, and
  its rows were in memory. A blocked `INSERT`, `UPDATE` or `DELETE` with `RETURNING` has changed the data,
  unless the app uses a transaction.
- **An error answer counts too.** A validation error can be larger than the legal answer of a small
  endpoint. Record that path with a test, or approve its size.
- **A streaming endpoint has no natural size.** Give it a high limit in the approved file, or its own mode.
- In `Enforce` mode, a response with a size limit starts at its first write or flush. A call to
  `Response.StartAsync` alone does not send the headers.
- Set `CheckLimits` to `false` to turn both limits off.

## Background jobs

The guard checks a request because the middleware opens a context for it. Code outside a request has no
context: a background job, a message handler, a timer. Open a scope there, and the guard checks the SQL
commands and the outbound calls of that code too.

```csharp
public sealed class NightlyCleanup(GuardScopes scopes, IServiceScopeFactory services) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        using var guarded = scopes.Begin("job nightly-cleanup");
        using var scope = services.CreateScope();

        var db = scope.ServiceProvider.GetRequiredService<ShopDb>();
        await db.Orders.Where(order => order.Total == 0).ExecuteDeleteAsync(stop);
    }
}
```

The name of the scope is its key in the profile, in the place of the endpoint key:

```json
"job nightly-cleanup": [
  "sql: DELETE FROM \"Orders\" AS \"o\" WHERE \"o\".\"Total\" = ?"
]
```

- **Record the job like an endpoint.** A test runs it once in `Learn` mode, for each legal path.
- **A blocked access throws `GuardViolationException`** in the code of the job. There is no response that
  could become a 403. The violation goes to the sinks and to the pending file as usual.
- **A scope can have its own mode**: `scopes.Begin("job nightly-cleanup", GuardMode.Audit)`. The rules are the
  rules of [Mode per endpoint](#mode-per-endpoint).
- **Use a name that no endpoint has.** A scope with the key of an endpoint shares the profile of that endpoint.
- **Call `Begin` in the method that does the work.** A context that an `async` method sets is not visible
  to its caller. Dispose the scope to end it; the context from before then applies again.
- A scope has no caller and no response, so it has no `caller:` and no `response:` entry. Row limits work.
- `EndpointCoverage.FindUnprofiled` does not know the scopes. A scope without a profile can touch nothing
  in `Enforce` mode, so a forgotten recording shows itself at the first run.

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

## SQL dialects

The guard replaces each string in the SQL text by `?`. It must find the end of each string exactly where
the database finds it. If it finds another end, it replaces code by `?`, and an injection keeps the legal shape.
Databases read strings and comments in different ways, so the rules come from the provider of the `DbContext`:

| Provider | Rules |
|---|---|
| SQLite, Oracle | Standard strings and comments |
| SQL Server | Standard strings. A comment can contain a comment |
| PostgreSQL | `$$...$$` and `$tag$...$tag$` are strings. A comment can contain a comment. Backslash: see below |
| MySQL, MariaDB | `#` starts a comment. Backslash: see below |
| Any other provider | Not known. Every text that databases read in different ways is in doubt |

**When in doubt, the command is not normalized.** MySQL and PostgreSQL read `\'` as a quote inside the string
or as the end of the string. That depends on a setting of the server, which the guard cannot see. So the
guard reads the string both ways. When the two ways end at the same place, the string becomes `?` as usual.
When they end at different places, the guard keeps the whole command as it is, with its values.

A command that is kept as it is only matches a profile entry with the same text. So a fixed command with
parameters still works. A command that is built from user input never matches, and it is reported or blocked.

Two more rules close other ways to keep the shape:

- Only a list of parameters is reduced to one placeholder: `IN (@a, @b, @c)` becomes `IN (?)`. A list with a
  literal keeps its length, because an injection could add a value or a whole `VALUES` row.
- Letters directly after a number are not part of the number: `1OR` becomes `?OR`.

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
- A limit has one entry in the pending file, with the highest number that a request needed.
  See [Limits](#limits).
- `Enforce` mode stops a request at its first unknown access, so the pending file gets one entry per
  request. `Audit` mode lets the request finish and writes all its unknown accesses.
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
| SQL injection that keeps the shape | `%` in a `LIKE` value, in a shop with many products | Every product is returned | More rows than a legal search |
| Missing role check | A normal user calls `GET /admin/orders` | The orders of all users leak | New kind of caller |

Two attacks are **not** stopped. [`KnownLimitTests`](tests/BehaviorLock.Tests/KnownLimitTests.cs) keeps them visible:

- **IDOR.** Reading the order of another user has the same SQL shape and the same kind of caller as a
  legal read.
- **Injection that keeps the shape and stays under the limits.** A `%` wildcard in a `LIKE` value changes
  the result, but not the statement. The guard sees it only when it returns more rows than the limit.

## Known limits

- **Attacks inside the profile pass.** Business logic errors, XSS and most DoS attacks do not change what an
  endpoint touches. Broken authorization passes when the attacker is a legal kind of caller, for example
  one user who reads the data of another user.
- **Callers and limits are only as good as the recording.** They come from the users and the data of your
  tests. See [Callers](#callers) and [Limits](#limits).
- **A row limit works after the command has run.** See [Limits](#limits).
- **Only EF Core and `HttpClient` are covered.** Dapper, raw ADO.NET, raw sockets, file access and process
  start are not checked. A few HTTP clients are not covered either; see [Outbound calls](#outbound-calls).
- **Code outside a request is checked only inside a scope.** Startup code and a background job without a
  scope are not checked. See [Background jobs](#background-jobs).
- **No request-wide rollback.** A blocked access never runs, but earlier writes of the same request stay,
  unless the app uses its own transaction.
- **The profile records what the code does, not what it should do.** If a legal request triggers a harmful
  path during recording, that path becomes legal. Review the profile diff.
- **Missing test coverage blocks legal traffic.** A legal path that no test covers gets a 403 in `Enforce` mode.
  Run `Audit` mode with a pending file first; see [Find the gaps in production](#find-the-gaps-in-production).
- **The approved file and the mode of an endpoint weaken the guard when they are used without care.**
  An approved attack is legal from then on, and an endpoint with the mode `Off` or `Audit` is not protected.
  Review both in the pull request.
- **The pending file is local to one instance.** There is no tool yet that collects the files of many
  instances or copies entries to the approved file. Both steps are done by hand.
- **The guard runs inside the app process.** An attacker with full code execution can turn it off.
- **The SQL normalizer is a small scanner, not a parser.** It has rules for SQLite, SQL Server, PostgreSQL and
  MySQL; see [SQL dialects](#sql-dialects). The rules for MySQL 8.4 and PostgreSQL 17 were compared with real
  servers, with random commands. The rules for SQL Server come from its documentation and are tested with
  SQL text only. A random test finds many errors, but it does not prove that there are none.
- **MySQL text in double quotes is never normalized.** MySQL reads `"..."` as a string or as a name, and that
  depends on a setting. The guard keeps it as it is, so each value gives its own shape. Use single quotes.
- **A command that is not normalized contains its values.** They can appear in the log and in the pending file.
- **Each length of a list with literals is its own shape.** Use parameters for lists.
- **The database check compares the provider and its version only.** It does not see the version of the
  database server or its settings, and both can change the SQL too.

## Extension points

| Interface | Purpose | Default |
|---|---|---|
| `IViolationSink` | Receives violations | `LoggingViolationSink` |
| `IProfileStore` | Loads and saves the recorded profile with its `Databases`, and names its location | `JsonFileProfileStore` |
| `ISqlNormalizer` | Reduces SQL to its shape, with the rules of a `SqlDialect`. The result must depend only on the text and the dialect, because the guard keeps it | `SqlNormalizer` |
| `IAccessGuard` | Checks one access. Call it from your own code to guard other resources. An access with `UpTo` is a limit | `AccessGuard` |
| `IGuardModeMetadata` | Endpoint metadata that sets the mode of one endpoint | `BehaviorLockAttribute` |

## Repository layout

```
src/BehaviorLock            The library
samples/SampleApi            Shop API with deliberate weaknesses, plus its recorded profile
tests/BehaviorLock.Tests    Unit tests, legal requests, attacks, pending and approved files, mode per endpoint, callers, limits, scopes, outbound calls
perf/BehaviorLock.LoadTest   Load test that compares the API with and without the guard
```

## Build and test

```
dotnet test
```

The test run rewrites `samples/SampleApi/behavior.lock.json`. A diff in that file after a test run
means the behavior of an endpoint changed.

### Tests against real MySQL and PostgreSQL servers

[`RealDatabaseTests`](tests/BehaviorLock.Tests/RealDatabaseTests.cs) compares the SQL normalizer with real
servers. It builds thousands of commands from random pieces, runs each one, and counts the rows. A command
that returns every row ran injected code, so its shape must differ from the legal shape. The tests cover
MySQL with and without `NO_BACKSLASH_ESCAPES` and `ANSI_QUOTES`, and PostgreSQL with
`standard_conforming_strings` on and off.

These tests are skipped unless the connection strings are set. Start two test servers, with Docker or with `wslc`:

```
docker run -d --rm --name behaviorlock-mysql -e MYSQL_ROOT_PASSWORD=test -e MYSQL_DATABASE=guard -p 33306:3306 mysql:8.4
docker run -d --rm --name behaviorlock-postgres -e POSTGRES_PASSWORD=test -e POSTGRES_DB=guard -p 35432:5432 postgres:17
```

Then set the variables and run the tests:

```
BEHAVIORLOCK_TEST_MYSQL="Server=127.0.0.1;Port=33306;User ID=root;Password=test;Database=guard;AllowUserVariables=true"
BEHAVIORLOCK_TEST_POSTGRES="Host=127.0.0.1;Port=35432;Username=postgres;Password=test;Database=guard"
dotnet test --filter "FullyQualifiedName~RealDatabaseTests"
```

The tests drop and create a table with the name `Users`, so use them only with a test database.

## Performance

[perf/BehaviorLock.LoadTest](perf/BehaviorLock.LoadTest) measures what the guard costs. It starts the sample
API four ways on real Kestrel ports (without the package, and in `Off`, `Audit` and `Enforce` mode) and
sends the same load to each one.

```
dotnet run -c Release --project perf/BehaviorLock.LoadTest
dotnet run -c Release --project perf/BehaviorLock.LoadTest -- --filter preview --rounds 8
```

Results from one machine (16 logical cores, Windows 11, .NET 11, local SQLite file, 16 connections,
8 rounds of 4 s), with the caller check and the limits on. Throughput in `Enforce` mode, compared with the
same API without the package:

| Scenario | Requests/s without the package | Change in `Enforce` mode |
|---|---:|---|
| No resource access | 65,800 | About 4% lower |
| One EF Core query | 16,500 | No clear difference (the median is +1%, the rounds go from -20% to +5%) |
| One raw SQL query | 18,200 | No clear difference (the median is -3%, the rounds go from -38% to +47%) |
| EF Core query + `UPDATE`, one connection | 440 | About 3% lower (the rounds go from -6% to +4%) |
| One outbound HTTP call | 61,900 | About 4% lower |
| SQL injection, blocked with a 403 | 16,500 | No clear difference (the median is +5%, the rounds go from -2% to +8%) |

The latency percentiles (p50, p95, p99) differ by 0.3 ms or less in every scenario.

The guard keeps the shapes of the SQL command texts that passed. An app sends the same few texts again and
again, so most commands are not normalized:

| Work for one SQL command | Time | Allocated |
|---|---:|---:|
| A text that the guard knows: one lookup and one check | About 0.4 µs | 216 bytes |
| A new text: normalize it first | 0.3 to 0.6 µs more | About 770 bytes more |

- The row check at the end of a command uses the same kept shape.
- The cache keeps at most `SqlShapeCacheSize` texts (default 1000) and about 4 MB of text. When it is full, it
  starts again from empty. A text of more than 20,000 characters is never kept.
- In `Enforce` mode a blocked command is never kept, so the texts of an attacker do not fill the cache.
  In `Audit` mode they can fill it. That costs time, not safety: the texts of the app are normalized again.
- A known text is still checked for each request. The cache only saves the work of normalizing.

How to read these numbers:

- The machine did other work during this run, so the requests per second are low and the SQL rounds differ
  much. Read the relative change, not the absolute numbers.
- With a size limit, the response body passes through one more writer. With the caller check, each request
  makes one more lookup in the profile.
- A local SQLite file answers in about 0.2 ms. That makes the relative cost of the guard as high as it gets.
  A database on the network is slower, so the same cost is a smaller share of the request.
- In `Off` mode, and for code outside a request and outside a scope, the guard skips its work: the SQL is not normalized and
  the outbound call is not inspected. Only the call into the EF Core interceptor and the HTTP handler remains.
- The load test answers outbound calls with a stub handler, so it does not measure the check of the .NET
  handlers. In a separate test with a real local call (40 to 70 µs per call), the check made no
  difference that the test could measure.
- The load client runs in the same process as the servers, and the machine speed changes during a run.
  The test compares each variant with the baseline run of the same round to reduce that effect.
  Differences below about 1% are not reliable.

## License

BehaviorLock is open source under the [Apache License 2.0](LICENSE).

Copyright 2026 Vadim Albarov
