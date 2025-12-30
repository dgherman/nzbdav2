using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NzbWebDAV.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddMissingDatabaseIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Add general (non-filtered) index on HealthCheckResults.DavItemId
            // The existing filtered index only covers queries with RepairStatus = 3
            // This index covers all other queries on DavItemId
            migrationBuilder.CreateIndex(
                name: "IX_HealthCheckResults_DavItemId_General",
                table: "HealthCheckResults",
                column: "DavItemId");

            // Add composite index on QueueItems for common query pattern
            // Covers: WHERE (PauseUntil IS NULL OR nowTime >= PauseUntil) ORDER BY Priority DESC, CreatedAt
            // Used in DavDatabaseClient.cs:85-88 for queue processing
            migrationBuilder.CreateIndex(
                name: "IX_QueueItems_PauseUntil_Priority_CreatedAt",
                table: "QueueItems",
                columns: new[] { "PauseUntil", "Priority", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Remove the indexes in reverse order
            migrationBuilder.DropIndex(
                name: "IX_QueueItems_PauseUntil_Priority_CreatedAt",
                table: "QueueItems");

            migrationBuilder.DropIndex(
                name: "IX_HealthCheckResults_DavItemId_General",
                table: "HealthCheckResults");
        }
    }
}
