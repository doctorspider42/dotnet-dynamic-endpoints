using DynamicEndpoints.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace DynamicEndpoints.Samples.Showcase.Data;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<DocumentRecord> Documents => Set<DocumentRecord>();

    /// <summary>Exposed to admins as the ef-crud entity "products" (the DbSet name).</summary>
    public DbSet<Product> Products => Set<Product>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // The dynamic endpoint definitions live in the application's own database.
        modelBuilder.ApplyDynamicEndpointsConfiguration();

        modelBuilder.Entity<DocumentRecord>(entity =>
        {
            entity.HasKey(d => d.Id);
            entity.HasIndex(d => d.Collection);
            entity.Property(d => d.Collection).HasMaxLength(64);
        });

        modelBuilder.Entity<Product>(entity =>
        {
            entity.HasIndex(p => new { p.TenantId, p.Sku }).IsUnique();
            entity.Property(p => p.Sku).HasMaxLength(20);
            entity.Property(p => p.Name).HasMaxLength(100);
            entity.Property(p => p.TenantId).HasMaxLength(64);
            entity.Property(p => p.Price).HasPrecision(10, 2);
            entity.Property(p => p.PurchasePrice).HasPrecision(10, 2);
            entity.Property(p => p.Version).IsConcurrencyToken();
            if (Database.IsSqlite())
            {
                // SQLite can't compare or sort decimals – filtering and sorting by price works on a REAL column.
                entity.Property(p => p.Price).HasConversion<double>();
            }
        });
    }
}

/// <summary>Schemaless document used by the "collection" processor.</summary>
public sealed class DocumentRecord
{
    public Guid Id { get; set; }

    public string Collection { get; set; } = "";

    public string Data { get; set; } = "{}";

    public DateTimeOffset CreatedAt { get; set; }
}
