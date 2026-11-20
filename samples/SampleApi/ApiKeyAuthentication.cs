using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace SampleApi;

/// <summary>
/// Signs a caller in by the <c>X-Api-Key</c> header. The keys are fixed on purpose: the sample only needs
/// two kinds of callers, a normal user and an admin.
/// </summary>
public sealed class ApiKeyAuthentication(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "ApiKey";
    public const string Header = "X-Api-Key";
    public const string UserKey = "alice-key";
    public const string AdminKey = "admin-key";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        Claim[]? claims = Request.Headers[Header].ToString() switch
        {
            UserKey => [new(ClaimTypes.Name, "alice")],
            AdminKey => [new(ClaimTypes.Name, "root"), new(ClaimTypes.Role, "admin")],
            _ => null,
        };

        if (claims is null)
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var user = new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(user, SchemeName)));
    }
}

public static class ShopAuthentication
{
    /// <summary>Registers the API key scheme and authorization.</summary>
    public static IServiceCollection AddShopAuthentication(this IServiceCollection services)
    {
        services.AddAuthentication(ApiKeyAuthentication.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthentication>(ApiKeyAuthentication.SchemeName, null);
        services.AddAuthorization();
        return services;
    }
}
