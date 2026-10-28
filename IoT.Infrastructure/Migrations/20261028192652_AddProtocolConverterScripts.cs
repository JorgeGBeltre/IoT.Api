using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IoT.Infrastructure.Migrations
{
    /// <summary>
    /// Convertidores dinámicos por tenant (adopción ThingsBoard, TB-2): tabla
    /// <c>protocol_converter_scripts</c>. Es CONFIGURACIÓN a nivel de tenant (como mqtt_brokers /
    /// tenant_settings), así que su RLS es la política <c>tenant_isolation</c> ESTÁNDAR por
    /// <c>TenantId</c> (misma forma que las 15 tablas base de
    /// <c>20261026143500_AddPostgresRowLevelSecurityPolicies</c>): ENABLE + FORCE ROW LEVEL SECURITY y
    /// UNA política combinada con USING == WITH CHECK —rama de sistema (ingesta/jobs) o
    /// <c>"TenantId" = app.tenant_id</c>—. NO lleva recorte por subárbol de customer: un script de
    /// decodificación no es dato de un customer concreto. El rol de runtime <c>iot_app</c> recibe los
    /// GRANT sobre la tabla nueva vía <see cref="Persistence.RlsRoleBootstrapper"/> (GRANT ALL ON ALL
    /// TABLES, re-aplicado en cada arranque tras migrar); no hace falta tocarlo aquí.
    /// </summary>
    /// <inheritdoc />
    public partial class AddProtocolConverterScripts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "protocol_converter_scripts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProfileKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    UplinkScript = table.Column<string>(type: "text", nullable: false),
                    DownlinkScript = table.Column<string>(type: "text", nullable: true),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_protocol_converter_scripts", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_protocol_converter_scripts_TenantId_ProfileKey",
                table: "protocol_converter_scripts",
                columns: new[] { "TenantId", "ProfileKey" },
                unique: true);

            // RLS por TENANT (política tenant_isolation estándar). USING == WITH CHECK; rama de sistema
            // (ingesta / jobs sin tenant) o igualdad de TenantId con la GUC app.tenant_id.
            migrationBuilder.Sql(@"
ALTER TABLE protocol_converter_scripts ENABLE ROW LEVEL SECURITY;
ALTER TABLE protocol_converter_scripts FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS tenant_isolation_protocol_converter_scripts ON protocol_converter_scripts;
CREATE POLICY tenant_isolation_protocol_converter_scripts ON protocol_converter_scripts FOR ALL USING (
    current_setting('app.is_system_session', true) = 'true'
    OR ""TenantId"" = NULLIF(current_setting('app.tenant_id', true), '')::uuid
) WITH CHECK (
    current_setting('app.is_system_session', true) = 'true'
    OR ""TenantId"" = NULLIF(current_setting('app.tenant_id', true), '')::uuid
);
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"DROP POLICY IF EXISTS tenant_isolation_protocol_converter_scripts ON protocol_converter_scripts;");

            migrationBuilder.DropTable(
                name: "protocol_converter_scripts");
        }
    }
}
