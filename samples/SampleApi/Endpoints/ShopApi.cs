namespace SampleApi;

public static class ShopApi
{
    /// <summary>Maps all endpoints of the sample. Every group has at least one deliberate weakness; see the README.</summary>
    public static void MapShopApi(this IEndpointRouteBuilder app)
    {
        app.MapGet("/health", () => "ok");
        app.MapProductEndpoints();
        app.MapAccountEndpoints();
        app.MapOrderEndpoints();
        app.MapWebhookEndpoints();
    }
}
