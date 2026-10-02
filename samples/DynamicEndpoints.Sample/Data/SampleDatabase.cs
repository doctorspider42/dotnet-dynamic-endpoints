using DynamicEndpoints.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace DynamicEndpoints.Sample.Data;

/// <summary>Demo only – a real application uses migrations (<c>dotnet ef migrations add …</c>).</summary>
internal static class SampleDatabase
{
    public static async Task EnsureCreatedAsync(IServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<AppDbContext>>();

        // Several replicas (e.g. under Aspire) may race to create the schema – the loser simply tries again.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await db.Database.EnsureCreatedAsync();
                break;
            }
            catch (Exception ex) when (attempt < 5)
            {
                logger.LogDebug(ex, "Creating the sample database failed, retrying.");
                await Task.Delay(TimeSpan.FromSeconds(attempt));
            }
        }

        // EnsureCreated doesn't add tables to an existing database. A local SQLite demo database from before history, drafts
        // and products existed is simply recreated (and seeded again).
        if (db.Database.IsSqlite())
        {
            try
            {
                await db.Set<DynamicEndpointDraftRecord>().AnyAsync();
                await db.Products.AnyAsync();
            }
            catch (Exception)
            {
                logger.LogWarning("The sample database is outdated – recreating it.");
                await db.Database.EnsureDeletedAsync();
                await db.Database.EnsureCreatedAsync();
            }
        }

        try
        {
            await SeedProductsAsync(db);
        }
        catch (Exception ex)
        {
            // E.g. a PostgreSQL volume created before the products table existed – recreate it to get the ef-crud demo.
            logger.LogWarning(ex, "The demo products could not be seeded.");
        }
    }

    // A few rows per tenant for the scaffolded ef-crud endpoints (/shop/products, X-Tenant: acme or globex).
    private static async Task SeedProductsAsync(AppDbContext db)
    {
        if (await db.Products.AnyAsync())
        {
            return;
        }

        (string Tenant, string Sku, string Name, decimal Price, int Stock)[] products =
        [
            ("acme", "ANV-1", "Anvil", 49.90m, 12),
            ("acme", "ROC-2", "Rocket skates", 129m, 3),
            ("acme", "MAG-3", "Giant magnet", 19.99m, 40),
            ("globex", "HAM-1", "Hammock", 59m, 8),
            ("globex", "LMP-2", "Lava lamp", 24.5m, 15),
        ];
        db.Products.AddRange(products.Select(p => new Product
        {
            TenantId = p.Tenant,
            Sku = p.Sku,
            Name = p.Name,
            Price = p.Price,
            PurchasePrice = Math.Round(p.Price * 0.6m, 2),
            Stock = p.Stock,
            CreatedAt = DateTimeOffset.UtcNow,
            Version = Guid.NewGuid(),
        }));
        await db.SaveChangesAsync();
    }
}
