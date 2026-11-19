using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Options;

namespace BehaviorLock.Tests;

public sealed class GuardModesTests
{
    private static readonly ResourceAccess Unknown = ResourceAccess.Sql("SELECT * FROM Users");

    private readonly GuardContextAccessor _accessor = new();
    private readonly CollectingSink _sink = new();

    [Theory]
    // Global Off is a kill switch.
    [InlineData(GuardMode.Off, null, GuardMode.Off)]
    [InlineData(GuardMode.Off, GuardMode.Off, GuardMode.Off)]
    [InlineData(GuardMode.Off, GuardMode.Audit, GuardMode.Off)]
    [InlineData(GuardMode.Off, GuardMode.Enforce, GuardMode.Off)]
    // Global Learn records every endpoint.
    [InlineData(GuardMode.Learn, null, GuardMode.Learn)]
    [InlineData(GuardMode.Learn, GuardMode.Off, GuardMode.Learn)]
    [InlineData(GuardMode.Learn, GuardMode.Audit, GuardMode.Learn)]
    [InlineData(GuardMode.Learn, GuardMode.Enforce, GuardMode.Learn)]
    // Global Audit or Enforce: the endpoint mode wins, stricter or weaker.
    [InlineData(GuardMode.Audit, null, GuardMode.Audit)]
    [InlineData(GuardMode.Audit, GuardMode.Off, GuardMode.Off)]
    [InlineData(GuardMode.Audit, GuardMode.Audit, GuardMode.Audit)]
    [InlineData(GuardMode.Audit, GuardMode.Enforce, GuardMode.Enforce)]
    [InlineData(GuardMode.Enforce, null, GuardMode.Enforce)]
    [InlineData(GuardMode.Enforce, GuardMode.Off, GuardMode.Off)]
    [InlineData(GuardMode.Enforce, GuardMode.Audit, GuardMode.Audit)]
    [InlineData(GuardMode.Enforce, GuardMode.Enforce, GuardMode.Enforce)]
    public void Effective_mode_follows_the_precedence_rules(GuardMode global, GuardMode? endpoint, GuardMode expected)
    {
        Assert.Equal(expected, GuardModes.Effective(global, endpoint));
    }

    [Theory]
    [InlineData(GuardMode.Audit)]
    [InlineData(GuardMode.Enforce)]
    public void Effective_mode_rejects_learn_as_endpoint_mode(GuardMode global)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => GuardModes.Effective(global, GuardMode.Learn));
    }

    [Theory]
    [InlineData(GuardMode.Learn)]
    [InlineData((GuardMode)42)]
    public void Attribute_rejects_a_mode_that_is_not_legal_per_endpoint(GuardMode mode)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new BehaviorLockAttribute(mode));
    }

    [Theory]
    [InlineData(GuardMode.Off)]
    [InlineData(GuardMode.Audit)]
    [InlineData(GuardMode.Enforce)]
    public void Attribute_keeps_a_legal_mode(GuardMode mode)
    {
        Assert.Equal(mode, new BehaviorLockAttribute(mode).Mode);
    }

    [Fact]
    public void Extension_rejects_learn_and_adds_no_metadata()
    {
        var builder = new RecordingConventionBuilder();

        Assert.Throws<ArgumentOutOfRangeException>(() => builder.WithBehaviorLock(GuardMode.Learn));
        Assert.Equal(0, builder.Conventions);
    }

    [Fact]
    public void Context_mode_enforce_blocks_under_global_audit()
    {
        _accessor.Current = new GuardContext("GET /products", GuardMode.Enforce);

        var exception = Assert.Throws<GuardViolationException>(() => Guard(GuardMode.Audit).Check(Unknown));

        Assert.True(exception.Violation.Blocked);
        Assert.Equal([exception.Violation], _sink.Items);
    }

    [Fact]
    public void Context_mode_audit_reports_without_blocking_under_global_enforce()
    {
        _accessor.Current = new GuardContext("GET /products", GuardMode.Audit);

        Guard(GuardMode.Enforce).Check(Unknown);

        Assert.Equal([new GuardViolation("GET /products", Unknown, Blocked: false)], _sink.Items);
        Assert.Null(_accessor.Current.Violation);
    }

    [Fact]
    public void Context_mode_off_makes_the_guard_inactive_under_global_enforce()
    {
        _accessor.Current = new GuardContext("GET /products", GuardMode.Off);
        var guard = Guard(GuardMode.Enforce);

        guard.Check(Unknown);

        Assert.False(guard.IsActive);
        Assert.Empty(_sink.Items);
    }

    [Fact]
    public void Global_off_beats_the_mode_of_a_context()
    {
        _accessor.Current = new GuardContext("GET /products", GuardMode.Enforce);
        var guard = Guard(GuardMode.Off);

        guard.Check(Unknown);

        Assert.False(guard.IsActive);
        Assert.Empty(_sink.Items);
    }

    private AccessGuard Guard(GuardMode global) =>
        new(_accessor, new EndpointProfile(), [_sink], Options.Create(new BehaviorLockOptions { Mode = global }));

    private sealed class RecordingConventionBuilder : IEndpointConventionBuilder
    {
        public int Conventions { get; private set; }

        public void Add(Action<EndpointBuilder> convention) => Conventions++;
    }
}
