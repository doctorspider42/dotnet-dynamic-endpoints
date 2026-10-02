using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace DynamicEndpoints.EntityFrameworkCore.Migrations;

/// <summary>
/// Adds the history and the drafts of <see cref="DynamicEndpointsDbContext"/>. Provider-independent like
/// <see cref="InitialDynamicEndpoints"/>. Existing definitions get their first history entry the next time they change – until
/// then the history shows the published revision.
/// </summary>
[DbContext(typeof(DynamicEndpointsDbContext))]
[Migration(Id)]
public sealed class DynamicEndpointRevisionsAndDrafts : Migration
{
    public const string Id = "20261003000000_DynamicEndpointRevisionsAndDrafts";

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: DynamicEndpointRevisionRecordConfiguration.DefaultTable,
            columns: table => new
            {
                EndpointId = table.Column<Guid>(nullable: false),
                Revision = table.Column<int>(nullable: false),
                Kind = table.Column<string>(maxLength: 32, nullable: false),
                Comment = table.Column<string>(maxLength: 1024, nullable: true),
                SourceRevision = table.Column<int>(nullable: true),
                SavedAt = table.Column<DateTimeOffset>(nullable: false),
                Definition = table.Column<string>(nullable: false),
            },
            constraints: table => table.PrimaryKey("PK_DynamicEndpointRevisions", x => new { x.EndpointId, x.Revision }));

        migrationBuilder.CreateTable(
            name: DynamicEndpointDraftRecordConfiguration.DefaultTable,
            columns: table => new
            {
                EndpointId = table.Column<Guid>(nullable: false),
                BaseRevision = table.Column<int>(nullable: false),
                Method = table.Column<string>(maxLength: 16, nullable: false),
                Route = table.Column<string>(maxLength: 1024, nullable: false),
                Name = table.Column<string>(maxLength: 256, nullable: true),
                PublishAt = table.Column<DateTimeOffset>(nullable: true),
                Comment = table.Column<string>(maxLength: 1024, nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(nullable: false),
                Definition = table.Column<string>(nullable: false),
            },
            constraints: table => table.PrimaryKey("PK_DynamicEndpointDrafts", x => x.EndpointId));
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: DynamicEndpointDraftRecordConfiguration.DefaultTable);
        migrationBuilder.DropTable(name: DynamicEndpointRevisionRecordConfiguration.DefaultTable);
    }
}
