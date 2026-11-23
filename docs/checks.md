# What the guard checks

Back to the [overview](../README.md#documentation).

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
  are not covered either. See [Known limits](known-limits.md).
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

1. Run `Audit` mode with a pending file. See [Find the gaps in production](profile.md#find-the-gaps-in-production).
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

You can change `EnforceLimits` while the app runs, like the mode. So when a limit blocks legal requests, you
can stop that without a new deploy, and the guard still blocks everything else. Each change is logged as a
warning, for example `BehaviorLock EnforceLimits changed from True to False`.
See [Change the mode while the app runs](modes.md#change-the-mode-while-the-app-runs).

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
  rules of [Mode per endpoint](modes.md#mode-per-endpoint).
- **Use a name that no endpoint has.** A scope with the key of an endpoint shares the profile of that endpoint.
- **Call `Begin` in the method that does the work.** A context that an `async` method sets is not visible
  to its caller. Dispose the scope to end it; the context from before then applies again.
- A scope has no caller and no response, so it has no `caller:` and no `response:` entry. Row limits work.
- `EndpointCoverage.FindUnprofiled` does not know the scopes. A scope without a profile can touch nothing
  in `Enforce` mode, so a forgotten recording shows itself at the first run.
