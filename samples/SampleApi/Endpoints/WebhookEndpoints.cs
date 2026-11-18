namespace SampleApi;

public static class WebhookEndpoints
{
    public static void MapWebhookEndpoints(this IEndpointRouteBuilder app)
    {
        // Weakness: SSRF. The server calls any URL that the user gives.
        app.MapGet("/preview", async (string url, IHttpClientFactory clients) =>
        {
            using var response = await clients.CreateClient().GetAsync(url);
            return Results.Ok(new { status = (int)response.StatusCode });
        });
    }
}
