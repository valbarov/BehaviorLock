using Microsoft.EntityFrameworkCore;

namespace SampleApi;

public static class ProductEndpoints
{
    public static void MapProductEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/products/{id:int}", async (int id, ShopDb db) =>
            await db.Products.FindAsync(id) is { } product ? Results.Ok(product) : Results.NotFound());

        app.MapPost("/products", async (Product product, ShopDb db) =>
        {
            db.Products.Add(product);
            await db.SaveChangesAsync();
            return Results.Created($"/products/{product.Id}", product);
        });

#pragma warning disable EF1002 // The SQL injection below is deliberate.

        // Weakness: SQL injection in a string value. User input is concatenated into the SQL text.
        app.MapGet("/products/search", async (string name, ShopDb db) =>
            await db.Products.FromSqlRaw($"SELECT * FROM Products WHERE Name LIKE '%{name}%'").ToListAsync());

        // Weakness: SQL injection in ORDER BY. No quote is needed to inject here.
        app.MapGet("/products", async (string sort, ShopDb db) =>
            await db.Products.FromSqlRaw($"SELECT * FROM Products ORDER BY {sort}").ToListAsync());

#pragma warning restore EF1002
    }
}
