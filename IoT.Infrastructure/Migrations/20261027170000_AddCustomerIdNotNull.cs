using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IoT.Infrastructure.Migrations
{
    /// <summary>
    /// Cierra la raíz común del fallo de la jerarquía por subárbol (ADR-013, fase E8.4a): hace que
    /// <c>CustomerId</c> sea OBLIGATORIO en <c>devices</c> y <c>users</c>, para que NUNCA existan
    /// filas con el nodo dueño nulo.
    ///
    /// La creación/adopción de dispositivos y el alta de usuarios ya asignan SIEMPRE un
    /// <c>CustomerId</c> (el del nodo de la sesión, o el Customer raíz del tenant). Esta migración:
    ///   1) BACKFILL RESIDUAL: asigna el Customer raíz del tenant a cualquier fila que todavía tenga
    ///      <c>CustomerId</c> NULL (ruta de actualización desde bases anteriores a E8.4a). En una base
    ///      recién migrada las tablas están vacías o ya backfilleadas por el seeder, así que afecta 0
    ///      filas; el raíz por tenant lo garantiza <c>DatabaseSeeder.SeedRootCustomersAndBackfillAsync</c>.
    ///   2) NOT NULL: <c>ALTER COLUMN "CustomerId" SET NOT NULL</c> en ambas tablas.
    ///
    /// Con esto, la migración siguiente (<c>AddSubtreeCleanPolicies</c>) puede eliminar la rama
    /// <c>OR "CustomerId" IS NULL</c> de las políticas RLS sin ocultar ninguna fila: ya no las hay.
    /// </summary>
    /// <inheritdoc />
    public partial class AddCustomerIdNotNull : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 0. Customer raíz por tenant (CRÍTICO para la ruta de ACTUALIZACIÓN). El seeder
            //    (DatabaseSeeder.EnsureRootCustomerAsync) corre DESPUÉS de MigrateAsync, así que al
            //    actualizar una base ya poblada (tenants/devices/users existentes, sin customers) la
            //    tabla customers está VACÍA cuando llega esta migración: el backfill (paso 1) dejaría
            //    CustomerId nulo y el SET NOT NULL (paso 2) fallaría (SQLSTATE 23502), impidiendo el
            //    arranque. Por eso creamos aquí el raíz de todo tenant que aún no lo tenga, IMITANDO
            //    al seeder (Code='ROOT', Level=0, Path = etiqueta 'c' + Id en hex sin guiones,
            //    ParentCustomerId NULL, IsActive, Name del tenant).
            //
            //    IDEMPOTENTE: guard NOT EXISTS por "TenantId" con "ParentCustomerId" IS NULL. Si el
            //    seeder ya creó el raíz (o una ejecución previa de esta migración), no se duplica; y el
            //    seeder, que busca el raíz por ParentCustomerId NULL, encontrará ESTE y tampoco
            //    duplicará. gen_random_uuid() se calcula UNA sola vez por fila en la subconsulta y se
            //    reutiliza en "Id" y en "Path" (coherencia Id↔Path). gen_random_uuid() viene en el
            //    core de PostgreSQL 13+.
            migrationBuilder.Sql(@"
INSERT INTO customers (""Id"", ""TenantId"", ""ParentCustomerId"", ""Name"", ""Code"", ""Path"", ""Level"", ""IsActive"", ""CreatedAt"", ""UpdatedAt"")
SELECT
    sub.new_id,
    sub.""TenantId"",
    NULL,
    sub.""Name"",
    'ROOT',
    ('c' || replace(sub.new_id::text, '-', ''))::ltree,
    0,
    true,
    now(),
    now()
FROM (
    SELECT t.""Id"" AS ""TenantId"", t.""Name"" AS ""Name"", gen_random_uuid() AS new_id
    FROM tenants t
    WHERE NOT EXISTS (
        SELECT 1 FROM customers c
        WHERE c.""TenantId"" = t.""Id"" AND c.""ParentCustomerId"" IS NULL
    )
) sub;
");

            // 1. Backfill residual: toda fila con CustomerId NULL se asigna al Customer raíz
            //    (ParentCustomerId IS NULL) de SU PROPIO tenant. Subconsulta correlacionada por
            //    "TenantId"; tras el paso 0 todo tenant tiene raíz, así que ninguna fila nula queda
            //    sin destino.
            migrationBuilder.Sql(@"
UPDATE devices
SET ""CustomerId"" = (
    SELECT c.""Id"" FROM customers c
    WHERE c.""TenantId"" = devices.""TenantId"" AND c.""ParentCustomerId"" IS NULL
    LIMIT 1
)
WHERE ""CustomerId"" IS NULL;

UPDATE users
SET ""CustomerId"" = (
    SELECT c.""Id"" FROM customers c
    WHERE c.""TenantId"" = users.""TenantId"" AND c.""ParentCustomerId"" IS NULL
    LIMIT 1
)
WHERE ""CustomerId"" IS NULL;
");

            // 2. NOT NULL en ambas columnas (SQL explícito, sin DEFAULT: la columna no lleva valor
            //    por defecto — toda inserción debe traer un CustomerId real). Si quedara alguna fila
            //    nula tras el backfill, el SET NOT NULL fallaría (fail-fast), señal de que falta el
            //    Customer raíz de algún tenant.
            migrationBuilder.Sql(@"
ALTER TABLE devices ALTER COLUMN ""CustomerId"" SET NOT NULL;
ALTER TABLE users ALTER COLUMN ""CustomerId"" SET NOT NULL;
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Volver la columna a NULLABLE (no se re-nulifican datos: las filas quedan con su
            // CustomerId asignado, lo cual es válido como nullable).
            migrationBuilder.Sql(@"
ALTER TABLE devices ALTER COLUMN ""CustomerId"" DROP NOT NULL;
ALTER TABLE users ALTER COLUMN ""CustomerId"" DROP NOT NULL;
");
        }
    }
}
