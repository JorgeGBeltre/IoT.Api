using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IoT.Infrastructure.Migrations
{
    /// <summary>
    /// Aislamiento por SUBÁRBOL de la jerarquía Customer (ADR-013, fase E8.3) a nivel de base de
    /// datos, encima del aislamiento por tenant de ADR-003. Sigue el patrón EXACTO de la migración
    /// de políticas RLS (20261026143500): <c>current_setting(..., true)</c>, <c>NULLIF(...,'')</c>
    /// para la cadena vacía, casts explícitos, comillas dobles en los identificadores.
    ///
    /// Semántica del subárbol (vía la GUC <c>app.customer_path</c>, fijada por TenantSessionInterceptor):
    ///   - sesión de sistema                → ve todo (como hoy);
    ///   - app.customer_path vacío/ausente  → ve todo el tenant (RAÍZ / token antiguo = NO DISRUPTIVO);
    ///   - app.customer_path = ltree        → sólo filas del subárbol (descendiente-o-igual: <c>&lt;@</c>).
    ///
    /// Las tablas de datos del dispositivo (positions, telemetry_records, device_commands,
    /// device_capabilities) NO reciben todavía el scope por subárbol; siguen SÓLO con aislamiento
    /// por tenant. Su acotación por customer vía el device es seguimiento E8.3b (rendimiento en
    /// tablas calientes). Brecha conocida: un customer-admin puede ver telemetría tenant-wide hasta E8.3b.
    /// </summary>
    /// <inheritdoc />
    public partial class AddCustomerSubtreeRls : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1. customers: aislamiento por tenant Y por subárbol en UNA sola política (AND). La
            //    tabla se creó después de la migración de RLS base, así que aquí se le habilita RLS
            //    por primera vez. path vacío = ver todo el tenant.
            migrationBuilder.Sql(@"
ALTER TABLE customers ENABLE ROW LEVEL SECURITY;
ALTER TABLE customers FORCE ROW LEVEL SECURITY;
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

            // 2. devices y users: se CONSERVA el check de tenant (idéntico al de ADR-003) y se le
            //    AÑADE con AND el check de subárbol. Es UNA sola política (AND interno): usar una
            //    segunda política permisiva ensancharía el acceso (las permisivas se combinan con OR).
            //    customer_path vacío O CustomerId NULL → sin recorte (NO DISRUPTIVO / filas sin nodo).
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
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Restaurar devices/users a la política SÓLO por tenant (estado previo, ADR-003).
            migrationBuilder.Sql(@"
DROP POLICY IF EXISTS tenant_isolation_devices ON devices;
CREATE POLICY tenant_isolation_devices ON devices FOR ALL USING (
    current_setting('app.is_system_session', true) = 'true'
    OR ""TenantId"" = NULLIF(current_setting('app.tenant_id', true), '')::uuid
) WITH CHECK (
    current_setting('app.is_system_session', true) = 'true'
    OR ""TenantId"" = NULLIF(current_setting('app.tenant_id', true), '')::uuid
);

DROP POLICY IF EXISTS tenant_isolation_users ON users;
CREATE POLICY tenant_isolation_users ON users FOR ALL USING (
    current_setting('app.is_system_session', true) = 'true'
    OR ""TenantId"" = NULLIF(current_setting('app.tenant_id', true), '')::uuid
) WITH CHECK (
    current_setting('app.is_system_session', true) = 'true'
    OR ""TenantId"" = NULLIF(current_setting('app.tenant_id', true), '')::uuid
);
");

            // Quitar la política y la RLS de customers (vuelve a su estado sin RLS previo a E8.3).
            migrationBuilder.Sql(@"
DROP POLICY IF EXISTS tenant_isolation_customers ON customers;
ALTER TABLE customers NO FORCE ROW LEVEL SECURITY;
ALTER TABLE customers DISABLE ROW LEVEL SECURITY;
");
        }
    }
}
