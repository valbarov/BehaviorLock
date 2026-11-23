using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BehaviorLock.Tests;

/// <summary>
/// A scope opens a guard context for code outside a request, for example a background job.
/// The name of the scope is its key in the profile.
/// </summary>
public sealed class GuardScopeTests : IDisposable
{
    private const string Job = "job nightly-cleanup";

    private static readonly ResourceAccess Known = ResourceAccess.Sql("DELETE FROM Orders WHERE Total = ?");
    private static readonly ResourceAccess Unknown = ResourceAccess.Sql("SELECT * FROM Users");

    private readonly string _directory = Directory.CreateTempSubdirectory("behaviorlock-").FullName;
    private readonly GuardContextAccessor _accessor = new();
    private readonly EndpointProfile _profile = new();
    private readonly CollectingSink _sink = new();

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void Code_outside_a_scope_is_not_checked()
    {
        var guard = Guard(GuardMode.Enforce);

        guard.Check(Unknown);

        Assert.False(guard.IsActive);
        Assert.Empty(_sink.Items);
    }

    [Fact]
    public void Learn_mode_records_the_accesses_under_the_name_of_the_scope()
    {
        using (Scopes(GuardMode.Learn).Begin(Job))
        {
            Guard(GuardMode.Learn).Check(Known);
        }

        Assert.Equal([Known.ToString()], _profile.ToDictionary()[Job]);
    }

    [Fact]
    public void Learn_mode_records_a_scope_that_touches_nothing()
    {
        Scopes(GuardMode.Learn).Begin(Job).Dispose();

        Assert.Empty(_profile.ToDictionary()[Job]);
    }

    [Fact]
    public void Enforce_mode_allows_a_known_access_and_throws_for_an_unknown_one()
    {
        _profile.Allow(Job, Known);
        var guard = Guard(GuardMode.Enforce);

        using var scope = Scopes(GuardMode.Enforce).Begin(Job);
        guard.Check(Known);
        var exception = Assert.Throws<GuardViolationException>(() => guard.Check(Unknown));

        Assert.Equal(new GuardViolation(Job, Unknown, Blocked: true), exception.Violation);
        Assert.Equal([exception.Violation], _sink.Items);
    }

    [Fact]
    public void A_scope_without_a_profile_can_touch_nothing_in_enforce_mode()
    {
        using var scope = Scopes(GuardMode.Enforce).Begin(Job);

        Assert.Throws<GuardViolationException>(() => Guard(GuardMode.Enforce).Check(Known));
    }

    [Fact]
    public void The_guard_is_inactive_again_when_the_scope_ends()
    {
        var guard = Guard(GuardMode.Enforce);

        var scope = Scopes(GuardMode.Enforce).Begin(Job);
        Assert.True(guard.IsActive);

        scope.Dispose();
        scope.Dispose();

        Assert.False(guard.IsActive);
        Assert.Null(_accessor.Current);
    }

    [Fact]
    public void An_inner_scope_gives_the_context_back_to_the_outer_one()
    {
        var scopes = Scopes(GuardMode.Enforce);

        using var outer = scopes.Begin(Job);
        var outerContext = _accessor.Current;

        using (scopes.Begin("job other"))
        {
            Assert.Equal("job other", _accessor.Current?.Endpoint);
        }

        Assert.Same(outerContext, _accessor.Current);
    }

    [Fact]
    public async Task The_scope_stays_open_across_an_await()
    {
        using var scope = Scopes(GuardMode.Enforce).Begin(Job);

        await Task.Yield();
        await Task.Run(() => Assert.Equal(Job, _accessor.Current?.Endpoint));

        Assert.Equal(Job, _accessor.Current?.Endpoint);
    }

    [Fact]
    public void A_scope_can_have_its_own_mode()
    {
        var guard = Guard(GuardMode.Enforce);

        using (Scopes(GuardMode.Enforce).Begin(Job, GuardMode.Audit))
        {
            guard.Check(Unknown);
        }

        using (Scopes(GuardMode.Enforce).Begin(Job, GuardMode.Off))
        {
            Assert.False(guard.IsActive);
        }

        Assert.Equal([new GuardViolation(Job, Unknown, Blocked: false)], _sink.Items);
    }

    [Fact]
    public void The_global_modes_off_and_learn_beat_the_mode_of_a_scope()
    {
        using (Scopes(GuardMode.Off).Begin(Job, GuardMode.Enforce))
        {
            Assert.False(Guard(GuardMode.Off).IsActive);
        }

        using (Scopes(GuardMode.Learn).Begin(Job, GuardMode.Enforce))
        {
            Guard(GuardMode.Learn).Check(Unknown);
        }

        Assert.Equal([Unknown.ToString()], _profile.ToDictionary()[Job]);
        Assert.Empty(_sink.Items);
    }

    [Fact]
    public void A_change_of_the_global_mode_reaches_a_scope_that_is_already_open()
    {
        // A job can keep one scope open for hours. The kill switch must not wait for its end.
        var settings = new LiveSettings(GuardMode.Enforce);
        var guard = Guard(settings);

        using var scope = new GuardScopes(_accessor, _profile, settings).Begin(Job);
        Assert.Throws<GuardViolationException>(() => guard.Check(Unknown));

        settings.ChangeMode(GuardMode.Audit);
        guard.Check(Unknown);

        settings.ChangeMode(GuardMode.Off);
        guard.Check(Unknown);

        Assert.False(guard.IsActive);
        Assert.Equal(
            [new GuardViolation(Job, Unknown, Blocked: true), new GuardViolation(Job, Unknown, Blocked: false)],
            _sink.Items);
    }

    [Fact]
    public void A_scope_keeps_its_own_mode_when_the_global_mode_changes_to_audit()
    {
        var settings = new LiveSettings(GuardMode.Enforce);
        var guard = Guard(settings);

        using var scope = new GuardScopes(_accessor, _profile, settings).Begin(Job, GuardMode.Enforce);
        settings.ChangeMode(GuardMode.Audit);

        Assert.Throws<GuardViolationException>(() => guard.Check(Unknown));
    }

    [Fact]
    public void Learn_is_not_legal_as_the_mode_of_a_scope()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Scopes(GuardMode.Enforce).Begin(Job, GuardMode.Learn));
        Assert.Null(_accessor.Current);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void A_scope_needs_a_name(string name)
    {
        Assert.Throws<ArgumentException>(() => Scopes(GuardMode.Enforce).Begin(name));
    }

    [Fact]
    public async Task A_job_of_the_sample_api_is_recorded_and_then_enforced()
    {
        var profilePath = Path.Combine(_directory, "behavior.lock.json");

        // The recording: a test runs the job once in Learn mode.
        await using (var recording = new GuardedApi(GuardMode.Learn, profilePath))
        {
            await RunJob(recording, db => db.Orders.ToListAsync());
        }

        await using var api = new GuardedApi(GuardMode.Enforce, profilePath);

        await RunJob(api, db => db.Orders.ToListAsync());
        await Assert.ThrowsAsync<GuardViolationException>(() => RunJob(api, db => db.Users.ToListAsync()));

        var violation = Assert.Single(api.Violations.Items);
        Assert.Equal(Job, violation.Endpoint);
        Assert.StartsWith("sql: SELECT", violation.Access.ToString());
        Assert.Contains("\"Users\"", violation.Access.Key);
    }

    private static async Task RunJob<T>(GuardedApi api, Func<SampleApi.ShopDb, Task<T>> work)
    {
        using var services = api.Services.CreateScope();
        using var scope = api.Services.GetRequiredService<GuardScopes>().Begin(Job);

        await work(services.ServiceProvider.GetRequiredService<SampleApi.ShopDb>());
    }

    private GuardScopes Scopes(GuardMode mode) => new(_accessor, _profile, new LiveSettings(mode));

    private AccessGuard Guard(GuardMode mode) => Guard(new LiveSettings(mode));

    private AccessGuard Guard(LiveSettings settings) => new(_accessor, _profile, [_sink], settings);
}
