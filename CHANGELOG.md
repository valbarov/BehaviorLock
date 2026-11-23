# Changelog

All notable changes of the package are listed here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the versions follow
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

While the version is below 1.0.0, a new preview can break the public types, the options and the format of the
profile file. Each such change is listed under **Changed** or **Removed**.

## [Unreleased]

### Added

- **Mode change in a running app.** A change of `Mode` in the configuration applies without a restart, between
  `Off`, `Audit` and `Enforce`. So a false positive can be stopped without a new deploy. A change that would
  have stopped the start is refused, and each change is logged as a warning.
- **Limit change in a running app.** A change of `EnforceLimits` in the configuration applies without a restart
  too. So a limit that blocks legal requests can be turned into a report, while everything else stays blocked.
- **Metrics.** The counter `behaviorlock.violations` of the meter `BehaviorLock` counts every violation, with
  the endpoint, the kind of the access and whether it was blocked.
- **Request id.** `GuardViolation.RequestId` and the log line of a violation have the id of the request,
  `HttpContext.TraceIdentifier`. So the reason for a 403 can be found from the request.
- **Placeholders in `PendingPath`.** `{machine}` and `{pid}` give each instance of the app its own pending
  file in a shared directory.
- **Endpoints without a profile in the log.** When the app starts in `Audit` or `Enforce` mode, a warning names
  the endpoints that the profile does not have. Endpoints with the mode `Off` are left out.
- `AddBehaviorLock(configuration)` binds the options to a configuration section and follows its reloads.
  The quick start uses it now.

### Changed

- `GuardViolation` has the new property `RequestId`. It is part of the equality of the record, so two
  violations of two requests are no longer equal.
- The approved file is also read when the app starts in `Off` mode, so that it is there when the mode changes.
- The mode is worked out for each access and no longer once per request. A change of the global mode also
  reaches a request or a scope that is already running.

## [0.1.0-preview.1] - 2026-11-20

The first public preview, for .NET 10 with EF Core 10 and for .NET 11 with EF Core 11.

### Added

- **The profile.** `Learn` mode records the resource accesses of each endpoint into `behavior.lock.json`.
  `Audit` mode reports an access that is not in the profile. `Enforce` mode also blocks it with a 403.
- **SQL.** An EF Core interceptor checks the shape of each statement before the command runs. The scanner
  reads strings and comments by the rules of SQLite, SQL Server, PostgreSQL and MySQL. When the database
  could read a string in two ways, the command is kept as it is.
- **Outbound calls.** The guard checks the method, scheme, host and port of each call of every `HttpClient`,
  and of each redirect hop.
- **Callers.** The profile holds the kind of caller of each endpoint: anonymous, or signed in with a list of
  roles and of the claims that `CallerClaims` names.
- **Limits.** The profile holds the highest number of rows of each command and the highest size of the
  response body. `EnforceLimits: false` reports a number over a limit without blocking.
- **Mode per endpoint.** `WithBehaviorLock` and `BehaviorLockAttribute` give one endpoint, route group,
  controller or action its own mode.
- **Background jobs.** `GuardScopes.Begin` checks the code outside a request.
- **Pending and approved files.** In production the guard writes each unknown access to a pending file.
  Reviewed accesses go into an approved file, which the guard allows next to the profile.
- **Checks at startup.** `Enforce` mode does not start with a missing or empty profile, or with a profile that
  was recorded with another database provider.
- **Extension points.** `IViolationSink`, `IProfileStore`, `ISqlNormalizer`, `IAccessGuard` and
  `IGuardModeMetadata`.
- `EndpointCoverage.FindUnprofiled` finds the endpoints that have no profile, for a test that fails the build.

[Unreleased]: https://github.com/valbarov/BehaviorLock/compare/v0.1.0-preview.1...HEAD
[0.1.0-preview.1]: https://github.com/valbarov/BehaviorLock/releases/tag/v0.1.0-preview.1
