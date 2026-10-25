using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IoT.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddTenantBootstrapApiKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BootstrapApiKeyFingerprint",
                table: "tenants",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BootstrapApiKeyHash",
                table: "tenants",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "BootstrapApiKeyIssuedAt",
                table: "tenants",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BootstrapApiKeyIssuedBy",
                table: "tenants",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_tenants_BootstrapApiKeyHash",
                table: "tenants",
                column: "BootstrapApiKeyHash",
                unique: true,
                filter: "\"BootstrapApiKeyHash\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_tenants_BootstrapApiKeyHash",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "BootstrapApiKeyFingerprint",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "BootstrapApiKeyHash",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "BootstrapApiKeyIssuedAt",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "BootstrapApiKeyIssuedBy",
                table: "tenants");
        }
    }
}
