using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IoT.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddFirmwareOta : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "OtaCompleted",
                table: "devices",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "OtaErrorMessage",
                table: "devices",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "OtaInProgress",
                table: "devices",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "OtaLastChunkSent",
                table: "devices",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "OtaTargetVersion",
                table: "devices",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "OtaTotalChunks",
                table: "devices",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "OtaUpdatedAt",
                table: "devices",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "firmware_version_records",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    FirmwareVersion = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Src = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    TargetModel = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    FeatureDescription = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    FileSizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    Sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    TotalChunks = table.Column<int>(type: "integer", nullable: false),
                    ChunkSizeBytes = table.Column<int>(type: "integer", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    UpdatedFromIp = table.Column<string>(type: "character varying(45)", maxLength: 45, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_firmware_version_records", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_firmware_version_records_TenantId_FirmwareVersion",
                table: "firmware_version_records",
                columns: new[] { "TenantId", "FirmwareVersion" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_firmware_version_records_TenantId_TargetModel",
                table: "firmware_version_records",
                columns: new[] { "TenantId", "TargetModel" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "firmware_version_records");

            migrationBuilder.DropColumn(
                name: "OtaCompleted",
                table: "devices");

            migrationBuilder.DropColumn(
                name: "OtaErrorMessage",
                table: "devices");

            migrationBuilder.DropColumn(
                name: "OtaInProgress",
                table: "devices");

            migrationBuilder.DropColumn(
                name: "OtaLastChunkSent",
                table: "devices");

            migrationBuilder.DropColumn(
                name: "OtaTargetVersion",
                table: "devices");

            migrationBuilder.DropColumn(
                name: "OtaTotalChunks",
                table: "devices");

            migrationBuilder.DropColumn(
                name: "OtaUpdatedAt",
                table: "devices");
        }
    }
}
