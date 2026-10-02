using DynamicEndpoints.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace DynamicEndpoints.Samples.BuiltInProcessors;

/// <summary>The definitions, and a products table the sql-query endpoints read.</summary>
public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Product> Products => Set<Product>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyDynamicEndpointsConfiguration();
        modelBuilder.Entity<Product>().HasIndex(p => p.Sku).IsUnique();
    }

    /// <summary>A few rows for the reports. Demo only – a real application has its data already.</summary>
    public async Task SeedProductsAsync()
    {
        if (await Products.AnyAsync())
        {
            return;
        }

        Products.AddRange(
            new Product { Sku = "ANV-1", Name = "Anvil", Price = 49.90, Stock = 12 },
            new Product { Sku = "ROC-2", Name = "Rocket skates", Price = 129, Stock = 3 },
            new Product { Sku = "MAG-3", Name = "Giant magnet", Price = 19.99, Stock = 40 },
            new Product { Sku = "TNT-4", Name = "Dynamite", Price = 9.5, Stock = 0 });
        await SaveChangesAsync();
    }
}

public sealed class Product
{
    public int Id { get; set; }

    public string Sku { get; set; } = "";

    public string Name { get; set; } = "";

    public double Price { get; set; }

    public int Stock { get; set; }
}
