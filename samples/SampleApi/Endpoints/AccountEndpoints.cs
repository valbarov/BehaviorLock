using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace SampleApi;

public sealed record LoginRequest(string Email, string Password);

public static class AccountEndpoints
{
    public static void MapAccountEndpoints(this IEndpointRouteBuilder app)
    {
#pragma warning disable EF1002 // The SQL injection below is deliberate.

        // Weakness: SQL injection in the login query. It allows a login without the password.
        app.MapPost("/login", async (LoginRequest login, ShopDb db) =>
        {
            var users = await db.Users
                .FromSqlRaw($"SELECT * FROM Users WHERE Email = '{login.Email}' AND Password = '{login.Password}'")
                .ToListAsync();

            return users.FirstOrDefault() is { } user
                ? Results.Ok(new { user.DisplayName })
                : Results.Unauthorized();
        });

#pragma warning restore EF1002

        // Weakness: mass assignment. The client can set any column, also IsAdmin.
        app.MapPatch("/users/{id:int}", async (int id, [FromBody] Dictionary<string, JsonElement> changes, ShopDb db) =>
        {
            var user = await db.Users.FindAsync(id);
            if (user is null)
            {
                return Results.NotFound();
            }

            foreach (var (name, value) in changes)
            {
                var property = db.Entry(user).Property(name);
                property.CurrentValue = value.Deserialize(property.Metadata.ClrType);
            }

            await db.SaveChangesAsync();
            return Results.NoContent();
        });
    }
}
