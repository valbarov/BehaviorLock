namespace BehaviorLock.Tests;

public sealed class AccessGuardTests
{
    private static readonly ResourceAccess Known = ResourceAccess.Sql("SELECT * FROM Products");
    private static readonly ResourceAccess Unknown = ResourceAccess.Sql("SELECT * FROM Users");

    private readonly GuardContextAccessor _accessor = new();
    private readonly EndpointProfile _profile = new();
    private readonly CollectingSink _sink = new();

    public AccessGuardTests()
    {
        _profile.Allow("GET /products", Known);
        _accessor.Current = new GuardContext("GET /products");
    }

    [Fact]
    public void Learn_mode_adds_the_access_to_the_profile()
    {
        Guard(GuardMode.Learn).Check(Unknown);

        Assert.True(_profile.IsAllowed("GET /products", Unknown));
        Assert.Empty(_sink.Items);
    }

    [Fact]
    public void Enforce_mode_allows_a_known_access()
    {
        Guard(GuardMode.Enforce).Check(Known);

        Assert.Empty(_sink.Items);
    }

    [Fact]
    public void Enforce_mode_blocks_an_unknown_access_and_marks_the_request()
    {
        var exception = Assert.Throws<GuardViolationException>(() => Guard(GuardMode.Enforce).Check(Unknown));

        Assert.Equal(new GuardViolation("GET /products", Unknown, Blocked: true), exception.Violation);
        Assert.Same(exception.Violation, _accessor.Current!.Violation);
        Assert.Equal([exception.Violation], _sink.Items);
    }

    [Fact]
    public void Enforce_mode_blocks_every_access_of_an_endpoint_without_a_profile()
    {
        _accessor.Current = new GuardContext("GET /new-endpoint");

        Assert.Throws<GuardViolationException>(() => Guard(GuardMode.Enforce).Check(Known));
    }

    [Fact]
    public void Audit_mode_reports_an_unknown_access_without_blocking()
    {
        Guard(GuardMode.Audit).Check(Unknown);

        Assert.Equal([new GuardViolation("GET /products", Unknown, Blocked: false)], _sink.Items);
        Assert.Null(_accessor.Current!.Violation);
    }

    [Theory]
    [InlineData(GuardMode.Off)]
    [InlineData(GuardMode.Enforce)]
    public void Access_is_ignored_when_the_guard_is_off_or_outside_a_request(GuardMode mode)
    {
        if (mode != GuardMode.Off)
        {
            _accessor.Current = null;
        }

        Guard(mode).Check(Unknown);

        Assert.Empty(_sink.Items);
        Assert.False(Guard(mode).IsActive);
    }

    [Theory]
    [InlineData(GuardMode.Learn)]
    [InlineData(GuardMode.Audit)]
    [InlineData(GuardMode.Enforce)]
    public void Guard_is_active_inside_a_request_in_every_mode_but_off(GuardMode mode)
    {
        Assert.True(Guard(mode).IsActive);
    }

    private AccessGuard Guard(GuardMode mode) =>
        new(_accessor, _profile, [_sink], new LiveSettings(mode));
}
