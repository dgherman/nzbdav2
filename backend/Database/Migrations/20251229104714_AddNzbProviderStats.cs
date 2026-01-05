using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NzbWebDAV.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddNzbProviderStats : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "NzbProviderStats",
                columns: table => new
                {
                    JobName = table.Column<string>(type: "TEXT", nullable: false),
                    ProviderIndex = table.Column<int>(type: "INTEGER", nullable: false),
                    SuccessfulSegments = table.Column<int>(type: "INTEGER", nullable: false),
                    FailedSegments = table.Column<int>(type: "INTEGER", nullable: false),
                    TotalBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    TotalTimeMs = table.Column<long>(type: "INTEGER", nullable: false),
                    LastUsed = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NzbProviderStats", x => new { x.JobName, x.ProviderIndex });
                });

            migrationBuilder.CreateIndex(
                name: "IX_NzbProviderStats_JobName",
                table: "NzbProviderStats",
                column: "JobName");

            migrationBuilder.CreateIndex(
                name: "IX_NzbProviderStats_JobName_LastUsed",
                table: "NzbProviderStats",
                columns: new[] { "JobName", "LastUsed" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "NzbProviderStats");
        }
    }
}
