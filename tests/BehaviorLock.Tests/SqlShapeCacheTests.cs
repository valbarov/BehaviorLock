using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SampleApi;

namespace BehaviorLock.Tests;

/// <summary>
/// The interceptor keeps the shapes of the command texts that passed, so it normalizes a text once.
/// The cache must not change what the guard checks, and it must not grow without end.
/// </summary>
public sealed class SqlShapeCacheTests
{
    private const string Endpoint = "GET /products";
    private const string Legal = "SELECT * FROM Products WHERE Id = 1";
    private const string LegalShape = "sql: SELECT * FROM Products WHERE Id = ?";

    private readonly GuardContextAccessor _accessor = new() { Current = new GuardContext(Endpoint) };
    private readonly EndpointProfile _profile = new();
    private readonly CollectingSink _sink = new();
    private readonly CountingNormalizer _normalizer = new();

    [Fact]
    public void A_command_text_is_normalized_once()
    {
        var interceptor = Interceptor(GuardMode.Learn);

        for (var i = 0; i < 3; i++)
        {
            Execute(interceptor, Legal);
        }

        Assert.Equal(1, _normalizer.Calls);
        Assert.Equal([LegalShape], _profile.ToDictionary()[Endpoint]);
    }

    [Fact]
    public void Another_text_with_the_same_shape_is_normalized_too()
    {
        var interceptor = Interceptor(GuardMode.Learn);

        Execute(interceptor, Legal);
        Execute(interceptor, "SELECT * FROM Products WHERE Id = 2");

        Assert.Equal(2, _normalizer.Calls);
        Assert.Equal([LegalShape], _profile.ToDictionary()[Endpoint]);
    }

    [Fact]
    public void A_known_text_is_still_checked_for_each_endpoint()
    {
        var interceptor = Interceptor(GuardMode.Enforce);
        _profile.Allow(Endpoint, ResourceAccess.Sql("SELECT * FROM Products WHERE Id = ?"));

        Execute(interceptor, Legal);

        // The text is known now, but this endpoint must not send it.
        _accessor.Current = new GuardContext("GET /other");
        Assert.Throws<GuardViolationException>(() => Execute(interceptor, Legal));
        Assert.Equal(1, _normalizer.Calls);
    }

    [Fact]
    public void A_known_text_is_reported_each_time_in_audit_mode()
    {
        var interceptor = Interceptor(GuardMode.Audit);

        Execute(interceptor, Legal);
        Execute(interceptor, Legal);

        Assert.Equal(1, _normalizer.Calls);
        Assert.Equal(2, _sink.Items.Count);
    }

    [Fact]
    public void A_blocked_command_is_not_kept()
    {
        var interceptor = Interceptor(GuardMode.Enforce);

        // An attacker sends a new text with each request. None of them may take room in the cache.
        Assert.Throws<GuardViolationException>(() => Execute(interceptor, Legal));
        Assert.Throws<GuardViolationException>(() => Execute(interceptor, Legal));

        Assert.Equal(2, _normalizer.Calls);
    }

    [Fact]
    public void A_full_cache_starts_again_from_empty()
    {
        var interceptor = Interceptor(GuardMode.Learn, size: 3);

        foreach (var id in new[] { 1, 2, 3, 1, 2, 3 })
        {
            Execute(interceptor, $"SELECT * FROM Products WHERE Id = {id}");
        }

        Assert.Equal(3, _normalizer.Calls);

        // The fourth text does not fit. The cache drops everything, so the first text is new again.
        Execute(interceptor, "SELECT * FROM Products WHERE Id = 4");
        Execute(interceptor, "SELECT * FROM Products WHERE Id = 1");
        Execute(interceptor, "SELECT * FROM Products WHERE Id = 1");

        Assert.Equal(5, _normalizer.Calls);
    }

    [Fact]
    public void A_very_long_text_is_not_kept()
    {
        var interceptor = Interceptor(GuardMode.Learn);
        var longText = $"SELECT * FROM Products WHERE Name = '{new string('x', 20_000)}'";

        Execute(interceptor, longText);
        Execute(interceptor, longText);

        Assert.Equal(2, _normalizer.Calls);
    }

    [Fact]
    public void Size_zero_turns_the_cache_off()
    {
        var interceptor = Interceptor(GuardMode.Learn, size: 0);

        Execute(interceptor, Legal);
        Execute(interceptor, Legal);

        Assert.Equal(2, _normalizer.Calls);
    }

    [Fact]
    public void The_row_check_uses_the_kept_shape()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var options = new DbContextOptionsBuilder<ShopDb>()
            .UseSqlite(connection)
            .AddInterceptors(Interceptor(GuardMode.Learn))
            .Options;
        using var db = new ShopDb(options);

        // Outside a request the guard does nothing, so the setup is not normalized.
        _accessor.Current = null;
        db.Database.EnsureCreated();
        Assert.Equal(0, _normalizer.Calls);

        _accessor.Current = new GuardContext(Endpoint);
        for (var i = 0; i < 3; i++)
        {
            _ = db.Products.AsNoTracking().ToList();
        }

        // One call for three commands, and each command was checked two times: before it ran and at the end.
        Assert.Equal(1, _normalizer.Calls);
        var recorded = _profile.ToDictionary()[Endpoint];
        Assert.Single(recorded, entry => entry.StartsWith("sql: SELECT"));
        Assert.Single(recorded, entry => entry.StartsWith("rows: up to "));
    }

    private static void Execute(GuardDbCommandInterceptor interceptor, string sql)
    {
        using var command = new SqliteCommand(sql);
        interceptor.ReaderExecuting(command, null!, default);
    }

    private GuardDbCommandInterceptor Interceptor(GuardMode mode, int? size = null)
    {
        var options = new BehaviorLockOptions { Mode = mode };
        if (size is { } number)
        {
            options.SqlShapeCacheSize = number;
        }

        var wrapped = Options.Create(options);
        return new GuardDbCommandInterceptor(new AccessGuard(_accessor, _profile, [_sink], wrapped), _normalizer, options: wrapped);
    }

    private sealed class CountingNormalizer : ISqlNormalizer
    {
        private readonly SqlNormalizer _inner = new();

        public int Calls { get; private set; }

        public IReadOnlyList<string> Normalize(string sql, SqlDialect dialect = SqlDialect.Unknown)
        {
            Calls++;
            return _inner.Normalize(sql, dialect);
        }
    }
}
