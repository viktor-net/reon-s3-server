using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Reon.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddStorageCapabilitiesAndObjectLock : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "object_lock_enabled",
                table: "bucket_settings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "object_lock_mode",
                table: "bucket_settings",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "None");

            migrationBuilder.CreateTable(
                name: "storage_capabilities",
                columns: table => new
                {
                    name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    supported = table.Column<bool>(type: "boolean", nullable: false),
                    source = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    details = table.Column<string>(type: "text", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_storage_capabilities", x => x.name);
                    table.CheckConstraint("ck_storage_capabilities_source", "\"source\" IN ('config', 'learned', 'default')");
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_bucket_settings_object_lock_mode",
                table: "bucket_settings",
                sql: "\"object_lock_mode\" IN ('None', 'Provider', 'Service')");

            migrationBuilder.Sql(
                "COMMENT ON TABLE storage_capabilities IS 'Реестр возможностей текущего S3-провайдера';");
            migrationBuilder.Sql(
                "COMMENT ON COLUMN storage_capabilities.name IS 'Канонический ключ возможности: versioning, object-tags, lifecycle, bucket-policy, object-lock';");
            migrationBuilder.Sql(
                "COMMENT ON COLUMN storage_capabilities.supported IS 'Поддерживает ли провайдер возможность';");
            migrationBuilder.Sql(
                "COMMENT ON COLUMN storage_capabilities.source IS 'Источник значения: config, learned или default';");
            migrationBuilder.Sql(
                "COMMENT ON COLUMN storage_capabilities.details IS 'Пояснение для выученного ограничения';");
            migrationBuilder.Sql(
                "COMMENT ON COLUMN storage_capabilities.updated_at IS 'Когда значение было обновлено';");
            migrationBuilder.Sql(
                "COMMENT ON COLUMN bucket_settings.object_lock_enabled IS 'Защита бакета от удаления включена';");
            migrationBuilder.Sql(
                "COMMENT ON COLUMN bucket_settings.object_lock_mode IS 'Источник защиты: None, Provider (S3 Object Lock) или Service (уровень сервиса)';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "storage_capabilities");

            migrationBuilder.DropCheckConstraint(
                name: "ck_bucket_settings_object_lock_mode",
                table: "bucket_settings");

            migrationBuilder.DropColumn(
                name: "object_lock_enabled",
                table: "bucket_settings");

            migrationBuilder.DropColumn(
                name: "object_lock_mode",
                table: "bucket_settings");
        }
    }
}
