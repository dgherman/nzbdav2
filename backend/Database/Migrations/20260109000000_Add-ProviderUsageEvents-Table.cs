using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NzbWebDAV.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddProviderUsageEventsTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ProviderUsageEvents",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    ProviderHost = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    ProviderType = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProviderUsageEvents", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ProviderUsageEvents_CreatedAt",
                table: "ProviderUsageEvents",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_ProviderUsageEvents_ProviderHost",
                table: "ProviderUsageEvents",
                column: "ProviderHost");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ProviderUsageEvents");
        }
    }
}
