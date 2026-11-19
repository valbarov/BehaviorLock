namespace BehaviorLock.Tests;

public sealed class SqlNormalizerTests
{
    private readonly SqlNormalizer _normalizer = new();

    [Theory]
    [InlineData("SELECT * FROM T WHERE a = 'it''s' AND b = 12.5", "SELECT * FROM T WHERE a = ? AND b = ?")]
    [InlineData("SELECT \"p\".\"Id\" FROM \"Products\" AS \"p\" WHERE \"p\".\"Id\" = @p0", "SELECT \"p\".\"Id\" FROM \"Products\" AS \"p\" WHERE \"p\".\"Id\" = ?")]
    [InlineData("SELECT t0.Col1\r\n  FROM   Orders2 AS t0", "SELECT t0.Col1 FROM Orders2 AS t0")]
    [InlineData("SELECT @@ROWCOUNT, x::int FROM T WHERE y = $1 AND z = :name", "SELECT @@ROWCOUNT, x::int FROM T WHERE y = ? AND z = ?")]
    public void Literals_and_parameters_become_placeholders(string sql, string expected)
    {
        Assert.Equal([expected], _normalizer.Normalize(sql));
    }

    [Fact]
    public void Parameter_lists_do_not_depend_on_the_number_of_parameters()
    {
        Assert.Equal(
            _normalizer.Normalize("SELECT * FROM T WHERE Id IN (@a)"),
            _normalizer.Normalize("SELECT * FROM T WHERE Id IN (@a, @b, @c)"));

        Assert.Equal(
            _normalizer.Normalize("INSERT INTO T (A, B) VALUES (@p0, @p1)"),
            _normalizer.Normalize("INSERT INTO T (A, B) VALUES (@p0, @p1), (@p2, @p3), (@p4, @p5)"));
    }

    [Fact]
    public void A_batch_is_split_into_statements()
    {
        var statements = _normalizer.Normalize("UPDATE T SET A = @p0 WHERE Id = @p1; SELECT changes();");

        Assert.Equal(["UPDATE T SET A = ? WHERE Id = ?", "SELECT changes()"], statements);
    }

    [Theory]
    [InlineData("x' OR '1'='1")]
    [InlineData("x' UNION SELECT Id, Email, 0 FROM Users --")]
    [InlineData("x'; DROP TABLE Products; --")]
    [InlineData("x' /* hidden */ OR 1=1 --")]
    public void An_injection_changes_the_shape(string input)
    {
        static string Query(string name) => $"SELECT * FROM Products WHERE Name LIKE '%{name}%'";

        Assert.NotEqual(_normalizer.Normalize(Query("Laptop")), _normalizer.Normalize(Query(input)));
    }

    [Fact]
    public void A_quote_inside_a_comment_does_not_start_a_literal()
    {
        var statements = _normalizer.Normalize("SELECT 1 -- it's a comment\nFROM T WHERE a = 'x'");

        Assert.Equal(["SELECT ? -- it's a comment FROM T WHERE a = ?"], statements);
    }
}
