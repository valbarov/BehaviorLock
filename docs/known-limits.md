# Known limits

Back to the [overview](../README.md#documentation).

- **Attacks inside the profile pass.** Business logic errors, XSS and most DoS attacks do not change what an
  endpoint touches. Broken authorization passes when the attacker is a legal kind of caller, for example
  one user who reads the data of another user.
- **Callers and limits are only as good as the recording.** They come from the users and the data of your
  tests. See [Callers](checks.md#callers) and [Limits](checks.md#limits).
- **A row limit works after the command has run.** See [Limits](checks.md#limits).
- **Only EF Core and `HttpClient` are covered.** Dapper, raw ADO.NET, raw sockets, file access and process
  start are not checked. A few HTTP clients are not covered either; see [Outbound calls](checks.md#outbound-calls).
- **Code outside a request is checked only inside a scope.** Startup code and a background job without a
  scope are not checked. See [Background jobs](checks.md#background-jobs).
- **No request-wide rollback.** A blocked access never runs, but earlier writes of the same request stay,
  unless the app uses its own transaction.
- **The profile records what the code does, not what it should do.** If a legal request triggers a harmful
  path during recording, that path becomes legal. Review the profile diff.
- **Missing test coverage blocks legal traffic.** A legal path that no test covers gets a 403 in `Enforce` mode.
  Run `Audit` mode with a pending file first; see [Find the gaps in production](profile.md#find-the-gaps-in-production).
- **The approved file and the mode of an endpoint weaken the guard when they are used without care.**
  An approved attack is legal from then on, and an endpoint with the mode `Off` or `Audit` is not protected.
  Review both in the pull request.
- **The pending file is local to one instance.** The placeholders in `PendingPath` keep the files of many
  instances apart, but there is no tool yet that puts them together or copies entries to the approved file.
  Both steps are done by hand.
- **The guard runs inside the app process.** An attacker with full code execution can turn it off.
- **The SQL normalizer is a small scanner, not a parser.** It has rules for SQLite, SQL Server, PostgreSQL and
  MySQL; see [SQL dialects](checks.md#sql-dialects). The rules for MySQL 8.4 and PostgreSQL 17 were compared with real
  servers, with random commands. The rules for SQL Server come from its documentation and are tested with
  SQL text only. A random test finds many errors, but it does not prove that there are none.
- **MySQL text in double quotes is never normalized.** MySQL reads `"..."` as a string or as a name, and that
  depends on a setting. The guard keeps it as it is, so each value gives its own shape. Use single quotes.
- **A command that is not normalized contains its values.** They can appear in the log and in the pending file.
- **Each length of a list with literals is its own shape.** Use parameters for lists.
- **The database check compares the provider and its version only.** It does not see the version of the
  database server or its settings, and both can change the SQL too.
