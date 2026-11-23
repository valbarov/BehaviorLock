using System.Data.Common;
using MySqlConnector;
using Npgsql;
using Xunit.Abstractions;

namespace BehaviorLock.Tests;

/// <summary>A theory that only runs when the environment variable holds a connection string.</summary>
public sealed class RealDatabaseTheoryAttribute : TheoryAttribute
{
    public RealDatabaseTheoryAttribute(string variable)
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(variable)))
        {
            Skip = $"Set {variable} to a connection string to run this test. See docs/development.md.";
        }
    }
}

/// <summary>
/// Compares the normalizer with real database servers. The servers decide what is an injection:
/// the tests build many commands from random pieces, run each one, and count the rows.
/// A command that returns every row ran injected code. Its shape must then differ from the legal shape.
/// </summary>
public sealed class RealDatabaseTests(ITestOutputHelper output)
{
    public const string MySqlVariable = "BEHAVIORLOCK_TEST_MYSQL";
    public const string PostgresVariable = "BEHAVIORLOCK_TEST_POSTGRES";

    private const int CommandsPerTemplate = 4000;

    // The table has two rows, and a legal command returns one of them at most.
    private const int LegalRows = 1;

    private static readonly string[] Setup =
    [
        "DROP TABLE IF EXISTS Users",
        "CREATE TABLE Users (Id INT, Name VARCHAR(50), City VARCHAR(50))",
        "INSERT INTO Users VALUES (1, 'alice', 'Paris'), (2, 'bob', 'Rome')",
    ];

    // Pieces that move the end of a string, start or end a comment, or add a condition that is always true.
    private static readonly string[] Pieces =
    [
        "\\", "'", "''", "\\'", "\\\\", "\"", "\"\"", "\\\"",
        " OR 1=1", " OR 1=1 -- ", " OR 1=1 #", " OR '1'='1", " OR \"1\"=\"1",
        "' OR 1=1 -- ", "\' OR 1=1 -- ", "\" OR 1=1 -- ", "$$ OR 1=1 -- ", " OR 1=1 /*",
        " -- ", "#", "/*", "*/", "$$", "$a$", "\n", " ", "=", "(", ")", "%",
        "x", "alice", "Paris", "1", "0x1", "1e1", "E", "N",
    ];

    private readonly SqlNormalizer _normalizer = new();

    static RealDatabaseTests()
    {
        // Npgsql must send the text as it is. By default it reads the text itself to find parameters.
        AppContext.SetSwitch("Npgsql.EnableSqlRewriting", false);
    }

    [RealDatabaseTheory(MySqlVariable)]
    [InlineData("")]
    [InlineData("NO_BACKSLASH_ESCAPES")]
    [InlineData("ANSI_QUOTES")]
    [InlineData("NO_BACKSLASH_ESCAPES,ANSI_QUOTES")]
    public async Task MySql_never_runs_injected_code_under_the_legal_shape(string sqlMode)
    {
        await using var connection = new MySqlConnection(Environment.GetEnvironmentVariable(MySqlVariable));
        await connection.OpenAsync();
        await Prepare(connection);
        if (sqlMode.Length > 0)
        {
            await Execute(connection, $"SET SESSION sql_mode = CONCAT(@@sql_mode, ',{sqlMode}')");
        }

        await Compare(connection, SqlDialect.MySql, sqlMode,
        [
            new("single quotes", "SELECT Name FROM Users WHERE Name = '{0}' AND City = '{1}'", "alice", "Paris"),
            new("double quotes", "SELECT Name FROM Users WHERE Name = \"{0}\" AND City = \"{1}\"", "alice", "Paris"),
            new("number", "SELECT Name FROM Users WHERE Id = {0} AND City = '{1}'", "1", "Paris"),
        ]);
    }

    [RealDatabaseTheory(PostgresVariable)]
    [InlineData("on")]
    [InlineData("off")]
    public async Task PostgreSql_never_runs_injected_code_under_the_legal_shape(string standardConformingStrings)
    {
        await using var connection = new NpgsqlConnection(Environment.GetEnvironmentVariable(PostgresVariable));
        await connection.OpenAsync();
        await Prepare(connection);
        await Execute(connection, $"SET standard_conforming_strings = {standardConformingStrings}");

        await Compare(connection, SqlDialect.PostgreSql, $"standard_conforming_strings = {standardConformingStrings}",
        [
            new("single quotes", "SELECT Name FROM Users WHERE Name = '{0}' AND City = '{1}'", "alice", "Paris"),
            new("escape strings", "SELECT Name FROM Users WHERE Name = E'{0}' AND City = E'{1}'", "alice", "Paris"),
            new("dollar quotes", "SELECT Name FROM Users WHERE Name = $${0}$$ AND City = $${1}$$", "alice", "Paris"),
            new("number", "SELECT Name FROM Users WHERE Id = {0} AND City = '{1}'", "1", "Paris"),
        ]);
    }

    /// <summary>A command with two places for user input, and two legal values for them.</summary>
    private sealed record Template(string Name, string Sql, string First, string Second);

    private async Task Compare(DbConnection connection, SqlDialect dialect, string setting, Template[] templates)
    {
        var missed = new List<string>();
        var allInjected = 0;

        foreach (var template in templates)
        {
            var legal = _normalizer.Normalize(string.Format(template.Sql, template.First, template.Second), dialect);
            var random = new Random(20261118);
            int injected = 0, literal = 0, falseAlarms = 0, errors = 0;

            for (var n = 0; n < CommandsPerTemplate; n++)
            {
                var sql = string.Format(template.Sql, Value(random), Value(random));
                var rows = await CountRows(connection, sql);
                var sameShape = _normalizer.Normalize(sql, dialect).SequenceEqual(legal);

                if (rows is null)
                {
                    errors++;
                }
                else if (rows > LegalRows)
                {
                    injected++;
                    if (sameShape)
                    {
                        missed.Add($"[{template.Name}] {sql}");
                    }
                }
                else
                {
                    literal++;
                    falseAlarms += sameShape ? 0 : 1;
                }
            }

            output.WriteLine(
                $"{dialect} ({setting}), {template.Name}: {injected} injections, {literal} commands without one " +
                $"({falseAlarms} of them with another shape), {errors} errors");

            allInjected += injected;
        }

        // The random commands must contain real injections, or the test proves nothing.
        Assert.True(allInjected >= 100, $"Only {allInjected} injections.");

        Assert.True(missed.Count == 0, $"{missed.Count} injections kept the legal shape, for example:\n{string.Join("\n", missed.Take(5))}");
    }

    private static string Value(Random random) =>
        string.Concat(Enumerable.Range(0, random.Next(1, 5)).Select(_ => Pieces[random.Next(Pieces.Length)]));

    private static async Task Prepare(DbConnection connection)
    {
        foreach (var sql in Setup)
        {
            await Execute(connection, sql);
        }
    }

    private static async Task Execute(DbConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>Returns the number of rows, or null when the server or the client rejects the command.</summary>
    private static async Task<int?> CountRows(DbConnection connection, string sql)
    {
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await using var reader = await command.ExecuteReaderAsync();

            var rows = 0;
            while (await reader.ReadAsync())
            {
                rows++;
            }

            return rows;
        }
        catch (Exception exception) when (exception is DbException or InvalidOperationException or NotSupportedException)
        {
            return null;
        }
    }
}
