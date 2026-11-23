# Development

Back to the [overview](../README.md#documentation).

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

The tests run once for .NET 10 with EF Core 10, and once for .NET 11 with EF Core 11. You need both runtimes.
To run the tests of one version only, add `-f net11.0` or `-f net10.0`.

The test run for .NET 11 rewrites `samples/SampleApi/behavior.lock.json`. A diff in that file after a test run
means the behavior of an endpoint changed. The test run for .NET 10 records its own profile next to the test
assembly, because the profile holds the version of the database provider.

### Tests against real MySQL and PostgreSQL servers

[`RealDatabaseTests`](../tests/BehaviorLock.Tests/RealDatabaseTests.cs) compares the SQL normalizer with real
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

### Release

Before a release, move the entries under "Unreleased" in [CHANGELOG.md](../CHANGELOG.md) to a section for the
new version.

A version tag publishes the package to nuget.org and creates a GitHub release. The tag gives the version.
A version with a suffix, for example `v1.2.3-preview.1`, becomes a prerelease:

```
git tag v0.2.0
git push origin v0.2.0
```

The [release workflow](../.github/workflows/release.yml) uses
[trusted publishing](https://learn.microsoft.com/nuget/nuget-org/trusted-publishing), so the repository stores no
API key. It needs two things, set up once:

- On nuget.org, a trusted publishing policy for this repository with the workflow file `release.yml`.
- In the repository, the secret `NUGET_USER` with the nuget.org user name of the package owner.

Each pull request also packs the library. The package is an artifact of the CI run.
