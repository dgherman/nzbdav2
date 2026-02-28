using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NzbWebDAV.Database.Migrations
{
    /// <inheritdoc />
    public partial class OptimizeProviderStatsIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ProviderUsageEvents_CreatedAt",
                table: "ProviderUsageEvents");

            migrationBuilder.DropIndex(
                name: "IX_ProviderUsageEvents_OperationType",
                table: "ProviderUsageEvents");

            migrationBuilder.CreateIndex(
                name: "IX_ProviderUsageEvents_CreatedAt_ProviderHost_ProviderType_OperationType",
                table: "ProviderUsageEvents",
                columns: new[] { "CreatedAt", "ProviderHost", "ProviderType", "OperationType" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ProviderUsageEvents_CreatedAt_ProviderHost_ProviderType_OperationType",
                table: "ProviderUsageEvents");

            migrationBuilder.CreateIndex(
                name: "IX_ProviderUsageEvents_CreatedAt",
                table: "ProviderUsageEvents",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_ProviderUsageEvents_OperationType",
                table: "ProviderUsageEvents",
                column: "OperationType");
        }
    }
}
