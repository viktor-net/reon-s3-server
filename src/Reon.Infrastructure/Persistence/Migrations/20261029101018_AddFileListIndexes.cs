using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Reon.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddFileListIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_files_content_type",
                table: "files",
                column: "content_type");

            migrationBuilder.CreateIndex(
                name: "ix_files_created_at",
                table: "files",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "ix_files_original_name",
                table: "files",
                column: "original_name");

            migrationBuilder.CreateIndex(
                name: "ix_files_status_created_at",
                table: "files",
                columns: new[] { "status", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_files_content_type",
                table: "files");

            migrationBuilder.DropIndex(
                name: "ix_files_created_at",
                table: "files");

            migrationBuilder.DropIndex(
                name: "ix_files_original_name",
                table: "files");

            migrationBuilder.DropIndex(
                name: "ix_files_status_created_at",
                table: "files");
        }
    }
}
