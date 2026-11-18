using System.Net;
using System.Net.Http.Json;

namespace BehaviorLock.Tests;

[Collection(ApiCollection.Name)]
public sealed class LegalRequestTests(ProfileFixture fixture)
{
    public static IEnumerable<object[]> Names => LegalRequests.All.Keys.Select(name => new object[] { name });

    [Theory]
    [MemberData(nameof(Names))]
    public async Task Legal_request_passes_in_enforce_mode(string name)
    {
        await using var api = fixture.Create(GuardMode.Enforce);

        using var response = await LegalRequests.All[name](api.CreateClient());

        Assert.True(response.IsSuccessStatusCode);
        Assert.Empty(api.Violations.Items);
    }

    [Fact]
    public async Task A_wrong_password_is_rejected_by_the_app_and_not_by_the_guard()
    {
        await using var api = fixture.Create(GuardMode.Enforce);

        using var response = await api.CreateClient()
            .PostAsJsonAsync("/login", new { email = "alice@example.com", password = "wrong" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(api.Violations.Items);
    }

    [Fact]
    public async Task Every_endpoint_has_a_profile()
    {
        await using var api = fixture.Create(GuardMode.Enforce);

        Assert.Empty(EndpointCoverage.FindUnprofiled(api.Services));
    }
}
