using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace DynamicEndpoints.EntityFrameworkCore.Migrations;

/// <summary>
/// Creates the table of <see cref="DynamicEndpointsDbContext"/>. Written by hand without store types, so the provider in use
/// (SQL Server, PostgreSQL, SQLite, MySQL, …) picks its own column types.
/// </summary>
[DbContext(typeof(DynamicEndpointsDbContext))]
[Migration(Id)]
public sealed class InitialDynamicEndpoints : Migration
{
    public const string Id = "20261002000000_InitialDynamicEndpoints";

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: DynamicEndpointRecordConfiguration.DefaultTable,
            columns: table => new
            {
                Id = table.Column<Guid>(nullable: false),
                Method = table.Column<string>(maxLength: 16, nullable: false),
                Route = table.Column<string>(maxLength: 1024, nullable: false),
                Name = table.Column<string>(maxLength: 256, nullable: true),
                Enabled = table.Column<bool>(nullable: false),
                Version = table.Column<int>(nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(nullable: false),
                Definition = table.Column<string>(nullable: false),
            },
            constraints: table => table.PrimaryKey("PK_DynamicEndpoints", x => x.Id));
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropTable(name: DynamicEndpointRecordConfiguration.DefaultTable);
}
