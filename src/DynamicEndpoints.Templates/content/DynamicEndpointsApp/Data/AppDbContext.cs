using DynamicEndpoints.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace DynamicEndpointsApp.Data;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // The dynamic endpoint definitions live in the application's own database (the DynamicEndpoints table).
        modelBuilder.ApplyDynamicEndpointsConfiguration();

        // Your own entities go here.
    }
}
