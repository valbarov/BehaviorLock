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
dotnet add package BehaviorLock --prerelease
```

The package is for .NET 10 with EF Core 10, and for .NET 11 with EF Core 11.

## Quick start

```csharp
using BehaviorLock;

var builder = WebApplication.CreateBuilder(args);

// 1. Register the guard. This also covers the outbound calls of every HttpClient.
builder.Services.AddBehaviorLock(builder.Configuration.GetSection("BehaviorLock"));

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

`Enforce` mode needs the profile. [The profile](docs/profile.md) says how to record it from your tests, and
[Modes](docs/modes.md) has the recommended rollout.

## Documentation

| Topic | Content |
|---|---|
| [Options](docs/options.md) | Every option with its default |
| [Modes](docs/modes.md) | `Off`, `Learn`, `Audit` and `Enforce`, a change of the mode while the app runs, and a mode per endpoint |
| [What the guard checks](docs/checks.md) | Outbound calls, callers, limits, SQL dialects and background jobs |
| [The profile](docs/profile.md) | How to record it from tests, deploy it, and find the gaps in production with the pending file and the approved file |
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
