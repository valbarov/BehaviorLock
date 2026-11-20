namespace BehaviorLock;

/// <summary>
/// How a database reads strings and comments. The normalizer must find the end of each string exactly
/// where the database finds it. If it does not, an attacker can hide code in the part between the two ends.
/// </summary>
public enum SqlDialect
{
    /// <summary>
    /// The database is not known. Text that databases read in different ways is not normalized:
    /// the command is kept as it is.
    /// </summary>
    Unknown,

    /// <summary>Standard strings and comments, for example SQLite and Oracle.</summary>
    Standard,

    /// <summary>SQL Server: standard strings, and comments inside comments.</summary>
    SqlServer,

    /// <summary>PostgreSQL: dollar-quoted strings, comments inside comments, and backslash escapes that depend on a setting.</summary>
    PostgreSql,

    /// <summary>MySQL and MariaDB: <c>#</c> comments, and backslash escapes that depend on a setting.</summary>
    MySql,
}

internal static class SqlDialects
{
    /// <summary>
    /// Returns the dialect of an EF Core provider, for example <c>Npgsql.EntityFrameworkCore.PostgreSQL</c>.
    /// A provider that is not known gives <see cref="SqlDialect.Unknown"/>.
    /// </summary>
    public static SqlDialect FromProvider(string? provider)
    {
        if (provider is null)
        {
            return SqlDialect.Unknown;
        }

        if (Has("MySql") || Has("MariaDb"))
        {
            return SqlDialect.MySql;
        }

        if (Has("Npgsql") || Has("Postgre"))
        {
            return SqlDialect.PostgreSql;
        }

        if (Has("SqlServer"))
        {
            return SqlDialect.SqlServer;
        }

        return Has("Sqlite") || Has("Oracle") ? SqlDialect.Standard : SqlDialect.Unknown;

        bool Has(string part) => provider.Contains(part, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>Whether a database reads some text in a certain way.</summary>
internal enum Reads
{
    No,
    Yes,

    /// <summary>It depends on a setting of the server, or the database is not known.</summary>
    Unsure,
}

/// <summary>The points where databases differ in how they read strings and comments.</summary>
internal readonly record struct DialectRules(
    Reads BackslashEscapes,
    Reads DollarQuotes,
    Reads HashComments,
    Reads NestedComments)
{
    public static DialectRules For(SqlDialect dialect) => dialect switch
    {
        SqlDialect.Standard => new(Reads.No, Reads.No, Reads.No, Reads.No),
        SqlDialect.SqlServer => new(Reads.No, Reads.No, Reads.No, Reads.Yes),

        // Backslash escapes: on in E'...' strings, and in all strings when standard_conforming_strings is off.
        SqlDialect.PostgreSql => new(Reads.Unsure, Reads.Yes, Reads.No, Reads.Yes),

        // Backslash escapes: on by default, off with the SQL mode NO_BACKSLASH_ESCAPES.
        SqlDialect.MySql => new(Reads.Unsure, Reads.No, Reads.Yes, Reads.No),

        _ => new(Reads.Unsure, Reads.Unsure, Reads.Unsure, Reads.Unsure),
    };
}
