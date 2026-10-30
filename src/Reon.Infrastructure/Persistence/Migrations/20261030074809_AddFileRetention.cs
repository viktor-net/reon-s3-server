using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Reon.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddFileRetention : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_files_status",
                table: "files");

            migrationBuilder.AddColumn<int>(
                name: "retention_seconds",
                table: "multipart_uploads",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "expired_at",
                table: "files",
                type: "timestamptz",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "expires_at",
                table: "files",
                type: "timestamptz",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "retention_hold",
                table: "files",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "ix_files_retention_due",
                table: "files",
                column: "expires_at",
                filter: "\"status\" = 'Ready' AND \"retention_hold\" = false");

            migrationBuilder.CreateIndex(
                name: "ix_files_status_expired_at",
                table: "files",
                columns: new[] { "status", "expired_at" },
                filter: "\"status\" = 'Expired'");

            migrationBuilder.AddCheckConstraint(
                name: "ck_files_status",
                table: "files",
                sql: "\"status\" IN ('Pending', 'Ready', 'Deleting', 'Deleted', 'Failed', 'Missing', 'Corrupted', 'Expired')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_files_retention_due",
                table: "files");

            migrationBuilder.DropIndex(
                name: "ix_files_status_expired_at",
                table: "files");

            migrationBuilder.DropCheckConstraint(
                name: "ck_files_status",
                table: "files");

            migrationBuilder.DropColumn(
                name: "retention_seconds",
                table: "multipart_uploads");

            migrationBuilder.DropColumn(
                name: "expired_at",
                table: "files");

            migrationBuilder.DropColumn(
                name: "expires_at",
                table: "files");

            migrationBuilder.DropColumn(
                name: "retention_hold",
                table: "files");

            migrationBuilder.AddCheckConstraint(
                name: "ck_files_status",
                table: "files",
                sql: "\"status\" IN ('Pending', 'Ready', 'Deleting', 'Deleted', 'Failed', 'Missing', 'Corrupted')");
        }
    }
}
