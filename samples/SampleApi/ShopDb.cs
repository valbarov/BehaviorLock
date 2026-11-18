using Microsoft.EntityFrameworkCore;

namespace SampleApi;

public sealed class Product
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public int Price { get; set; }
}

public sealed class User
{
    public int Id { get; set; }
    public required string Email { get; set; }

    /// <summary>Plain text on purpose: the sample needs a secret that an attack can leak.</summary>
    public required string Password { get; set; }
    public required string DisplayName { get; set; }
    public bool IsAdmin { get; set; }
}

public sealed class Order
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public int Total { get; set; }
}

public sealed class ShopDb(DbContextOptions<ShopDb> options) : DbContext(options)
{
    public DbSet<Product> Products => Set<Product>();
    public DbSet<User> Users => Set<User>();
    public DbSet<Order> Orders => Set<Order>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Product>().HasData(
            new Product { Id = 1, Name = "Laptop", Price = 1200 },
            new Product { Id = 2, Name = "Phone", Price = 800 });

        modelBuilder.Entity<User>().HasData(
            new User { Id = 1, Email = "alice@example.com", Password = "correct-horse", DisplayName = "Alice" },
            new User { Id = 2, Email = "bob@example.com", Password = "battery-staple", DisplayName = "Bob" });

        modelBuilder.Entity<Order>().HasData(
            new Order { Id = 1, UserId = 1, Total = 50 },
            new Order { Id = 2, UserId = 2, Total = 75 });
    }
}
