using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NzbWebDAV.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddOperationTypeBytesTransferredToProviderUsageEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "OperationType",
                table: "ProviderUsageEvents",
                type: "TEXT",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "BytesTransferred",
                table: "ProviderUsageEvents",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProviderUsageEvents_OperationType",
                table: "ProviderUsageEvents",
                column: "OperationType");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ProviderUsageEvents_OperationType",
                table: "ProviderUsageEvents");

            migrationBuilder.DropColumn(
                name: "OperationType",
                table: "ProviderUsageEvents");

            migrationBuilder.DropColumn(
                name: "BytesTransferred",
                table: "ProviderUsageEvents");
        }
    }
}
