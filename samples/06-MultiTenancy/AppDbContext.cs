using DynamicEndpoints.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace DynamicEndpoints.Samples.MultiTenancy;

/// <summary>The definitions (each may belong to a tenant), the tenants' products and shared reference data.</summary>
public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Product> Products => Set<Product>();

    public DbSet<Country> Countries => Set<Country>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // The tenant is part of the definition (stored as JSON), so the tables are the same as without tenancy.
        modelBuilder.ApplyDynamicEndpointsConfiguration();

        modelBuilder.Entity<Product>(entity =>
        {
            entity.HasIndex(p => new { p.TenantId, p.Sku }).IsUnique();
            entity.Property(p => p.Sku).HasMaxLength(20);
            entity.Property(p => p.Name).HasMaxLength(100);
            entity.Property(p => p.TenantId).HasMaxLength(64);
        });

        modelBuilder.Entity<Country>().HasKey(c => c.Code);
    }

    /// <summary>A few rows per tenant, and the countries everybody shares. Demo only.</summary>
    public async Task SeedDataAsync()
    {
        if (await Products.AnyAsync())
        {
            return;
        }

        Products.AddRange(
            new Product { TenantId = "acme", Sku = "ANV-1", Name = "Anvil", Price = 49.90, Stock = 12 },
            new Product { TenantId = "acme", Sku = "ROC-2", Name = "Rocket skates", Price = 129, Stock = 3 },
            new Product { TenantId = "acme", Sku = "MAG-3", Name = "Giant magnet", Price = 19.99, Stock = 40 },
            new Product { TenantId = "globex", Sku = "HAM-1", Name = "Hammock", Price = 59, Stock = 8 },
            new Product { TenantId = "globex", Sku = "LMP-2", Name = "Lava lamp", Price = 24.5, Stock = 15 });
        Countries.AddRange(
            new Country { Code = "PL", Name = "Poland" },
            new Country { Code = "DE", Name = "Germany" },
            new Country { Code = "US", Name = "United States" });
        await SaveChangesAsync();
    }
}

/// <summary>A product of one tenant's shop – <see cref="TenantId"/> is the tenant column of the ef-crud endpoints.</summary>
public sealed class Product
{
    public int Id { get; set; }

    public string Sku { get; set; } = "";

    public string Name { get; set; } = "";

    public double Price { get; set; }

    public int Stock { get; set; }

    /// <summary>Every query is filtered by it, new rows get the request's tenant, requests can never write it.</summary>
    public string TenantId { get; set; } = "";
}

/// <summary>Reference data, the same for every tenant (<c>SharedAcrossTenants()</c>).</summary>
public sealed class Country
{
    public string Code { get; set; } = "";

    public string Name { get; set; } = "";
}
