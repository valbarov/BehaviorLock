using Microsoft.EntityFrameworkCore;

namespace SampleApi;

public static class AdminEndpoints
{
    public static void MapAdminEndpoints(this IEndpointRouteBuilder app)
    {
        // Weakness: broken function level authorization. The endpoint asks for a signed-in caller,
        // but it does not ask for the admin role. Any user can read the orders of all users.
        app.MapGet("/admin/orders", async (ShopDb db) => await db.Orders.ToListAsync()).RequireAuthorization();
    }
}
