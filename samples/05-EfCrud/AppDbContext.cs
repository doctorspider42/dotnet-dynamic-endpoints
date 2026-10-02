using DynamicEndpoints.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace DynamicEndpoints.Samples.EfCrud;

/// <summary>The definitions, and the application's own entities that admins may build CRUD endpoints on (see Program.cs).</summary>
public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    /// <summary>Exposed as the ef-crud entity "products" – the DbSet name.</summary>
    public DbSet<Product> Products => Set<Product>();

    /// <summary>Exposed as "categories".</summary>
    public DbSet<Category> Categories => Set<Category>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyDynamicEndpointsConfiguration();

        modelBuilder.Entity<Category>(entity =>
        {
            entity.Property(c => c.Name).HasMaxLength(50);
            entity.Property(c => c.InternalNote).HasMaxLength(500);
        });

        modelBuilder.Entity<Product>(entity =>
        {
            entity.HasIndex(p => p.Sku).IsUnique();
            entity.Property(p => p.Sku).HasMaxLength(20);         // → maxLength of the scaffolded parameters
            entity.Property(p => p.Name).HasMaxLength(100);
            entity.Property(p => p.Price).HasPrecision(10, 2);    // → the range of the price parameter
            entity.Property(p => p.PurchasePrice).HasPrecision(10, 2);
            entity.Property(p => p.Version).IsConcurrencyToken(); // → ETags and If-Match
            if (Database.IsSqlite())
            {
                // SQLite can't compare or sort decimals – filtering and sorting by price works on a REAL column.
                entity.Property(p => p.Price).HasConversion<double>();
            }
        });
    }

    /// <summary>A few rows to click around with. Demo only.</summary>
    public async Task SeedDataAsync()
    {
        if (await Categories.AnyAsync())
        {
            return;
        }

        var tools = new Category { Name = "Tools", InternalNote = "Margin 40 %" };
        var gadgets = new Category { Name = "Gadgets", InternalNote = "Discontinue in Q4" };
        Categories.AddRange(tools, gadgets);
        await SaveChangesAsync();

        Products.AddRange(
            new Product { Sku = "ANV-1", Name = "Anvil", Price = 49.90m, PurchasePrice = 30m, Stock = 12, CategoryId = tools.Id },
            new Product { Sku = "ROC-2", Name = "Rocket skates", Price = 129m, PurchasePrice = 80m, Stock = 3, CategoryId = gadgets.Id },
            new Product { Sku = "MAG-3", Name = "Giant magnet", Price = 19.99m, PurchasePrice = 12m, Stock = 40, CategoryId = gadgets.Id },
            new Product { Sku = "HAM-4", Name = "Hammer", Price = 15m, PurchasePrice = 7m, Stock = 25, CategoryId = tools.Id });
        foreach (var product in Products.Local)
        {
            product.CreatedAt = DateTimeOffset.UtcNow;
            product.Version = Guid.NewGuid();
        }

        await SaveChangesAsync();
    }
}

/// <summary>Exposed with an explicit list of fields (Program.cs): only those are read or written.</summary>
public sealed class Product
{
    public int Id { get; set; }

    public string Sku { get; set; } = "";

    public string Name { get; set; } = "";

    public decimal Price { get; set; }

    public int Stock { get; set; }

    public int? CategoryId { get; set; }

    /// <summary>Read-only for requests – set by <see cref="ProductRules"/>.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Not allowlisted: never returned, never written by a dynamic endpoint.</summary>
    public decimal? PurchasePrice { get; set; }

    /// <summary>The concurrency token – the ETag of the endpoints, compared with If-Match.</summary>
    public Guid Version { get; set; }
}

/// <summary>Exposed with <c>AllFields(except: …)</c>: every mapped scalar property but the internal note.</summary>
public sealed class Category
{
    public int Id { get; set; }

    public string Name { get; set; } = "";

    public bool Active { get; set; } = true;

    /// <summary>Kept away from the endpoints by the <c>except</c> list.</summary>
    public string? InternalNote { get; set; }
}
