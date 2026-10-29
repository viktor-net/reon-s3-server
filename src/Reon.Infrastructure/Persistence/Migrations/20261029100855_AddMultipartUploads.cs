using System;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Reon.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMultipartUploads : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "multipart_uploads",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    file_id = table.Column<Guid>(type: "uuid", nullable: false),
                    storage_upload_id = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    bucket = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    object_key = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    original_name = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    content_type = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    tags = table.Column<JsonDocument>(type: "jsonb", nullable: true),
                    owner = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    failure_reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_multipart_uploads", x => x.id);
                    table.CheckConstraint("ck_multipart_uploads_status", "\"status\" IN ('Initiated', 'InProgress', 'Completing', 'Completed', 'Aborted', 'Expired', 'Failed')");
                });

            migrationBuilder.CreateTable(
                name: "multipart_upload_parts",
                columns: table => new
                {
                    upload_id = table.Column<Guid>(type: "uuid", nullable: false),
                    part_number = table.Column<int>(type: "integer", nullable: false),
                    etag = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    size = table.Column<long>(type: "bigint", nullable: false),
                    uploaded_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_multipart_upload_parts", x => new { x.upload_id, x.part_number });
                    table.ForeignKey(
                        name: "FK_multipart_upload_parts_multipart_uploads_upload_id",
                        column: x => x.upload_id,
                        principalTable: "multipart_uploads",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_multipart_uploads_file_id",
                table: "multipart_uploads",
                column: "file_id");

            migrationBuilder.CreateIndex(
                name: "ix_multipart_uploads_status_expires_at",
                table: "multipart_uploads",
                columns: new[] { "status", "expires_at" });

            migrationBuilder.CreateIndex(
                name: "ux_multipart_uploads_storage_upload_id",
                table: "multipart_uploads",
                column: "storage_upload_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "multipart_upload_parts");

            migrationBuilder.DropTable(
                name: "multipart_uploads");
        }
    }
}
