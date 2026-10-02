using DynamicEndpoints.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace DynamicEndpoints.Samples.QuickStart;

/// <summary>The application's own context. The endpoint definitions, their history and drafts live in it, next to your tables.</summary>
public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // The DynamicEndpoints, DynamicEndpointRevisions and DynamicEndpointDrafts tables.
        modelBuilder.ApplyDynamicEndpointsConfiguration();

        // Your own entities go here.
    }
}
