using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace BehaviorLock.Tests;

/// <summary>When the guard would ignore an access, the interceptor and the handler must not do the work of building it.</summary>
public sealed class InactiveGuardTests
{
    private readonly GuardContextAccessor _accessor = new() { Current = new GuardContext("GET /products") };
    private readonly CountingNormalizer _normalizer = new();

    [Fact]
    public void Off_mode_does_not_normalize_sql()
    {
        Execute(GuardMode.Off);

        Assert.Equal(0, _normalizer.Calls);
    }

    [Fact]
    public void Sql_outside_a_request_is_not_normalized()
    {
        _accessor.Current = null;

        Execute(GuardMode.Enforce);

        Assert.Equal(0, _normalizer.Calls);
    }

    [Fact]
    public void Sql_inside_a_request_is_normalized_and_checked()
    {
        Assert.Throws<GuardViolationException>(() => Execute(GuardMode.Enforce));

        Assert.Equal(1, _normalizer.Calls);
    }

    [Fact]
    public async Task Off_mode_lets_an_outbound_call_pass()
    {
        using var client = new HttpClient(new GuardHttpHandler(Guard(GuardMode.Off)) { InnerHandler = new StubHttpHandler() });

        var response = await client.GetAsync("http://169.254.169.254/latest/meta-data");

        Assert.True(response.IsSuccessStatusCode);
    }

    private void Execute(GuardMode mode)
    {
        using var command = new SqliteCommand("SELECT * FROM Users");
        new GuardDbCommandInterceptor(Guard(mode), _normalizer).ReaderExecuting(command, null!, default);
    }

    private AccessGuard Guard(GuardMode mode) =>
        new(_accessor, new EndpointProfile(), [], Options.Create(new BehaviorLockOptions { Mode = mode }));

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
