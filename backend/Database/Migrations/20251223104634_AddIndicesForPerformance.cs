using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NzbWebDAV.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddIndicesForPerformance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_MissingArticleEvents_Filename",
                table: "MissingArticleEvents",
                column: "Filename");

            migrationBuilder.CreateIndex(
                name: "IX_MissingArticleEvents_SegmentId",
                table: "MissingArticleEvents",
                column: "SegmentId");

            migrationBuilder.CreateIndex(
                name: "IX_DavItems_Path",
                table: "DavItems",
                column: "Path");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MissingArticleEvents_Filename",
                table: "MissingArticleEvents");

            migrationBuilder.DropIndex(
                name: "IX_MissingArticleEvents_SegmentId",
                table: "MissingArticleEvents");

            migrationBuilder.DropIndex(
                name: "IX_DavItems_Path",
                table: "DavItems");
        }
    }
}
