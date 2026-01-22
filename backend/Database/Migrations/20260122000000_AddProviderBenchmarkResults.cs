using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NzbWebDAV.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddProviderBenchmarkResults : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ProviderBenchmarkResults",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    RunId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    TestFileName = table.Column<string>(type: "TEXT", nullable: false),
                    TestFileSize = table.Column<long>(type: "INTEGER", nullable: false),
                    TestSizeMb = table.Column<int>(type: "INTEGER", nullable: false),
                    ProviderIndex = table.Column<int>(type: "INTEGER", nullable: false),
                    ProviderHost = table.Column<string>(type: "TEXT", nullable: false),
                    ProviderType = table.Column<string>(type: "TEXT", nullable: false),
                    IsLoadBalanced = table.Column<bool>(type: "INTEGER", nullable: false),
                    BytesDownloaded = table.Column<long>(type: "INTEGER", nullable: false),
                    ElapsedSeconds = table.Column<double>(type: "REAL", nullable: false),
                    SpeedMbps = table.Column<double>(type: "REAL", nullable: false),
                    Success = table.Column<bool>(type: "INTEGER", nullable: false),
                    ErrorMessage = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProviderBenchmarkResults", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ProviderBenchmarkResults_CreatedAt",
                table: "ProviderBenchmarkResults",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_ProviderBenchmarkResults_RunId",
                table: "ProviderBenchmarkResults",
                column: "RunId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ProviderBenchmarkResults");
        }
    }
}
