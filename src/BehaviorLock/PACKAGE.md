# BehaviorLock

Active security for ASP.NET Core APIs. BehaviorLock records what each endpoint does when it handles legal
requests. In production it blocks every request that makes an endpoint do something else.

It does not look for known attack patterns. It only knows the legal behavior, so it also stops attacks and
bugs that nobody has seen before, as long as they change what the endpoint touches, who calls it, or how
much it returns.

> Status: MVP. It targets .NET 10 and .NET 11, and it covers EF Core and `HttpClient`.
> Read the [known limits](https://github.com/valbarov/BehaviorLock#known-limits) before you use it.

## How it works

1. **Record.** Your tests send one legal request per legal path. In `Learn` mode the guard writes down every
   resource access of each endpoint. The result is the profile, stored in `behavior.lock.json`.
2. **Review.** You commit the profile. When an endpoint starts to touch something new, the profile diff
   shows it in the pull request.
3. **Enforce.** In production the guard checks each access before it runs. An access that is not in the
   profile never runs. The request gets a 403 and an alert is raised.

A profile entry is a SQL statement shape, an outbound host, a kind of caller, a number of rows or a response size:

```json
{
  "GET /products/search": [
    "caller: anonymous",
    "response: up to 100 bytes",
    "rows: up to 10 from SELECT * FROM Products WHERE Name LIKE ?",
    "sql: SELECT * FROM Products WHERE Name LIKE ?"
  ]
}
```

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
  "Mode": "Audit",
  "ProfilePath": "behavior.lock.json"
}
```

Start with `Audit` mode. It reports an access that is not in the profile, but it does not block it.
Switch to `Enforce` when the reports stop.

## Documentation

The full documentation is in the [repository](https://github.com/valbarov/BehaviorLock#documentation): the modes,
the options, how to record the profile from tests, how to find the gaps in production, the metrics, and the
known limits.

## Security

Report a vulnerability in private, not as a public issue. See the
[security policy](https://github.com/valbarov/BehaviorLock/blob/main/SECURITY.md).

## License

[Apache License 2.0](https://github.com/valbarov/BehaviorLock/blob/main/LICENSE)
