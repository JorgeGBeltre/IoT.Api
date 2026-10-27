using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IoT.Infrastructure.Migrations
{
    /// <summary>
    /// Cierra la FUGA DE DATOS DE DISPOSITIVO ENTRE HERMANOS (ADR-013, fase E8.3b). Las tablas de
    /// datos calientes <c>positions</c>, <c>telemetry_records</c>, <c>device_commands</c> y
    /// <c>device_capabilities</c> quedaron tras E8.3 SÓLO con aislamiento por tenant (ADR-003): no
    /// tienen <c>CustomerId</c> propio, así que el filtro de subárbol (que opera sobre
    /// <c>ICustomerScoped</c>) nunca las tocó. Un customer-admin de un nodo no-raíz podía leer GPS /
    /// telemetría / comandos de un dispositivo de su HERMANO (mismo tenant) consultando esas tablas
    /// por <c>DeviceId</c>, aunque la fila <c>devices</c> del hermano estuviera oculta.
    ///
    /// Esta migración acota esas 4 tablas al SUBÁRBOL del usuario vía el <c>Customer</c> del DEVICE
    /// DUEÑO, preservando intacto el aislamiento por tenant (ADR-003) y la NO DISRUPCIÓN:
    ///   - sesión de sistema                → ve todo (como hoy);
    ///   - app.customer_path vacío/ausente  → ve todo el tenant (RAÍZ / token antiguo = NO DISRUPTIVO);
    ///   - device con <c>CustomerId</c> NULL → visible dentro del tenant (no se oculta);
    ///   - en otro caso                      → sólo si el customer dueño del device desciende-o-es-igual
    ///                                         del nodo del usuario (<c>&lt;@</c>).
    ///
    /// Patrón EXACTO de 20261027143609 (devices/users): <c>current_setting(..., true)</c>,
    /// <c>NULLIF(...,'')</c> antes del cast a <c>ltree</c>, comillas dobles en los identificadores, y
    /// UNA sola política combinada con AND (jamás una segunda permisiva: se combinarían con OR y
    /// ensancharían el acceso). Las tablas con tenant propio (positions/telemetry/commands) conservan
    /// su check de tenant idéntico al de ADR-003 y le AÑADEN con AND el recorte por subárbol;
    /// <c>device_capabilities</c> (sin tenant propio, lo deriva vía <c>devices</c>) EXTIENDE su EXISTS.
    /// </summary>
    /// <inheritdoc />
    public partial class AddDeviceDataSubtreeRls : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // positions, telemetry_records, device_commands: conservan el check de tenant (ADR-003)
            // y le añaden con AND el recorte por subárbol vía el device dueño. EXISTS sobre devices
            // por "DeviceId"; dentro, el customer del device debe descender-o-ser-igual del path del
            // usuario. path vacío (NULLIF→NULL) O device con "CustomerId" NULL ⇒ sin recorte.
            migrationBuilder.Sql(@"
DROP POLICY IF EXISTS tenant_isolation_positions ON positions;
CREATE POLICY tenant_isolation_positions ON positions FOR ALL USING (
    current_setting('app.is_system_session', true) = 'true'
    OR (
        ""TenantId"" = NULLIF(current_setting('app.tenant_id', true), '')::uuid
        AND (
            NULLIF(current_setting('app.customer_path', true), '') IS NULL
            OR EXISTS (
                SELECT 1 FROM devices d
                WHERE d.""Id"" = positions.""DeviceId""
                AND (
                    d.""CustomerId"" IS NULL
                    OR EXISTS (
                        SELECT 1 FROM customers c
                        WHERE c.""Id"" = d.""CustomerId""
                        AND c.""Path"" <@ NULLIF(current_setting('app.customer_path', true), '')::ltree
                    )
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
                WHERE d.""Id"" = positions.""DeviceId""
                AND (
                    d.""CustomerId"" IS NULL
                    OR EXISTS (
                        SELECT 1 FROM customers c
                        WHERE c.""Id"" = d.""CustomerId""
                        AND c.""Path"" <@ NULLIF(current_setting('app.customer_path', true), '')::ltree
                    )
                )
            )
        )
    )
);

DROP POLICY IF EXISTS tenant_isolation_telemetry_records ON telemetry_records;
CREATE POLICY tenant_isolation_telemetry_records ON telemetry_records FOR ALL USING (
    current_setting('app.is_system_session', true) = 'true'
    OR (
        ""TenantId"" = NULLIF(current_setting('app.tenant_id', true), '')::uuid
        AND (
            NULLIF(current_setting('app.customer_path', true), '') IS NULL
            OR EXISTS (
                SELECT 1 FROM devices d
                WHERE d.""Id"" = telemetry_records.""DeviceId""
                AND (
                    d.""CustomerId"" IS NULL
                    OR EXISTS (
                        SELECT 1 FROM customers c
                        WHERE c.""Id"" = d.""CustomerId""
                        AND c.""Path"" <@ NULLIF(current_setting('app.customer_path', true), '')::ltree
                    )
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
                WHERE d.""Id"" = telemetry_records.""DeviceId""
                AND (
                    d.""CustomerId"" IS NULL
                    OR EXISTS (
                        SELECT 1 FROM customers c
                        WHERE c.""Id"" = d.""CustomerId""
                        AND c.""Path"" <@ NULLIF(current_setting('app.customer_path', true), '')::ltree
                    )
                )
            )
        )
    )
);

DROP POLICY IF EXISTS tenant_isolation_device_commands ON device_commands;
CREATE POLICY tenant_isolation_device_commands ON device_commands FOR ALL USING (
    current_setting('app.is_system_session', true) = 'true'
    OR (
        ""TenantId"" = NULLIF(current_setting('app.tenant_id', true), '')::uuid
        AND (
            NULLIF(current_setting('app.customer_path', true), '') IS NULL
            OR EXISTS (
                SELECT 1 FROM devices d
                WHERE d.""Id"" = device_commands.""DeviceId""
                AND (
                    d.""CustomerId"" IS NULL
                    OR EXISTS (
                        SELECT 1 FROM customers c
                        WHERE c.""Id"" = d.""CustomerId""
                        AND c.""Path"" <@ NULLIF(current_setting('app.customer_path', true), '')::ltree
                    )
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
                WHERE d.""Id"" = device_commands.""DeviceId""
                AND (
                    d.""CustomerId"" IS NULL
                    OR EXISTS (
                        SELECT 1 FROM customers c
                        WHERE c.""Id"" = d.""CustomerId""
                        AND c.""Path"" <@ NULLIF(current_setting('app.customer_path', true), '')::ltree
                    )
                )
            )
        )
    )
);
");

            // device_capabilities: su política ya deriva el tenant vía EXISTS sobre devices (no tiene
            // "TenantId" propio). Se CONSERVA esa lógica de tenant y se le AÑADE con AND el recorte por
            // subárbol del customer del device. path vacío O device con "CustomerId" NULL ⇒ sin recorte.
            migrationBuilder.Sql(@"
DROP POLICY IF EXISTS tenant_isolation_device_capabilities ON device_capabilities;
CREATE POLICY tenant_isolation_device_capabilities ON device_capabilities FOR ALL USING (
    current_setting('app.is_system_session', true) = 'true'
    OR EXISTS (
        SELECT 1 FROM devices d
        WHERE d.""Id"" = device_capabilities.""DeviceId""
        AND (d.""TenantId"" = NULLIF(current_setting('app.tenant_id', true), '')::uuid OR current_setting('app.is_system_session', true) = 'true')
        AND (
            NULLIF(current_setting('app.customer_path', true), '') IS NULL
            OR d.""CustomerId"" IS NULL
            OR EXISTS (
                SELECT 1 FROM customers c
                WHERE c.""Id"" = d.""CustomerId""
                AND c.""Path"" <@ NULLIF(current_setting('app.customer_path', true), '')::ltree
            )
        )
    )
) WITH CHECK (
    current_setting('app.is_system_session', true) = 'true'
    OR EXISTS (
        SELECT 1 FROM devices d
        WHERE d.""Id"" = device_capabilities.""DeviceId""
        AND (d.""TenantId"" = NULLIF(current_setting('app.tenant_id', true), '')::uuid OR current_setting('app.is_system_session', true) = 'true')
        AND (
            NULLIF(current_setting('app.customer_path', true), '') IS NULL
            OR d.""CustomerId"" IS NULL
            OR EXISTS (
                SELECT 1 FROM customers c
                WHERE c.""Id"" = d.""CustomerId""
                AND c.""Path"" <@ NULLIF(current_setting('app.customer_path', true), '')::ltree
            )
        )
    )
);
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Restaurar las 4 tablas a su política SÓLO por tenant (estado previo a E8.3b).
            // positions/telemetry_records/device_commands: igualdad de "TenantId" (ADR-003).
            migrationBuilder.Sql(@"
DROP POLICY IF EXISTS tenant_isolation_positions ON positions;
CREATE POLICY tenant_isolation_positions ON positions FOR ALL USING (
    current_setting('app.is_system_session', true) = 'true'
    OR ""TenantId"" = NULLIF(current_setting('app.tenant_id', true), '')::uuid
) WITH CHECK (
    current_setting('app.is_system_session', true) = 'true'
    OR ""TenantId"" = NULLIF(current_setting('app.tenant_id', true), '')::uuid
);

DROP POLICY IF EXISTS tenant_isolation_telemetry_records ON telemetry_records;
CREATE POLICY tenant_isolation_telemetry_records ON telemetry_records FOR ALL USING (
    current_setting('app.is_system_session', true) = 'true'
    OR ""TenantId"" = NULLIF(current_setting('app.tenant_id', true), '')::uuid
) WITH CHECK (
    current_setting('app.is_system_session', true) = 'true'
    OR ""TenantId"" = NULLIF(current_setting('app.tenant_id', true), '')::uuid
);

DROP POLICY IF EXISTS tenant_isolation_device_commands ON device_commands;
CREATE POLICY tenant_isolation_device_commands ON device_commands FOR ALL USING (
    current_setting('app.is_system_session', true) = 'true'
    OR ""TenantId"" = NULLIF(current_setting('app.tenant_id', true), '')::uuid
) WITH CHECK (
    current_setting('app.is_system_session', true) = 'true'
    OR ""TenantId"" = NULLIF(current_setting('app.tenant_id', true), '')::uuid
);
");

            // device_capabilities: restaurar el EXISTS sobre devices sólo por tenant (sin subárbol).
            migrationBuilder.Sql(@"
DROP POLICY IF EXISTS tenant_isolation_device_capabilities ON device_capabilities;
CREATE POLICY tenant_isolation_device_capabilities ON device_capabilities FOR ALL USING (
    current_setting('app.is_system_session', true) = 'true'
    OR EXISTS (
        SELECT 1 FROM devices d
        WHERE d.""Id"" = device_capabilities.""DeviceId""
        AND (d.""TenantId"" = NULLIF(current_setting('app.tenant_id', true), '')::uuid OR current_setting('app.is_system_session', true) = 'true')
    )
) WITH CHECK (
    current_setting('app.is_system_session', true) = 'true'
    OR EXISTS (
        SELECT 1 FROM devices d
        WHERE d.""Id"" = device_capabilities.""DeviceId""
        AND (d.""TenantId"" = NULLIF(current_setting('app.tenant_id', true), '')::uuid OR current_setting('app.is_system_session', true) = 'true')
    )
);
");
        }
    }
}
