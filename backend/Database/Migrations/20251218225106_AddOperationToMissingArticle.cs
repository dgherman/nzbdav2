using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NzbWebDAV.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddOperationToMissingArticle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "OperationCountsJson",
                table: "MissingArticleSummaries",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Operation",
                table: "MissingArticleEvents",
                type: "TEXT",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "OperationCountsJson",
                table: "MissingArticleSummaries");

            migrationBuilder.DropColumn(
                name: "Operation",
                table: "MissingArticleEvents");
        }
    }
}
