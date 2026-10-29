using System;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Reon.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAdminStorage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "audit_events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    actor = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    action = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    resource = table.Column<string>(type: "character varying(1200)", maxLength: 1200, nullable: false),
                    result = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    status_code = table.Column<int>(type: "integer", nullable: true),
                    details = table.Column<JsonDocument>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_audit_events", x => x.id);
                    table.CheckConstraint("ck_audit_events_result", "\"result\" IN ('Success', 'Failure')");
                });

            migrationBuilder.CreateTable(
                name: "bucket_settings",
                columns: table => new
                {
                    bucket = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    quota_bytes = table.Column<long>(type: "bigint", nullable: true),
                    access_mode = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_bucket_settings", x => x.bucket);
                    table.CheckConstraint("ck_bucket_settings_access_mode", "\"access_mode\" IN ('Private', 'PublicRead')");
                });

            migrationBuilder.CreateIndex(
                name: "ix_audit_events_action",
                table: "audit_events",
                column: "action");

            migrationBuilder.CreateIndex(
                name: "ix_audit_events_actor",
                table: "audit_events",
                column: "actor");

            migrationBuilder.CreateIndex(
                name: "ix_audit_events_occurred_at",
                table: "audit_events",
                column: "occurred_at");

            migrationBuilder.CreateIndex(
                name: "ix_audit_events_resource",
                table: "audit_events",
                column: "resource");

            migrationBuilder.CreateIndex(
                name: "ix_audit_events_result",
                table: "audit_events",
                column: "result");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "audit_events");

            migrationBuilder.DropTable(
                name: "bucket_settings");
        }
    }
}
