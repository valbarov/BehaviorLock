# BehaviorLock

[![CI](https://github.com/valbarov/BehaviorLock/actions/workflows/ci.yml/badge.svg)](https://github.com/valbarov/BehaviorLock/actions/workflows/ci.yml)

Active security for ASP.NET Core APIs. BehaviorLock records what each endpoint does when it handles legal
requests. In production it blocks every request that makes an endpoint do something else.

It does not look for known attack patterns. It only knows the legal behavior, so it also stops attacks and
bugs that nobody has seen before, as long as they change what the endpoint touches, who calls it, or how
much it returns.

> Status: MVP. It targets .NET 10 and .NET 11, and it covers EF Core and `HttpClient`. Read [Known limits](docs/known-limits.md) before you use it.

## How it works

1. **Record.** Your tests send one legal request per legal path. In `Learn` mode the guard writes down every
   resource access of each endpoint. The result is the *profile*, stored in `behavior.lock.json`.
   It works like a package lock file, but it locks behavior instead of versions.
2. **Review.** You commit the profile. When an endpoint starts to touch something new, the profile diff shows it
   in the pull request.
3. **Enforce.** In production the guard checks each access *before* it runs. An access that is not in the
   profile never runs. The request gets a 403 and an alert is raised. Only the row limit is checked after
   the command has run; see [Limits](docs/checks.md#limits).
4. **Close the gaps.** Tests never cover every legal path. In production the guard writes each unknown access
   to a pending file. A person reviews it and accepts the legal ones.
   See [Find the gaps in production](docs/profile.md#find-the-gaps-in-production).

You do not have to enforce everything at once. Each endpoint can have its own mode.
See [Mode per endpoint](docs/modes.md#mode-per-endpoint).

An access is one of:

| Kind | What is recorded | Example |
|---|---|---|
| `sql` | The statement shape: the SQL text with all literals and parameters replaced by `?` | `SELECT * FROM Products WHERE Name LIKE ?` |
| `http` | Method, scheme, host and port of an outbound call, also of each redirect hop. See [Outbound calls](docs/checks.md#outbound-calls) | `GET https://hooks.example.com` |
| `caller` | The kind of caller: anonymous, or signed in with a list of roles and of chosen claims. See [Callers](docs/checks.md#callers) | `signed in, role admin` |
| `rows` | The largest number of rows that one command returns. See [Limits](docs/checks.md#limits) | `up to 10 from SELECT * FROM Products WHERE Name LIKE ?` |
| `response` | The largest size of the response body. See [Limits](docs/checks.md#limits) | `up to 1000 bytes` |

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
See [Record with the database of production](docs/profile.md#record-with-the-database-of-production).

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

## Install

```
dotnet add package BehaviorLock
```

The package is for .NET 10 with EF Core 10, and for .NET 11 with EF Core 11.

## Quick start

Start with `Audit` mode. The guard then records and reports, but it blocks nothing. So this first step cannot
break a legal request.

### 1. Add the guard to the app

```csharp
using BehaviorLock;

var builder = WebApplication.CreateBuilder(args);

// Register the guard. This also covers the outbound calls of every HttpClient.
builder.Services.AddBehaviorLock(builder.Configuration.GetSection("BehaviorLock"));

// Let the guard check the commands of your DbContext.
builder.Services.AddDbContext<ShopDb>((services, options) => options
    .UseSqlite(connectionString)
    .UseBehaviorLock(services));

var app = builder.Build();

// Add the middleware after routing, authentication and authorization, and before the endpoints.
app.UseAuthentication();
app.UseAuthorization();
app.UseBehaviorLock();
```

```json
"BehaviorLock": {
  "Mode": "Audit"
}
```

`Audit` is also the mode when the configuration has none.

### 2. Record the profile in a test

Add a test that starts the app in `Learn` mode and sends the legal requests. It needs the package
`Microsoft.AspNetCore.Mvc.Testing`.

```csharp
public sealed class RecordProfile
{
    [Fact]
    public async Task Record_the_legal_requests()
    {
        await using var app = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
                services.Configure<BehaviorLockOptions>(options => options.Mode = GuardMode.Learn)));

        using var client = app.CreateClient();

        // One legal request for each legal path of each endpoint.
        await client.GetAsync("/products/1");
        await client.GetAsync("/products/search?name=Lap");

        // The end of the test stops the app, and that saves the profile.
    }
}
```

The test writes `behavior.lock.json` into the project folder of the app. Two things to know:

- Run the test against the kind of database that production uses. Each database provider writes its own SQL.
  See [Record with the database of production](docs/profile.md#record-with-the-database-of-production).
- `Learn` mode adds to a profile that is already there. Delete the file before the recording, so that an
  access that the code no longer makes leaves the profile.
  See [Record the profile from tests](docs/profile.md#record-the-profile-from-tests) for the full pattern.

### 3. Commit the profile

Commit `behavior.lock.json`. From now on, a pull request that makes an endpoint read a new table, call a new
host or take a new kind of caller also changes this file. The reviewer sees it in the diff.

Let CI fail when the committed profile is not the one that the tests record. Then nobody can forget to commit
the change. See [Check the profile in CI](docs/profile.md#check-the-profile-in-ci).

### 4. Deploy and watch

A web project publishes the JSON files of its folder, so the profile goes out with the app. In production,
each access that is not in the profile gives a log warning and counts in a metric. No request is blocked.
See [Alerts and metrics](docs/monitoring.md).

### Next: block

When the warnings have stopped, change the mode to `Enforce`. Then an access that is not in the profile never
runs, and the request gets a 403. [Modes](docs/modes.md) has the recommended rollout, also for one endpoint at
a time. [Find the gaps in production](docs/profile.md#find-the-gaps-in-production) says how to collect and
review what the tests missed.

## Documentation

| Topic | Content |
|---|---|
| [Options](docs/options.md) | Every option with its default |
| [Modes](docs/modes.md) | `Off`, `Learn`, `Audit` and `Enforce`, a change of the mode while the app runs, and a mode per endpoint |
| [What the guard checks](docs/checks.md) | Outbound calls, callers, limits, SQL dialects and background jobs |
| [The profile](docs/profile.md) | How to record it from tests, check it in CI, deploy it, and find the gaps in production with the pending file and the approved file |
| [Alerts and metrics](docs/monitoring.md) | The log line of a violation, a sink of your own, and the counter for a dashboard |
| [Known limits](docs/known-limits.md) | What the guard does not cover. Read it before you use the package |
| [Extension points](docs/extension-points.md) | The interfaces that you can replace |
| [Performance](docs/performance.md) | The cost per request, and how it was measured |
| [Development](docs/development.md) | The repository layout, and how to build, test and release |

## Security

Found a way to get past the guard? Report it in private, not as a public issue.
[SECURITY.md](SECURITY.md) says how, and what counts as a vulnerability.

## Changes

[CHANGELOG.md](CHANGELOG.md) lists the changes of each version.

## License

BehaviorLock is open source under the [Apache License 2.0](LICENSE).

Copyright 2026 Vadim Albarov
