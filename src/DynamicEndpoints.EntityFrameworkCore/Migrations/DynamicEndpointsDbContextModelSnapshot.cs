using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace DynamicEndpoints.EntityFrameworkCore.Migrations;

// Provider-independent like the migrations: no store types, so the provider in use maps the columns exactly as it maps the model.
[DbContext(typeof(DynamicEndpointsDbContext))]
internal sealed class DynamicEndpointsDbContextModelSnapshot : ModelSnapshot
{
    protected override void BuildModel(ModelBuilder modelBuilder)
    {
        modelBuilder.HasAnnotation("ProductVersion", "10.0.12");

        modelBuilder.Entity("DynamicEndpoints.EntityFrameworkCore.DynamicEndpointRecord", b =>
        {
            b.Property<Guid>("Id").ValueGeneratedOnAdd();
            b.Property<DateTimeOffset>("CreatedAt");
            b.Property<string>("Definition").IsRequired();
            b.Property<bool>("Enabled");
            b.Property<string>("Method").IsRequired().HasMaxLength(16);
            b.Property<string>("Name").HasMaxLength(256);
            b.Property<string>("Route").IsRequired().HasMaxLength(1024);
            b.Property<DateTimeOffset>("UpdatedAt");
            b.Property<int>("Version").IsConcurrencyToken();
            b.HasKey("Id");
            b.ToTable("DynamicEndpoints");
        });

        modelBuilder.Entity("DynamicEndpoints.EntityFrameworkCore.DynamicEndpointDraftRecord", b =>
        {
            b.Property<Guid>("EndpointId");
            b.Property<int>("BaseRevision");
            b.Property<string>("Comment").HasMaxLength(1024);
            b.Property<DateTimeOffset>("CreatedAt");
            b.Property<string>("Definition").IsRequired();
            b.Property<string>("Method").IsRequired().HasMaxLength(16);
            b.Property<string>("Name").HasMaxLength(256);
            b.Property<DateTimeOffset?>("PublishAt");
            b.Property<string>("Route").IsRequired().HasMaxLength(1024);
            b.Property<DateTimeOffset>("UpdatedAt");
            b.HasKey("EndpointId");
            b.ToTable("DynamicEndpointDrafts");
        });

        modelBuilder.Entity("DynamicEndpoints.EntityFrameworkCore.DynamicEndpointRevisionRecord", b =>
        {
            b.Property<Guid>("EndpointId");
            b.Property<int>("Revision");
            b.Property<string>("Comment").HasMaxLength(1024);
            b.Property<string>("Definition").IsRequired();
            b.Property<string>("Kind").IsRequired().HasMaxLength(32);
            b.Property<DateTimeOffset>("SavedAt");
            b.Property<int?>("SourceRevision");
            b.HasKey("EndpointId", "Revision");
            b.ToTable("DynamicEndpointRevisions");
        });
    }
}
