using System.Net;
using Microsoft.EntityFrameworkCore;

namespace BehaviorLock.Tests;

/// <summary>
/// Shows the harm of each attack when the guard is off, and that the same request does no harm in Enforce mode.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class AttackImpactTests(ProfileFixture fixture)
{
    [Fact]
    public async Task Union_injection_leaks_user_emails_only_without_the_guard()
    {
        Assert.Contains("alice@example.com", await Body(GuardMode.Off, Attacks.UnionRead));
        Assert.DoesNotContain("alice@example.com", await Body(GuardMode.Enforce, Attacks.UnionRead));
    }

    [Fact]
    public async Task Stacked_statement_deletes_the_users_only_without_the_guard()
    {
        Task<int> UserCount(GuardedApi api) => api.QueryDb(db => db.Users.CountAsync());

        Assert.Equal(0, await Run(GuardMode.Off, Attacks.StackedDelete, (api, _) => UserCount(api)));
        Assert.Equal(2, await Run(GuardMode.Enforce, Attacks.StackedDelete, (api, _) => UserCount(api)));
    }

    [Fact]
    public async Task Login_without_the_password_works_only_without_the_guard()
    {
        Assert.Equal(HttpStatusCode.OK, await Status(GuardMode.Off, Attacks.LoginBypass));
        Assert.Equal(HttpStatusCode.Forbidden, await Status(GuardMode.Enforce, Attacks.LoginBypass));
    }

    [Fact]
    public async Task Blind_injection_leaks_one_bit_per_request_only_without_the_guard()
    {
        await using var open = fixture.Create(GuardMode.Off);
        await using var guarded = fixture.Create(GuardMode.Enforce);

        // The first product in the answer tells whether a user email starts with the prefix.
        var whenTrue = await open.CreateClient().GetStringAsync(Attacks.BlindOrderByUrl("alice"));
        var whenFalse = await open.CreateClient().GetStringAsync(Attacks.BlindOrderByUrl("zzz"));
        using var blocked = await guarded.CreateClient().GetAsync(Attacks.BlindOrderByUrl("alice"));

        Assert.StartsWith("[{\"id\":1,\"name\":\"Laptop\"", whenTrue);
        Assert.StartsWith("[{\"id\":2,\"name\":\"Phone\"", whenFalse);
        Assert.Equal(HttpStatusCode.Forbidden, blocked.StatusCode);
    }

    [Fact]
    public async Task Mass_assignment_makes_an_admin_only_without_the_guard()
    {
        Task<bool> IsAdmin(GuardedApi api) => api.QueryDb(db => db.Users.Where(user => user.Id == 1).Select(user => user.IsAdmin).SingleAsync());

        Assert.True(await Run(GuardMode.Off, Attacks.MassAssignment, (api, _) => IsAdmin(api)));
        Assert.False(await Run(GuardMode.Enforce, Attacks.MassAssignment, (api, _) => IsAdmin(api)));
    }

    [Fact]
    public async Task Delete_bug_removes_every_order_only_without_the_guard()
    {
        Task<int> OrderCount(GuardedApi api) => api.QueryDb(db => db.Orders.CountAsync());

        Assert.Equal(0, await Run(GuardMode.Off, Attacks.DeleteAllBug, (api, _) => OrderCount(api)));
        Assert.Equal(2, await Run(GuardMode.Enforce, Attacks.DeleteAllBug, (api, _) => OrderCount(api)));
    }

    [Fact]
    public async Task Debug_switch_leaks_passwords_only_without_the_guard()
    {
        Assert.Contains("correct-horse", await Body(GuardMode.Off, Attacks.DebugSwitch));
        Assert.DoesNotContain("correct-horse", await Body(GuardMode.Enforce, Attacks.DebugSwitch));
    }

    [Theory]
    [InlineData(Attacks.SsrfMetadata, Attacks.MetadataUrl)]
    [InlineData(Attacks.SsrfOtherPort, "https://hooks.example.com:8443/ping")]
    [InlineData(Attacks.Exfiltration, Attacks.ExfiltrationUrl)]
    public async Task Unknown_host_is_called_only_without_the_guard(string attack, string url)
    {
        Task<bool> WasCalled(GuardedApi api) => Task.FromResult(api.Outbound.Urls.Contains(url));

        Assert.True(await Run(GuardMode.Off, attack, (api, _) => WasCalled(api)));
        Assert.False(await Run(GuardMode.Enforce, attack, (api, _) => WasCalled(api)));
    }

    [Fact]
    public async Task Wildcard_returns_every_product_only_without_the_guard()
    {
        Assert.Contains("Cable 10", await Body(GuardMode.Off, Attacks.WildcardDump));
        Assert.Empty(await Body(GuardMode.Enforce, Attacks.WildcardDump));
    }

    [Fact]
    public async Task A_normal_user_reads_the_orders_of_all_users_only_without_the_guard()
    {
        Assert.Contains("\"userId\":2", await Body(GuardMode.Off, Attacks.UserAsAdmin));
        Assert.Empty(await Body(GuardMode.Enforce, Attacks.UserAsAdmin));
    }

    private Task<string> Body(GuardMode mode, string attack) =>
        Run(mode, attack, (_, response) => response.Content.ReadAsStringAsync());

    private Task<HttpStatusCode> Status(GuardMode mode, string attack) =>
        Run(mode, attack, (_, response) => Task.FromResult(response.StatusCode));

    /// <summary>Sends one attack to a fresh API in the given mode and returns what the observer sees.</summary>
    private async Task<T> Run<T>(GuardMode mode, string attack, Func<GuardedApi, HttpResponseMessage, Task<T>> observe)
    {
        await using var api = fixture.Create(mode);
        using var response = await Attacks.All[attack].Send(api.CreateClient());
        return await observe(api, response);
    }
}
