using System.Text.Json;

namespace BehaviorLock.Tests;

/// <summary>
/// When the app starts in Audit or Enforce mode, a warning names the endpoints that the profile does not
/// have. So the gap is seen before the first 403. See <see cref="ModeTestApi"/> and <see cref="TinyApi"/>.
/// </summary>
public sealed class UnprofiledEndpointLogTests
{
    // The profile of ModeTestApi has none of its endpoints. The two endpoints with the mode Off are left out.
    private const string ModeTestApiEndpoints =
        "GET /attribute, GET /audit, GET /default, GET /enforce, GET /group/audit, GET /group/plain, GET /twice, GET mvc/controller";

    private static readonly string[] TinyApiEndpoints =
    [
        "GET /whoami", "GET /text", "GET /json", "GET /chunks", "GET /unflushed",
        "GET /fetch", "GET /fetch-quiet", "GET /fetch-factory",
    ];

    [Theory]
    [InlineData(GuardMode.Audit)]
    [InlineData(GuardMode.Enforce)]
    public async Task The_start_names_the_endpoints_without_a_profile(GuardMode mode)
    {
        await using var api = await ModeTestApi.StartAsync(mode);

        var warning = Assert.Single(CoverageWarnings(api.Logs));

        Assert.Contains("behavior.lock.json does not have 8 of the endpoints of the app", warning);
        Assert.Contains($": {ModeTestApiEndpoints}. Enforce mode blocks", warning);
    }

    [Fact]
    public async Task An_endpoint_with_the_mode_off_is_not_named()
    {
        await using var api = await ModeTestApi.StartAsync(GuardMode.Enforce);

        var warning = Assert.Single(CoverageWarnings(api.Logs));

        Assert.DoesNotContain("GET /off", warning);
        Assert.DoesNotContain("mvc/action", warning);
    }

    [Fact]
    public async Task Only_the_missing_endpoints_are_named()
    {
        var profile = Profile(TinyApiEndpoints.Except(["GET /json", "GET /chunks"]));
        await using var api = await TinyApi.StartAsync(GuardMode.Enforce, profile);

        var warning = Assert.Single(CoverageWarnings(api.Logs));

        Assert.Contains("does not have 2 of the endpoints of the app: GET /chunks, GET /json. Enforce mode", warning);
    }

    [Fact]
    public async Task A_profile_with_every_endpoint_gives_no_warning()
    {
        await using var api = await TinyApi.StartAsync(GuardMode.Enforce, Profile(TinyApiEndpoints));

        Assert.Empty(api.Logs.Warnings);
    }

    [Theory]
    [InlineData(GuardMode.Off)]
    [InlineData(GuardMode.Learn)]
    public async Task Off_and_learn_mode_name_no_endpoint(GuardMode mode)
    {
        await using var api = await ModeTestApi.StartAsync(mode);

        Assert.Empty(CoverageWarnings(api.Logs));
    }

    [Fact]
    public async Task An_empty_profile_has_its_own_warning_and_no_list()
    {
        await using var api = await TinyApi.StartAsync(GuardMode.Audit, "{}");

        Assert.Contains("is missing or empty", Assert.Single(api.Logs.Warnings));
        Assert.Empty(CoverageWarnings(api.Logs));
    }

    [Fact]
    public async Task A_change_from_off_names_the_endpoints_once()
    {
        await using var api = await ModeTestApi.StartAsync(GuardMode.Off);
        Assert.Empty(CoverageWarnings(api.Logs));

        api.ChangeMode(GuardMode.Enforce);
        api.ChangeMode(GuardMode.Audit);
        api.ChangeMode(GuardMode.Enforce);

        Assert.Contains(ModeTestApiEndpoints, Assert.Single(CoverageWarnings(api.Logs)));
    }

    [Fact]
    public void A_long_list_ends_with_the_number_of_endpoints_that_are_left_out()
    {
        string[] endpoints = ["GET /a", "GET /b", "GET /c", "GET /d", "GET /e"];

        Assert.Equal("GET /a, GET /b, GET /c, GET /d, GET /e", EndpointCoverage.Describe(endpoints, limit: 5));
        Assert.Equal("GET /a, GET /b, GET /c and 2 more", EndpointCoverage.Describe(endpoints, limit: 3));
    }

    private static IEnumerable<string> CoverageWarnings(CollectingLogs logs) =>
        logs.Warnings.Where(warning => warning.Contains("of the endpoints of the app"));

    /// <summary>A profile with these endpoints. None of them has an access.</summary>
    private static string Profile(IEnumerable<string> endpoints) =>
        JsonSerializer.Serialize(endpoints.ToDictionary(endpoint => endpoint, _ => Array.Empty<string>()));
}
