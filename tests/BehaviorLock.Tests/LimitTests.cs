using Microsoft.Extensions.Options;

namespace BehaviorLock.Tests;

/// <summary>
/// A limit is an access with a number. The profile keeps the highest number, and every number up to it passes.
/// </summary>
public sealed class LimitTests : IDisposable
{
    private const string Endpoint = "GET /products";
    private const string Shape = "SELECT * FROM Products";

    private readonly string _directory = Directory.CreateTempSubdirectory("behaviorlock-").FullName;
    private readonly GuardContextAccessor _accessor = new() { Current = new GuardContext(Endpoint) };
    private readonly EndpointProfile _profile = new();
    private readonly CollectingSink _sink = new();

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void A_limit_has_a_readable_form()
    {
        Assert.Equal("rows: up to 100 from SELECT * FROM Products", ResourceAccess.Rows(Shape, 100).ToString());
        Assert.Equal("response: up to 2500 bytes", ResourceAccess.ResponseSize(2500).ToString());
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(2, 10)]
    [InlineData(10, 10)]
    [InlineData(11, 100)]
    [InlineData(999_999, 1_000_000)]
    [InlineData(long.MaxValue, long.MaxValue)]
    public void A_limit_is_rounded_up_to_the_next_step(long number, long expected)
    {
        Assert.Equal(expected, ResourceAccess.ResponseSize(number).RoundedUp().UpTo);
    }

    [Fact]
    public void Rounding_does_not_change_an_access_without_a_number()
    {
        var access = ResourceAccess.Sql(Shape);

        Assert.Same(access, access.RoundedUp());
    }

    [Fact]
    public void The_profile_allows_every_number_up_to_the_limit()
    {
        _profile.Allow(Endpoint, ResourceAccess.Rows(Shape, 100));

        Assert.True(_profile.IsAllowed(Endpoint, ResourceAccess.Rows(Shape, 0)));
        Assert.True(_profile.IsAllowed(Endpoint, ResourceAccess.Rows(Shape, 100)));
        Assert.False(_profile.IsAllowed(Endpoint, ResourceAccess.Rows(Shape, 101)));
        Assert.Equal(100, _profile.LimitOf(Endpoint, ResourceAccess.Rows(Shape, 0)));
    }

    [Fact]
    public void A_limit_belongs_to_one_endpoint_and_one_statement()
    {
        _profile.Allow(Endpoint, ResourceAccess.Rows(Shape, 100));

        Assert.False(_profile.IsAllowed(Endpoint, ResourceAccess.Rows("SELECT * FROM Users", 1)));
        Assert.False(_profile.IsAllowed("GET /other", ResourceAccess.Rows(Shape, 1)));
        Assert.Null(_profile.LimitOf("GET /other", ResourceAccess.Rows(Shape, 0)));
    }

    [Fact]
    public void A_higher_number_raises_the_limit_and_a_lower_number_does_not_change_it()
    {
        _profile.Allow(Endpoint, ResourceAccess.Rows(Shape, 10));
        _profile.Allow(Endpoint, ResourceAccess.Rows(Shape, 1000));
        _profile.Allow(Endpoint, ResourceAccess.Rows(Shape, 100));

        Assert.Equal(["rows: up to 1000 from SELECT * FROM Products"], _profile.ToDictionary()[Endpoint]);
        Assert.Equal(1, _profile.AccessCount);
    }

    [Fact]
    public void A_saved_limit_is_loaded_again()
    {
        var store = new JsonFileProfileStore(Options.Create(new BehaviorLockOptions()), new ContentRoot(_directory));
        _profile.Allow(Endpoint, ResourceAccess.Sql(Shape));
        _profile.Allow(Endpoint, ResourceAccess.Rows(Shape, 100));
        _profile.Allow(Endpoint, ResourceAccess.ResponseSize(1000));

        store.Save(_profile);
        var loaded = store.Load();

        Assert.Equal(_profile.ToDictionary(), loaded.ToDictionary());
        Assert.True(loaded.IsAllowed(Endpoint, ResourceAccess.Rows(Shape, 42)));
        Assert.False(loaded.IsAllowed(Endpoint, ResourceAccess.ResponseSize(1001)));
    }

    [Fact]
    public void The_higher_limit_stays_when_two_profiles_are_merged()
    {
        _profile.Allow(Endpoint, ResourceAccess.Rows(Shape, 10));
        _profile.Allow(Endpoint, ResourceAccess.ResponseSize(1000));

        // The approved file raises one limit. Its lower limit does not lower the recorded one.
        var approved = new EndpointProfile(new Dictionary<string, string[]>
        {
            [Endpoint] = ["rows: up to 500 from SELECT * FROM Products", "response: up to 100 bytes"],
        });

        Assert.Equal(1, _profile.Merge(approved));
        Assert.Equal(500, _profile.LimitOf(Endpoint, ResourceAccess.Rows(Shape, 0)));
        Assert.Equal(1000, _profile.LimitOf(Endpoint, ResourceAccess.ResponseSize(0)));
    }

    [Fact]
    public void A_pending_limit_leaves_when_the_profile_allows_its_number()
    {
        var pending = new EndpointProfile(new Dictionary<string, string[]>
        {
            [Endpoint] = ["rows: up to 100 from SELECT * FROM Products", "response: up to 10000 bytes"],
        });
        _profile.Allow(Endpoint, ResourceAccess.Rows(Shape, 500));
        _profile.Allow(Endpoint, ResourceAccess.ResponseSize(1000));

        Assert.Equal(["response: up to 10000 bytes"], pending.Except(_profile).ToDictionary()[Endpoint]);
    }

    [Fact]
    public void Learn_mode_records_the_number_rounded_up()
    {
        Guard(GuardMode.Learn).Check(ResourceAccess.Rows(Shape, 37));

        Assert.Equal(["rows: up to 100 from SELECT * FROM Products"], _profile.ToDictionary()[Endpoint]);
    }

    [Fact]
    public void Enforce_mode_blocks_a_number_over_the_limit_and_reports_it_rounded_up()
    {
        _profile.Allow(Endpoint, ResourceAccess.Rows(Shape, 10));

        Guard(GuardMode.Enforce).Check(ResourceAccess.Rows(Shape, 10));
        var exception = Assert.Throws<GuardViolationException>(() => Guard(GuardMode.Enforce).Check(ResourceAccess.Rows(Shape, 37)));

        // The reported entry is the one that a person can copy to the approved file.
        Assert.Equal(new GuardViolation(Endpoint, ResourceAccess.Rows(Shape, 100), Blocked: true), exception.Violation);
        Assert.Equal([exception.Violation], _sink.Items);
    }

    [Fact]
    public void Audit_mode_reports_a_number_over_the_limit_without_blocking()
    {
        _profile.Allow(Endpoint, ResourceAccess.Rows(Shape, 10));

        Guard(GuardMode.Audit).Check(ResourceAccess.Rows(Shape, 37));

        Assert.Equal([new GuardViolation(Endpoint, ResourceAccess.Rows(Shape, 100), Blocked: false)], _sink.Items);
    }

    [Theory]
    [InlineData(GuardMode.Audit)]
    [InlineData(GuardMode.Enforce)]
    public void A_limit_that_the_profile_does_not_have_is_not_checked(GuardMode mode)
    {
        // The statement is allowed, but nobody recorded how many rows it returns.
        _profile.Allow(Endpoint, ResourceAccess.Sql(Shape));

        Guard(mode).Check(ResourceAccess.Rows(Shape, 1_000_000));

        Assert.Empty(_sink.Items);
    }

    [Theory]
    [InlineData(GuardMode.Audit)]
    [InlineData(GuardMode.Enforce)]
    public void A_caller_is_not_checked_when_the_profile_has_no_caller(GuardMode mode)
    {
        _profile.Allow(Endpoint, ResourceAccess.Sql(Shape));

        Guard(mode).Check(new ResourceAccess(ResourceAccess.CallerKind, "anonymous"));

        Assert.Empty(_sink.Items);
    }

    [Fact]
    public void A_caller_is_checked_when_the_profile_has_another_caller()
    {
        _profile.Allow(Endpoint, new ResourceAccess(ResourceAccess.CallerKind, "signed in, role admin"));

        Assert.Throws<GuardViolationException>(
            () => Guard(GuardMode.Enforce).Check(new ResourceAccess(ResourceAccess.CallerKind, "anonymous")));
    }

    [Fact]
    public void The_pending_file_keeps_one_entry_per_limit()
    {
        var path = Path.Combine(_directory, "behavior.lock.pending.json");
        var options = Options.Create(new BehaviorLockOptions { PendingPath = path });
        var pending = new PendingAccesses(
            options, new ContentRoot(_directory), _profile, Microsoft.Extensions.Logging.Abstractions.NullLogger<PendingAccesses>.Instance);

        // An attacker asks for more and more rows. The file must not get one entry per request.
        foreach (var rows in new[] { 100, 10_000, 1000 })
        {
            pending.Report(new GuardViolation(Endpoint, ResourceAccess.Rows(Shape, rows), Blocked: true));
        }

        pending.Write();

        Assert.Contains("\"rows: up to 10000 from SELECT * FROM Products\"", File.ReadAllText(path));
        Assert.DoesNotContain("up to 100 ", File.ReadAllText(path));
    }

    private AccessGuard Guard(GuardMode mode) =>
        new(_accessor, _profile, [_sink], Options.Create(new BehaviorLockOptions { Mode = mode }));
}
