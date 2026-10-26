using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IoT.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPostgresRowLevelSecurityPolicies : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1. El rol de aplicación (iot_app, NOBYPASSRLS) y sus GRANTs ya NO se crean aquí.
            //    Llevaban una contraseña literal versionada (secreto en el repo) e idéntica en
            //    todos los despliegues. Ahora los provisiona RlsRoleBootstrapper al arrancar,
            //    como admin y tras migrar, tomando usuario/clave de ConnectionStrings:Postgres
            //    (fuente única). Esta migración conserva sólo el esquema y las políticas RLS,
            //    que sí son fuente de verdad del esquema (ADR-010). Ver RlsRoleBootstrapper.

            // 2. Ensure missing tables exist if not previously migrated
            migrationBuilder.Sql(@"
CREATE TABLE IF NOT EXISTS mqtt_brokers (
    ""Id"" uuid NOT NULL,
    ""TenantId"" uuid NOT NULL,
    ""Name"" character varying(120) NOT NULL,
    ""Host"" character varying(255) NOT NULL,
    ""Port"" integer NOT NULL,
    ""Protocol"" character varying(20) NOT NULL,
    ""TlsEnabled"" boolean NOT NULL,
    ""AuthMethod"" character varying(30) NOT NULL,
    ""IsActive"" boolean NOT NULL,
    ""CreatedAt"" timestamp with time zone NOT NULL,
    ""UpdatedAt"" timestamp with time zone NOT NULL,
    ""LastSeenAt"" timestamp with time zone NULL,
    CONSTRAINT ""PK_mqtt_brokers"" PRIMARY KEY (""Id"")
);
CREATE UNIQUE INDEX IF NOT EXISTS ""IX_mqtt_brokers_TenantId_Name"" ON mqtt_brokers (""TenantId"", ""Name"");
CREATE INDEX IF NOT EXISTS ""IX_mqtt_brokers_TenantId"" ON mqtt_brokers (""TenantId"");

CREATE TABLE IF NOT EXISTS tenant_settings (
    ""Id"" uuid NOT NULL,
    ""TenantId"" uuid NOT NULL,
    ""RetentionDays"" integer NOT NULL,
    ""AllowOfflineQueue"" boolean NOT NULL,
    ""MapProvider"" character varying(30) NOT NULL,
    ""WebhookUrl"" character varying(2048) NOT NULL,
    ""CreatedAt"" timestamp with time zone NOT NULL,
    ""UpdatedAt"" timestamp with time zone NOT NULL,
    CONSTRAINT ""PK_tenant_settings"" PRIMARY KEY (""Id"")
);
CREATE UNIQUE INDEX IF NOT EXISTS ""IX_tenant_settings_TenantId"" ON tenant_settings (""TenantId"");
");

            // 3. Enable Row-Level Security (RLS) on all 15 tenant-scoped tables with TenantId column
            migrationBuilder.Sql(@"
DO $$
DECLARE
    t text;
    tables text[] := ARRAY[
        'audit_logs',
        'device_commands',
        'device_groups',
        'devices',
        'firmware_version_records',
        'mqtt_brokers',
        'otp_codes',
        'password_reset_tokens',
        'positions',
        'role_permissions',
        'roles',
        'telemetry_records',
        'tenant_settings',
        'user_permissions',
        'users'
    ];
BEGIN
    FOREACH t IN ARRAY tables
    LOOP
        EXECUTE format('ALTER TABLE %I ENABLE ROW LEVEL SECURITY;', t);
        EXECUTE format('ALTER TABLE %I FORCE ROW LEVEL SECURITY;', t);
        EXECUTE format('DROP POLICY IF EXISTS %I ON %I;', 'tenant_isolation_' || t, t);
        EXECUTE format('CREATE POLICY %I ON %I FOR ALL USING (
            current_setting(''app.is_system_session'', true) = ''true''
            OR ""TenantId"" = NULLIF(current_setting(''app.tenant_id'', true), '''')::uuid
        ) WITH CHECK (
            current_setting(''app.is_system_session'', true) = ''true''
            OR ""TenantId"" = NULLIF(current_setting(''app.tenant_id'', true), '''')::uuid
        );', 'tenant_isolation_' || t, t);
    END LOOP;

    -- Device capabilities policy linked to devices
    ALTER TABLE device_capabilities ENABLE ROW LEVEL SECURITY;
    ALTER TABLE device_capabilities FORCE ROW LEVEL SECURITY;
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
END $$;
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
DO $$
DECLARE
    t text;
    tables text[] := ARRAY[
        'audit_logs',
        'device_commands',
        'device_groups',
        'devices',
        'device_capabilities',
        'firmware_version_records',
        'mqtt_brokers',
        'otp_codes',
        'password_reset_tokens',
        'positions',
        'role_permissions',
        'roles',
        'telemetry_records',
        'tenant_settings',
        'user_permissions',
        'users'
    ];
BEGIN
    FOREACH t IN ARRAY tables
    LOOP
        EXECUTE format('DROP POLICY IF EXISTS %I ON %I;', 'tenant_isolation_' || t, t);
        EXECUTE format('ALTER TABLE %I NO FORCE ROW LEVEL SECURITY;', t);
        EXECUTE format('ALTER TABLE %I DISABLE ROW LEVEL SECURITY;', t);
    END LOOP;
END $$;
");
        }
    }
}
