using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IoT.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDeviceMqttApiKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "MqttApiKeyFingerprint",
                table: "devices",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MqttApiKeyHash",
                table: "devices",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "MqttApiKeyIssuedAt",
                table: "devices",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MqttApiKeyIssuedBy",
                table: "devices",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MqttApiKeyFingerprint",
                table: "devices");

            migrationBuilder.DropColumn(
                name: "MqttApiKeyHash",
                table: "devices");

            migrationBuilder.DropColumn(
                name: "MqttApiKeyIssuedAt",
                table: "devices");

            migrationBuilder.DropColumn(
                name: "MqttApiKeyIssuedBy",
                table: "devices");
        }
    }
}
