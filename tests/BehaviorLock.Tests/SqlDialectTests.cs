namespace BehaviorLock.Tests;

/// <summary>
/// The normalizer must find the end of each string where the database finds it.
/// If it finds another end, it replaces code by a placeholder, and an injection keeps the legal shape.
/// </summary>
public sealed class SqlDialectTests
{
    private const string TwoValues = "SELECT * FROM Users WHERE Name = '{0}' AND City = '{1}'";
    private const string OneValue = "SELECT * FROM Users WHERE Name = '{0}'";

    private readonly SqlNormalizer _normalizer = new();

    public static TheoryData<SqlDialect> BackslashDialects => [SqlDialect.MySql, SqlDialect.PostgreSql, SqlDialect.Unknown];

    [Theory]
    [MemberData(nameof(BackslashDialects))]
    public void A_backslash_that_hides_code_in_two_values_changes_the_shape(SqlDialect dialect)
    {
        // With backslash escapes the database reads '\' AND City = ' as one string, and then runs OR 1=1.
        var attack = string.Format(TwoValues, @"\", " OR 1=1 -- ");

        Assert.NotEqual(Shape(TwoValues, dialect, "alice", "Paris"), _normalizer.Normalize(attack, dialect));
        Assert.Equal([attack], _normalizer.Normalize(attack, dialect));
    }

    [Theory]
    [MemberData(nameof(BackslashDialects))]
    public void A_backslash_that_hides_code_in_one_value_changes_the_shape(SqlDialect dialect)
    {
        // Without backslash escapes the database reads 'a\' as one string, and then runs OR 1=1.
        var attack = string.Format(OneValue, @"a\' OR 1=1 -- ");

        Assert.NotEqual(Shape(OneValue, dialect, "alice"), _normalizer.Normalize(attack, dialect));
        Assert.Equal([attack], _normalizer.Normalize(attack, dialect));
    }

    [Theory]
    [InlineData(SqlDialect.Standard)]
    [InlineData(SqlDialect.SqlServer)]
    public void A_backslash_is_a_normal_character_without_backslash_escapes(SqlDialect dialect)
    {
        // These databases read '\' as a complete string, so the text below is no injection for them.
        var sql = string.Format(TwoValues, @"\", " OR 1=1 -- ");

        Assert.Equal(Shape(TwoValues, dialect, "alice", "Paris"), _normalizer.Normalize(sql, dialect));
    }

    [Theory]
    [InlineData(SqlDialect.MySql, @"C:\temp\file")]
    [InlineData(SqlDialect.MySql, @"ends with an escaped backslash \\")]
    [InlineData(SqlDialect.PostgreSql, @"100\% sure")]
    [InlineData(SqlDialect.Unknown, @"line\nbreak")]
    public void A_backslash_that_does_not_move_the_end_of_the_string_is_normalized(SqlDialect dialect, string value)
    {
        Assert.Equal(Shape(OneValue, dialect, "alice"), Shape(OneValue, dialect, value));
    }

    [Fact]
    public void A_double_quoted_text_with_a_doubtful_end_is_not_normalized_in_mysql()
    {
        // MySQL reads "..." as a string by default, with backslash escapes.
        const string attack = "SELECT * FROM Users WHERE Name = \"a\\\" OR 1=1 -- \"";

        Assert.Equal([attack], _normalizer.Normalize(attack, SqlDialect.MySql));
    }

    [Fact]
    public void A_hash_starts_a_comment_in_mysql()
    {
        var statements = _normalizer.Normalize("SELECT 1 # it's a comment\nFROM T WHERE a = 'x'", SqlDialect.MySql);

        Assert.Equal(["SELECT ? # it's a comment FROM T WHERE a = ?"], statements);
    }

    [Fact]
    public void A_hash_is_a_normal_character_in_other_databases()
    {
        Assert.Equal(["SELECT * FROM #tmp WHERE a = ?"], _normalizer.Normalize("SELECT * FROM #tmp WHERE a = 'x'", SqlDialect.SqlServer));
        Assert.Equal(["SELECT data #> ? FROM T"], _normalizer.Normalize("SELECT data #> '{a}' FROM T", SqlDialect.PostgreSql));
    }

    [Fact]
    public void A_dollar_quoted_string_is_a_literal_in_postgresql()
    {
        var statements = _normalizer.Normalize("SELECT $$it's$$, $tag$ a $$ b $tag$ FROM T", SqlDialect.PostgreSql);

        Assert.Equal(["SELECT ?, ? FROM T"], statements);
    }

    [Fact]
    public void Code_after_a_dollar_quoted_string_changes_the_shape_in_postgresql()
    {
        const string query = "SELECT * FROM T WHERE a = $${0}$$";

        // The database ends the string at the second $$ and runs OR 1=1.
        var attack = _normalizer.Normalize(string.Format(query, "' $$ OR 1=1 -- '"), SqlDialect.PostgreSql);

        Assert.NotEqual(_normalizer.Normalize(string.Format(query, "5"), SqlDialect.PostgreSql), attack);
        Assert.Contains("OR ?=?", Assert.Single(attack));
    }

    [Fact]
    public void A_dollar_inside_a_name_starts_no_string_in_postgresql()
    {
        var statements = _normalizer.Normalize("SELECT a$b$c FROM T WHERE x = 'y'", SqlDialect.PostgreSql);

        Assert.Equal(["SELECT a$b$c FROM T WHERE x = ?"], statements);
    }

    [Theory]
    [InlineData(SqlDialect.PostgreSql)]
    [InlineData(SqlDialect.SqlServer)]
    public void A_comment_inside_a_comment_ends_with_the_outer_comment(SqlDialect dialect)
    {
        var statements = _normalizer.Normalize("SELECT 1 /* a /* b */ it's */ FROM T WHERE x = 'y'", dialect);

        Assert.Equal(["SELECT ? /* a /* b */ it's */ FROM T WHERE x = ?"], statements);
    }

    [Theory]
    [InlineData(SqlDialect.MySql)]
    [InlineData(SqlDialect.Standard)]
    public void A_comment_ends_at_the_first_end_mark_without_nested_comments(SqlDialect dialect)
    {
        var statements = _normalizer.Normalize("SELECT 1 /* a /* b */ + 2 FROM T", dialect);

        Assert.Equal(["SELECT ? /* a /* b */ + ? FROM T"], statements);
    }

    [Theory]
    [InlineData("SELECT * FROM T WHERE a = $$x$$")]
    [InlineData("SELECT 1 # a comment, or not")]
    [InlineData("SELECT 1 /* a /* b */ c */ FROM T")]
    [InlineData(@"SELECT * FROM T WHERE a = 'x\' OR 1=1 -- '")]
    public void An_unknown_database_keeps_a_command_that_databases_read_in_different_ways(string sql)
    {
        Assert.Equal([sql], _normalizer.Normalize(sql, SqlDialect.Unknown));
    }

    [Fact]
    public void A_command_that_is_not_normalized_is_one_statement()
    {
        const string sql = @"UPDATE T SET a = 1; SELECT * FROM T WHERE b = 'x\' OR c = 'y'";

        Assert.Equal([sql], _normalizer.Normalize(sql, SqlDialect.MySql));
    }

    [Fact]
    public void A_row_of_literals_that_an_injection_adds_changes_the_shape()
    {
        const string insert = "INSERT INTO Users (Name, IsAdmin) VALUES ('{0}', 0)";

        // The value closes its row and adds a second row with IsAdmin = 1.
        var attack = _normalizer.Normalize(string.Format(insert, "a', 1), ('b"), SqlDialect.Standard);

        Assert.NotEqual(_normalizer.Normalize(string.Format(insert, "alice"), SqlDialect.Standard), attack);
        Assert.Equal(["INSERT INTO Users (Name, IsAdmin) VALUES (?, ?), (?, ?)"], attack);
    }

    [Fact]
    public void A_list_with_a_literal_keeps_its_length()
    {
        Assert.Equal(["SELECT * FROM T WHERE Id IN (?, ?, ?)"], _normalizer.Normalize("SELECT * FROM T WHERE Id IN (@a, @b, 3)"));
        Assert.Equal(["SELECT * FROM T WHERE Id IN (?, ?)"], _normalizer.Normalize("SELECT * FROM T WHERE Id IN ('a', 'b')"));
    }

    [Fact]
    public void The_internal_parameter_mark_in_the_input_is_no_parameter()
    {
        var statements = _normalizer.Normalize("SELECT * FROM T WHERE Id IN (\u0001, \u0001)");

        Assert.DoesNotContain("(?)", Assert.Single(statements));
    }

    [Theory]
    [InlineData("12")]
    [InlineData("1.5")]
    [InlineData("1e5")]
    [InlineData("1.5E-3")]
    [InlineData("0x1F")]
    public void A_number_becomes_one_placeholder(string number)
    {
        Assert.Equal(["SELECT * FROM T WHERE a = ?"], _normalizer.Normalize($"SELECT * FROM T WHERE a = {number}"));
    }

    [Fact]
    public void Letters_directly_after_a_number_stay_in_the_shape()
    {
        Assert.Equal(["SELECT * FROM T WHERE a = ?OR b"], _normalizer.Normalize("SELECT * FROM T WHERE a = 1OR b"));
    }

    [Theory]
    [InlineData("Microsoft.EntityFrameworkCore.Sqlite 11.0.0", SqlDialect.Standard)]
    [InlineData("Oracle.EntityFrameworkCore", SqlDialect.Standard)]
    [InlineData("Microsoft.EntityFrameworkCore.SqlServer 11.0.0", SqlDialect.SqlServer)]
    [InlineData("Npgsql.EntityFrameworkCore.PostgreSQL 11.0.0", SqlDialect.PostgreSql)]
    [InlineData("Pomelo.EntityFrameworkCore.MySql 9.0.0", SqlDialect.MySql)]
    [InlineData("MySql.EntityFrameworkCore", SqlDialect.MySql)]
    [InlineData("FirebirdSql.EntityFrameworkCore.Firebird", SqlDialect.Unknown)]
    [InlineData(null, SqlDialect.Unknown)]
    public void The_dialect_comes_from_the_provider(string? provider, SqlDialect expected)
    {
        Assert.Equal(expected, SqlDialects.FromProvider(provider));
    }

    private IReadOnlyList<string> Shape(string query, SqlDialect dialect, params object[] values) =>
        _normalizer.Normalize(string.Format(query, values), dialect);
}
