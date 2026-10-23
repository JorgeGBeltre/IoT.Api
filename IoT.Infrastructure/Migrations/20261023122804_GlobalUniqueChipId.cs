using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IoT.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class GlobalUniqueChipId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_devices_ChipId",
                table: "devices");

            migrationBuilder.CreateIndex(
                name: "IX_devices_ChipId",
                table: "devices",
                column: "ChipId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_devices_ChipId",
                table: "devices");

            migrationBuilder.CreateIndex(
                name: "IX_devices_ChipId",
                table: "devices",
                column: "ChipId");
        }
    }
}
