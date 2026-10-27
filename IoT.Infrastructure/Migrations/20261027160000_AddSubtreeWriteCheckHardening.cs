using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IoT.Infrastructure.Migrations
{
    /// <summary>
    /// Endurecimiento FAIL-CLOSED del WITH CHECK de las políticas RLS por subárbol (ADR-013, fase
    /// E8.3/E8.3b → punto 4 del endurecimiento E8.3b). Hasta ahora USING y WITH CHECK eran idénticos
    /// y ambos llevaban una rama <c>OR "CustomerId" IS NULL</c> (o <c>OR d."CustomerId" IS NULL</c>)
    /// que es NO DISRUPTIVA en LECTURAS (no oculta filas existentes sin nodo) pero, aplicada también
    /// al WITH CHECK, dejaba FAIL-OPEN la ESCRITURA: una sesión acotada a un subárbol
    /// (<c>app.customer_path</c> NO vacío y NO sistema) podía escribir una fila con el customer fuera
    /// de su subárbol simplemente dejándolo NULL, escapándose del subárbol al escribir.
    ///
    /// Esta migración SEPARA USING de WITH CHECK (<c>CREATE POLICY ... USING (...) WITH CHECK (...)</c>):
    ///   - USING QUEDA IGUAL que ahora (conserva la rama de null del customer) ⇒ las LECTURAS no
    ///     cambian: una fila con <c>CustomerId</c> NULL dentro del tenant sigue siendo visible.
    ///   - WITH CHECK QUITA la rama de null del customer para la sesión acotada: una fila escrita por
    ///     una sesión con <c>app.customer_path</c> NO vacío sólo es válida si su customer (o el del
    ///     device dueño) es NO NULL y desciende-o-es-igual del nodo (<c>path &lt;@ app.customer_path</c>).
    ///
    /// NO DISRUPTIVO: se CONSERVAN en WITH CHECK las ramas de (1) operación de sistema
    /// (<c>app.is_system_session = 'true'</c>) y (2) raíz / token antiguo (<c>app.customer_path</c>
    /// vacío ⇒ <c>NULLIF(...) IS NULL</c>), ambas SIN restricción de customer. Así no se rompe la
    /// ingesta por sistema ni la creación por un admin raíz (que hoy aún crea devices con
    /// <c>CustomerId</c> NULL). En la práctica esto sólo afecta a sesiones de sub-customer (que aún no
    /// existen: punto 1/3 de E8.4 introduce su creación), de ahí la disrupción CERO.
    ///
    /// Se CONSERVA INTACTO el check de tenant (ADR-003) tanto en USING como en WITH CHECK. El patrón
    /// de quoting es el EXACTO de 20261026143500 / 20261027143609 / 20261027150000:
    /// <c>current_setting(..., true)</c>, <c>NULLIF(...,'')</c> antes del cast, comillas dobles en los
    /// identificadores. El snapshot del modelo NO cambia (sólo cambian políticas, no el esquema).
    /// </summary>
    /// <inheritdoc />
    public partial class AddSubtreeWriteCheckHardening : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // customers: la pertenencia es su PROPIO Path; su WITH CHECK ya era fail-closed para la
            // sesión acotada (no tenía rama de null). Se redefine por uniformidad con USING == WITH
            // CHECK (idénticos): una fila escrita sólo es válida si su Path desciende-o-es-igual del
            // nodo (o sistema / raíz). Sin cambio funcional respecto a 20261027143609.
            migrationBuilder.Sql(@"
DROP POLICY IF EXISTS tenant_isolation_customers ON customers;
CREATE POLICY tenant_isolation_customers ON customers FOR ALL USING (
    current_setting('app.is_system_session', true) = 'true'
    OR (
        ""TenantId"" = NULLIF(current_setting('app.tenant_id', true), '')::uuid
        AND (
            NULLIF(current_setting('app.customer_path', true), '') IS NULL
            OR ""Path"" <@ NULLIF(current_setting('app.customer_path', true), '')::ltree
        )
    )
) WITH CHECK (
    current_setting('app.is_system_session', true) = 'true'
    OR (
        ""TenantId"" = NULLIF(current_setting('app.tenant_id', true), '')::uuid
        AND (
            NULLIF(current_setting('app.customer_path', true), '') IS NULL
            OR ""Path"" <@ NULLIF(current_setting('app.customer_path', true), '')::ltree
        )
    )
);
");

            // devices y users: USING conserva la rama OR ""CustomerId"" IS NULL (lecturas no
            // disruptivas); WITH CHECK la QUITA. Para una sesión acotada (path no vacío, no sistema)
            // la escritura exige EXISTS un customer del subárbol dueño de la fila (CustomerId NO NULL).
            migrationBuilder.Sql(@"
DROP POLICY IF EXISTS tenant_isolation_devices ON devices;
CREATE POLICY tenant_isolation_devices ON devices FOR ALL USING (
    current_setting('app.is_system_session', true) = 'true'
    OR (
        ""TenantId"" = NULLIF(current_setting('app.tenant_id', true), '')::uuid
        AND (
            NULLIF(current_setting('app.customer_path', true), '') IS NULL
            OR ""CustomerId"" IS NULL
            OR EXISTS (
                SELECT 1 FROM customers c
                WHERE c.""Id"" = devices.""CustomerId""
                AND c.""Path"" <@ NULLIF(current_setting('app.customer_path', true), '')::ltree
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
                SELECT 1 FROM customers c
                WHERE c.""Id"" = devices.""CustomerId""
                AND c.""Path"" <@ NULLIF(current_setting('app.customer_path', true), '')::ltree
            )
        )
    )
);

DROP POLICY IF EXISTS tenant_isolation_users ON users;
CREATE POLICY tenant_isolation_users ON users FOR ALL USING (
    current_setting('app.is_system_session', true) = 'true'
    OR (
        ""TenantId"" = NULLIF(current_setting('app.tenant_id', true), '')::uuid
        AND (
            NULLIF(current_setting('app.customer_path', true), '') IS NULL
            OR ""CustomerId"" IS NULL
            OR EXISTS (
                SELECT 1 FROM customers c
                WHERE c.""Id"" = users.""CustomerId""
                AND c.""Path"" <@ NULLIF(current_setting('app.customer_path', true), '')::ltree
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
                SELECT 1 FROM customers c
                WHERE c.""Id"" = users.""CustomerId""
                AND c.""Path"" <@ NULLIF(current_setting('app.customer_path', true), '')::ltree
            )
        )
    )
);
");

            // positions, telemetry_records, device_commands: USING conserva la rama
            // d.""CustomerId"" IS NULL (lecturas no disruptivas); WITH CHECK la QUITA. Una sesión
            // acotada sólo puede escribir una fila cuyo DEVICE dueño tenga un customer del subárbol.
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
                AND EXISTS (
                    SELECT 1 FROM customers c
                    WHERE c.""Id"" = d.""CustomerId""
                    AND c.""Path"" <@ NULLIF(current_setting('app.customer_path', true), '')::ltree
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
                AND EXISTS (
                    SELECT 1 FROM customers c
                    WHERE c.""Id"" = d.""CustomerId""
                    AND c.""Path"" <@ NULLIF(current_setting('app.customer_path', true), '')::ltree
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

            // device_capabilities: sin TenantId propio (lo deriva vía devices). USING conserva la
            // rama d.""CustomerId"" IS NULL; WITH CHECK la QUITA. Se CONSERVA INTACTO el check de
            // tenant dentro del EXISTS sobre devices.
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
            // Restaurar las políticas al estado previo (20261027143609 + 20261027150000): USING y
            // WITH CHECK IDÉNTICOS, CON la rama de null del customer también en WITH CHECK.

            // customers (= 20261027143609).
            migrationBuilder.Sql(@"
DROP POLICY IF EXISTS tenant_isolation_customers ON customers;
CREATE POLICY tenant_isolation_customers ON customers FOR ALL USING (
    current_setting('app.is_system_session', true) = 'true'
    OR (
        ""TenantId"" = NULLIF(current_setting('app.tenant_id', true), '')::uuid
        AND (
            NULLIF(current_setting('app.customer_path', true), '') IS NULL
            OR ""Path"" <@ NULLIF(current_setting('app.customer_path', true), '')::ltree
        )
    )
) WITH CHECK (
    current_setting('app.is_system_session', true) = 'true'
    OR (
        ""TenantId"" = NULLIF(current_setting('app.tenant_id', true), '')::uuid
        AND (
            NULLIF(current_setting('app.customer_path', true), '') IS NULL
            OR ""Path"" <@ NULLIF(current_setting('app.customer_path', true), '')::ltree
        )
    )
);
");

            // devices y users (= 20261027143609): WITH CHECK CON la rama OR ""CustomerId"" IS NULL.
            migrationBuilder.Sql(@"
DROP POLICY IF EXISTS tenant_isolation_devices ON devices;
CREATE POLICY tenant_isolation_devices ON devices FOR ALL USING (
    current_setting('app.is_system_session', true) = 'true'
    OR (
        ""TenantId"" = NULLIF(current_setting('app.tenant_id', true), '')::uuid
        AND (
            NULLIF(current_setting('app.customer_path', true), '') IS NULL
            OR ""CustomerId"" IS NULL
            OR EXISTS (
                SELECT 1 FROM customers c
                WHERE c.""Id"" = devices.""CustomerId""
                AND c.""Path"" <@ NULLIF(current_setting('app.customer_path', true), '')::ltree
            )
        )
    )
) WITH CHECK (
    current_setting('app.is_system_session', true) = 'true'
    OR (
        ""TenantId"" = NULLIF(current_setting('app.tenant_id', true), '')::uuid
        AND (
            NULLIF(current_setting('app.customer_path', true), '') IS NULL
            OR ""CustomerId"" IS NULL
            OR EXISTS (
                SELECT 1 FROM customers c
                WHERE c.""Id"" = devices.""CustomerId""
                AND c.""Path"" <@ NULLIF(current_setting('app.customer_path', true), '')::ltree
            )
        )
    )
);

DROP POLICY IF EXISTS tenant_isolation_users ON users;
CREATE POLICY tenant_isolation_users ON users FOR ALL USING (
    current_setting('app.is_system_session', true) = 'true'
    OR (
        ""TenantId"" = NULLIF(current_setting('app.tenant_id', true), '')::uuid
        AND (
            NULLIF(current_setting('app.customer_path', true), '') IS NULL
            OR ""CustomerId"" IS NULL
            OR EXISTS (
                SELECT 1 FROM customers c
                WHERE c.""Id"" = users.""CustomerId""
                AND c.""Path"" <@ NULLIF(current_setting('app.customer_path', true), '')::ltree
            )
        )
    )
) WITH CHECK (
    current_setting('app.is_system_session', true) = 'true'
    OR (
        ""TenantId"" = NULLIF(current_setting('app.tenant_id', true), '')::uuid
        AND (
            NULLIF(current_setting('app.customer_path', true), '') IS NULL
            OR ""CustomerId"" IS NULL
            OR EXISTS (
                SELECT 1 FROM customers c
                WHERE c.""Id"" = users.""CustomerId""
                AND c.""Path"" <@ NULLIF(current_setting('app.customer_path', true), '')::ltree
            )
        )
    )
);
");

            // positions, telemetry_records, device_commands (= 20261027150000): WITH CHECK CON la
            // rama d.""CustomerId"" IS NULL.
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

            // device_capabilities (= 20261027150000): WITH CHECK CON la rama d.""CustomerId"" IS NULL.
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
    }
}
