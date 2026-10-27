using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IoT.Infrastructure.Migrations
{
    /// <summary>
    /// Modelo de atributos de dispositivo (ThingsBoard, fase P1a): tabla <c>device_attributes</c>
    /// (dato PROPIEDAD DEL DEVICE) + su RLS por subárbol FAIL-CLOSED vía el <c>Customer</c> del device
    /// dueño. Reutiliza el patrón EXACTO de <c>20261027180000_AddSubtreeCleanPolicies</c> para las
    /// tablas propiedad del device (positions/telemetry_records/device_commands): ENABLE + FORCE ROW
    /// LEVEL SECURITY y UNA sola política combinada con AND (jamás una segunda permisiva: se
    /// combinarían con OR y ensancharían el acceso), con USING == WITH CHECK:
    ///   sistema (<c>app.is_system_session='true'</c>)  → sin restricción (ingesta / jobs);
    ///   check de tenant de ADR-003                     → <c>"TenantId" = app.tenant_id</c>;
    ///   <c>app.customer_path</c> vacío/ausente         → sin recorte por subárbol (raíz / token antiguo = NO DISRUPTIVO);
    ///   en otro caso                                   → el device dueño debe tener un customer del subárbol (<c>&lt;@</c>).
    /// SIN rama de customer nulo: <c>devices.CustomerId</c> es NOT NULL desde E8.4a. El rol de runtime
    /// <c>iot_app</c> recibe los GRANT sobre la tabla nueva vía <c>RlsRoleBootstrapper</c>
    /// (GRANT ALL ON ALL TABLES, re-aplicado en cada arranque tras migrar), no hace falta tocarlo aquí.
    /// </summary>
    /// <inheritdoc />
    public partial class AddDeviceAttributes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "device_attributes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    DeviceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Scope = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Value = table.Column<string>(type: "text", nullable: false),
                    LastUpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_device_attributes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_device_attributes_devices_DeviceId",
                        column: x => x.DeviceId,
                        principalTable: "devices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_device_attributes_DeviceId",
                table: "device_attributes",
                column: "DeviceId");

            migrationBuilder.CreateIndex(
                name: "IX_device_attributes_TenantId_DeviceId_Scope",
                table: "device_attributes",
                columns: new[] { "TenantId", "DeviceId", "Scope" });

            migrationBuilder.CreateIndex(
                name: "IX_device_attributes_TenantId_DeviceId_Scope_Key",
                table: "device_attributes",
                columns: new[] { "TenantId", "DeviceId", "Scope", "Key" },
                unique: true);

            // RLS fail-closed por subárbol vía el device dueño (patrón EXACTO de positions/telemetry
            // en 20261027180000). USING == WITH CHECK; conserva el check de tenant (ADR-003) y las
            // ramas de sistema y path vacío; SIN rama de customer nulo (devices.CustomerId NOT NULL).
            migrationBuilder.Sql(@"
ALTER TABLE device_attributes ENABLE ROW LEVEL SECURITY;
ALTER TABLE device_attributes FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS tenant_isolation_device_attributes ON device_attributes;
CREATE POLICY tenant_isolation_device_attributes ON device_attributes FOR ALL USING (
    current_setting('app.is_system_session', true) = 'true'
    OR (
        ""TenantId"" = NULLIF(current_setting('app.tenant_id', true), '')::uuid
        AND (
            NULLIF(current_setting('app.customer_path', true), '') IS NULL
            OR EXISTS (
                SELECT 1 FROM devices d
                WHERE d.""Id"" = device_attributes.""DeviceId""
                AND EXISTS (
                    SELECT 1 FROM customers c
                    WHERE c.""Id"" = d.""CustomerId""
                    AND c.""Path"" <@ NULLIF(current_setting('app.customer_path', true), '')::ltree
                )
            )
        )
    )
) WITH CHECK (
    current_setting('app.is_system_session', true) = 'true'
    OR (
        ""TenantId"" = NULLIF(current_setting('app.tenant_id', true), '')::uuid
        AND (
            NULLIF(current_setting('app.customer_path', true), '') IS NULL
            OR EXISTS (
                SELECT 1 FROM devices d
                WHERE d.""Id"" = device_attributes.""DeviceId""
                AND EXISTS (
                    SELECT 1 FROM customers c
                    WHERE c.""Id"" = d.""CustomerId""
                    AND c.""Path"" <@ NULLIF(current_setting('app.customer_path', true), '')::ltree
                )
            )
        )
    )
);
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"DROP POLICY IF EXISTS tenant_isolation_device_attributes ON device_attributes;");

            migrationBuilder.DropTable(
                name: "device_attributes");
        }
    }
}
