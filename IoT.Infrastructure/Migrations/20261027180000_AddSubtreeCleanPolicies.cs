using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IoT.Infrastructure.Migrations
{
    /// <summary>
    /// Políticas RLS por subárbol LIMPIAS y FAIL-CLOSED (ADR-013, fase E8.4a). SUPERSEDE a
    /// <c>20261027160000_AddSubtreeWriteCheckHardening</c>, que dejaba la rama
    /// <c>OR "CustomerId" IS NULL</c> (o <c>OR d."CustomerId" IS NULL</c>) en el USING de
    /// devices/users/positions/telemetry_records/device_commands/device_capabilities para no ocultar
    /// filas sin nodo en las LECTURAS.
    ///
    /// Tras <c>20261027170000_AddCustomerIdNotNull</c> YA NO existen filas con <c>CustomerId</c> nulo
    /// (la columna es NOT NULL y la creación/adopción asigna siempre el nodo). Por tanto la rama de
    /// null es código muerto y además era el ÚNICO resquicio por el que una sesión acotada podía:
    ///   - BORRAR (DELETE) filas con <c>CustomerId</c> nulo de su tenant (DELETE sólo se gobierna por
    ///     USING), o
    ///   - RECLAMARLAS vía UPDATE (la selección de fila del UPDATE también se gobierna por USING).
    /// Esta migración QUITA esa rama de null de AMBOS USING y WITH CHECK, dejando USING == WITH CHECK
    /// (salvo lo ya existente). Resultado: fail-closed limpio, sin escape por DELETE/UPDATE-reclamo.
    ///
    /// SE CONSERVAN INTACTAS las ramas NO DISRUPTIVAS: operación de sistema
    /// (<c>app.is_system_session = 'true'</c>) y <c>app.customer_path</c> vacío/ausente (raíz / token
    /// antiguo / fallback). Se CONSERVA INTACTO el check de tenant de ADR-003 en USING y WITH CHECK.
    /// <c>customers</c> no tenía rama de null (su pertenencia es su propio <c>Path</c>); se redefine
    /// idéntica por uniformidad. Patrón de quoting EXACTO de 20261026143500 / 20261027143609 /
    /// 20261027150000 / 20261027160000. El snapshot del modelo NO cambia (sólo cambian políticas).
    /// </summary>
    /// <inheritdoc />
    public partial class AddSubtreeCleanPolicies : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // customers: sin cambio funcional respecto a 20261027160000 (nunca tuvo rama de null).
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

            // devices y users: USING == WITH CHECK, SIN la rama OR ""CustomerId"" IS NULL. Una sesión
            // acotada sólo ve/escribe filas cuyo customer dueño desciende-o-es-igual de su nodo.
            migrationBuilder.Sql(@"
DROP POLICY IF EXISTS tenant_isolation_devices ON devices;
CREATE POLICY tenant_isolation_devices ON devices FOR ALL USING (
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

            // positions, telemetry_records, device_commands: USING == WITH CHECK, SIN la rama
            // d.""CustomerId"" IS NULL. El device dueño debe tener un customer del subárbol.
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

            // device_capabilities (sin TenantId propio; lo deriva vía devices): USING == WITH CHECK,
            // SIN la rama d.""CustomerId"" IS NULL. Se CONSERVA el check de tenant dentro del EXISTS.
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
            // Restaurar EXACTAMENTE las políticas de 20261027160000 (USING CON la rama de null del
            // customer en devices/users/positions/telemetry/commands/capabilities; WITH CHECK SIN
            // ella; customers idéntica).
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
    }
}
