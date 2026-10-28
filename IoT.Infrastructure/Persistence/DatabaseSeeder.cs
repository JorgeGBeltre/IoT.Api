using System.Security.Cryptography;
using IoT.Application.Common.Interfaces;
using IoT.Domain.Common;
using IoT.Domain.Entities;
using IoT.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IoT.Infrastructure.Persistence;

public sealed class DatabaseSeeder
{
    private readonly ApplicationDbContext _context;
    private readonly IPasswordHasher _passwordHasher;
    private readonly IDeviceApiKeyService _apiKeyService;
    private readonly IConfiguration _configuration;
    private readonly IHostEnvironment _environment;
    private readonly ILogger<DatabaseSeeder> _logger;

    public DatabaseSeeder(
        ApplicationDbContext context,
        IPasswordHasher passwordHasher,
        IDeviceApiKeyService apiKeyService,
        IConfiguration configuration,
        IHostEnvironment environment,
        ILogger<DatabaseSeeder> logger)
    {
        _context = context;
        _passwordHasher = passwordHasher;
        _apiKeyService = apiKeyService;
        _configuration = configuration;
        _environment = environment;
        _logger = logger;
    }

    public async Task SeedAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            // Aplica las migraciones de EF Core (crea el esquema si la base es nueva y
            // lo mantiene versionado). Reemplaza EnsureCreated, que no deja historial de
            // migraciones y no es apto para producción.
            _logger.LogInformation("Aplicando migraciones de base de datos PostgreSQL...");
            await _context.Database.MigrateAsync(cancellationToken);

            // Datos de DEMOSTRACIÓN (tenants, usuarios y dispositivos de ejemplo) SÓLO en
            // Development. En Producción el arranque únicamente migra el esquema y aplica las
            // credenciales de bootstrap configuradas: no se siembran empresas, usuarios ni
            // equipos demo, para que una base ya limpia siga limpia tras reiniciar el servicio.
            bool isDevelopment = _environment.IsDevelopment();

            var tenant1Id = Guid.Parse("11111111-1111-1111-1111-111111111111");

            // 1. Seed Tenants (SÓLO Development)
            if (isDevelopment && !await _context.Tenants.IgnoreQueryFilters().AnyAsync(cancellationToken))
            {
                _logger.LogInformation("Sembrando tenant de prueba...");
                var tenants = new List<Tenant>
                {
                    Tenant.CreateWithId(tenant1Id, "Logística & Transporte Caribe", "CARIBE-LOG").Value
                };
                await _context.Tenants.AddRangeAsync(tenants, cancellationToken);
                await _context.SaveChangesAsync(cancellationToken);
            }

            // 2. Seed Users (SÓLO Development)
            if (isDevelopment && !await _context.Users.IgnoreQueryFilters().AnyAsync(cancellationToken))
            {
                _logger.LogInformation("Sembrando usuario administrador inicial...");

                (string password, bool mustChange) = ResolveSeedAdminPassword(isDevelopment);
                // Hash POR admin (BCrypt usa sal por hash): aunque compartan contraseña inicial,
                // los hashes difieren y el cambio obligatorio en prod (mustChange) aísla cada cuenta.

                var adminTenant1 = User.Create(
                    tenant1Id,
                    "admin",
                    "admin@iot.caribe",
                    _passwordHasher.Hash(password),
                    "Administrador Caribe",
                    "Admin",
                    mustChangePassword: mustChange).Value;

                // ADR-013 E8.4a: asignar el Customer raíz AL CREAR (no después), para no violar el
                // NOT NULL de CustomerId al guardar.
                adminTenant1.AssignToCustomer((await EnsureRootCustomerAsync(tenant1Id, cancellationToken)).Id);

                await _context.Users.AddAsync(adminTenant1, cancellationToken);
                await _context.SaveChangesAsync(cancellationToken);
            }

            // 3. Seed Devices (SÓLO Development)
            if (isDevelopment && !await _context.Devices.IgnoreQueryFilters().AnyAsync(cancellationToken))
            {
                _logger.LogInformation("Sembrando dispositivos iniciales de flota y sensores...");
                var dev1 = Device.Create(
                    tenant1Id,
                    "GPS-DEMO01",
                    "24:6F:28:AB:10:01",
                    "Camión Distribuidor 01 (SD Este)",
                    "MQTT-GPS-Tracker",
                    "Generic").Value;

                var dev2 = Device.Create(
                    tenant1Id,
                    "GPS-DEMO02",
                    "24:6F:28:AB:10:02",
                    "Furgón Logístico 02 (Distrito Nacional)",
                    "MQTT-GPS-Tracker",
                    "Generic").Value;

                // ADR-013 E8.4a: asignar el Customer raíz del tenant AL CREAR cada device (NOT NULL).
                var rootT1 = (await EnsureRootCustomerAsync(tenant1Id, cancellationToken)).Id;
                dev1.AssignToCustomer(rootT1);
                dev2.AssignToCustomer(rootT1);

                await _context.Devices.AddRangeAsync(new[] { dev1, dev2 }, cancellationToken);
                await _context.SaveChangesAsync(cancellationToken);
            }

            // 3b. Alta de ORGANIZACIÓN + ADMINISTRADOR para producción, dirigida por
            //     configuración (Bootstrap:Tenants). A diferencia de los bloques demo de arriba,
            //     corre en cualquier entorno pero SÓLO si hay tenants configurados; es idempotente
            //     (por Code y por usuario) y va ANTES de las credenciales de bootstrap MQTT para
            //     que el tenant ya exista cuando se le fija la ApiKey por su mismo Code.
            await SeedConfiguredTenantsAndAdminsAsync(isDevelopment, cancellationToken);

            // 3c. Customer raíz por tenant + backfill de la jerarquía (ADR-013, fase E8.1).
            //     Corre DESPUÉS de dar de alta tenants/admins y en el mismo scope de sistema
            //     (IgnoreQueryFilters), para que toda base existente quede con un nodo raíz por
            //     tenant y sin devices/users con CustomerId nulo. No cambia authz/RLS.
            await SeedRootCustomersAndBackfillAsync(cancellationToken);

            // 3d. Sembrado de permisos estándar, roles de tenant y vinculación de usuarios (ADR-013, Decisión D3).
            //     Idempotente: crea los permisos del catálogo StandardPermissions que no existan,
            //     asegura los roles básicos (Admin, Operator, Viewer) por tenant con sus permisos por defecto,
            //     y vincula RoleId en los usuarios que aún tengan RoleId nulo.
            await SeedPermissionsAndRolesAsync(cancellationToken);

            // 4. Credenciales de bootstrap por tenant (reemplazan la antigua clave global
            //    única para todos los tenants — MEDIUM-2). Idempotente y FUERA del bloque de
            //    siembra de tenants: la migración añade las columnas a una base ya existente,
            //    donde los tenants ya están creados, así que hay que poder fijar/rotar la
            //    credencial en cada arranque sin depender de que sea la primera siembra.
            await SeedTenantBootstrapCredentialsAsync(cancellationToken);

            _logger.LogInformation("Inicialización y sembrado de base de datos completado.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error durante la inicialización de la base de datos.");
            throw;
        }
    }

    /// <summary>
    /// Fija (o rota) la credencial de bootstrap MQTT de cada tenant a partir de la
    /// configuración <c>MqttBroker:BootstrapCredentials</c> (lista de {TenantCode, ApiKey}).
    /// Se guarda sólo el hash; el texto plano vive en la configuración porque es la única
    /// forma de que un publicador multi-equipo (el simulador, herramientas del operador) la
    /// presente. Cada clave está ACOTADA a su tenant: a diferencia de la antigua clave global,
    /// filtrar una no permite inyectar en otro tenant. Es idempotente: si el hash ya coincide
    /// no reescribe; si cambia la clave en configuración, la rota en el siguiente arranque.
    /// Sin credenciales configuradas no hace nada (no hay publicadores multi-equipo).
    /// </summary>
    private async Task SeedTenantBootstrapCredentialsAsync(CancellationToken cancellationToken)
    {
        var configured = _configuration
            .GetSection("MqttBroker:BootstrapCredentials")
            .Get<List<BootstrapCredentialConfig>>() ?? new List<BootstrapCredentialConfig>();

        if (configured.Count == 0)
            return;

        foreach (var cred in configured)
        {
            // Una ApiKey vacía = "no configurada para este despliegue" (p.ej. una organización
            // opcional que no se dio de alta): se omite en silencio, no es un error de config.
            if (string.IsNullOrWhiteSpace(cred.ApiKey))
                continue;

            if (string.IsNullOrWhiteSpace(cred.TenantCode))
            {
                _logger.LogWarning(
                    "Credencial de bootstrap mal configurada (falta TenantCode); se omite.");
                continue;
            }

            string code = cred.TenantCode.Trim().ToUpperInvariant();
            var tenant = await _context.Tenants.IgnoreQueryFilters()
                .FirstOrDefaultAsync(t => t.Code == code, cancellationToken);
            if (tenant is null)
            {
                _logger.LogWarning(
                    "Credencial de bootstrap para el tenant '{Code}', que no existe; se omite.", code);
                continue;
            }

            string hash = _apiKeyService.ComputeHash(cred.ApiKey);
            if (string.Equals(tenant.BootstrapApiKeyHash, hash, StringComparison.Ordinal))
                continue; // ya está puesta: idempotente

            string fingerprint = hash[..8];
            tenant.SetBootstrapApiKey(hash, fingerprint, "DatabaseSeeder");
            _context.Tenants.Update(tenant);
            await _context.SaveChangesAsync(cancellationToken);
            _logger.LogInformation(
                "Credencial de bootstrap sembrada/rotada para el tenant {Code} (huella {Fingerprint}).",
                tenant.Code, fingerprint);
        }
    }

    /// <summary>
    /// Alta de ORGANIZACIÓN + ADMINISTRADOR para PRODUCCIÓN dirigida por configuración
    /// (<c>Bootstrap:Tenants</c>: lista de {Code, Name, AdminEmail, AdminUsername}). A diferencia
    /// del sembrado demo (sólo Development), permite que una base de datos LIMPIA de producción
    /// tenga una organización y un administrador con los que iniciar sesión, sin datos de ejemplo.
    ///
    /// Es IDEMPOTENTE: busca el tenant por Code y el admin por usuario/email dentro de ese tenant,
    /// y NO recrea ni reescribe lo que ya exista (no resetea la contraseña en cada reinicio). La
    /// contraseña inicial sale de <c>Seed:AdminPassword</c> (obligatoria y fuerte en producción; el
    /// admin la cambia en el primer inicio de sesión). Sin <c>Bootstrap:Tenants</c> no hace nada
    /// (no-op), así que Development y los despliegues que no lo usan quedan intactos. Corre ANTES de
    /// <see cref="SeedTenantBootstrapCredentialsAsync"/> para que el tenant ya exista cuando se le
    /// fija la credencial de bootstrap MQTT por su mismo Code.
    /// </summary>
    private async Task SeedConfiguredTenantsAndAdminsAsync(bool isDevelopment, CancellationToken cancellationToken)
    {
        var configured = _configuration
            .GetSection("Bootstrap:Tenants")
            .Get<List<BootstrapTenantConfig>>() ?? new List<BootstrapTenantConfig>();

        if (configured.Count == 0)
            return;

        foreach (var cfg in configured)
        {
            if (string.IsNullOrWhiteSpace(cfg.Code) || string.IsNullOrWhiteSpace(cfg.Name) ||
                string.IsNullOrWhiteSpace(cfg.AdminEmail) || string.IsNullOrWhiteSpace(cfg.AdminUsername))
            {
                throw new InvalidOperationException(
                    "Bootstrap:Tenants mal configurado: cada entrada requiere Code, Name, AdminEmail y AdminUsername.");
            }

            string code = cfg.Code.Trim().ToUpperInvariant();

            // Tenant: crear si no existe (idempotente por Code). IgnoreQueryFilters porque este
            // contexto admin siembra cross-tenant (omite RLS por diseño).
            var tenant = await _context.Tenants.IgnoreQueryFilters()
                .FirstOrDefaultAsync(t => t.Code == code, cancellationToken);
            if (tenant is null)
            {
                var tenantResult = Tenant.Create(cfg.Name.Trim(), code);
                if (tenantResult.IsFailure)
                    throw new InvalidOperationException(
                        $"No se pudo crear la organización de bootstrap '{code}': {tenantResult.Error.Message}");
                tenant = tenantResult.Value;
                await _context.Tenants.AddAsync(tenant, cancellationToken);
                await _context.SaveChangesAsync(cancellationToken);
                _logger.LogInformation("Organización de bootstrap creada: {Code}.", code);
            }

            // Administrador del tenant: crear si no existe (idempotente por usuario/email DENTRO del
            // tenant). Si ya existe no se toca, para no resetear su contraseña en cada reinicio.
            string username = cfg.AdminUsername.Trim();
            string email = cfg.AdminEmail.Trim().ToLowerInvariant();
            bool adminExists = await _context.Users.IgnoreQueryFilters()
                .AnyAsync(u => u.TenantId == tenant.Id && (u.Username == username || u.Email == email), cancellationToken);
            if (!adminExists)
            {
                // La contraseña sólo se resuelve cuando hay un admin que CREAR: así un reinicio en
                // el que la organización y su admin ya existen no depende de Seed:AdminPassword (su
                // fail-fast de producción sólo aplica al dar de alta un administrador nuevo).
                (string password, bool mustChange) = ResolveSeedAdminPassword(isDevelopment);

                var userResult = User.Create(
                    tenant.Id,
                    username,
                    email,
                    _passwordHasher.Hash(password),
                    $"Administrador {cfg.Name.Trim()}",
                    "Admin",
                    mustChangePassword: mustChange);
                if (userResult.IsFailure)
                    throw new InvalidOperationException(
                        $"No se pudo crear el administrador de bootstrap de '{code}': {userResult.Error.Message}");

                // ADR-013 E8.4a: el admin de bootstrap se asigna al Customer raíz del tenant AL
                // CREAR, para no violar el NOT NULL de CustomerId (esta ruta corre en producción
                // sobre una base limpia, donde el backfill de 3c vendría después de este SaveChanges).
                var bootstrapRoot = await EnsureRootCustomerAsync(tenant.Id, cancellationToken);
                userResult.Value.AssignToCustomer(bootstrapRoot.Id);

                await _context.Users.AddAsync(userResult.Value, cancellationToken);
                await _context.SaveChangesAsync(cancellationToken);
                _logger.LogInformation(
                    "Administrador inicial creado para {Code} (usuario {Username}); cambio de contraseña obligatorio: {MustChange}.",
                    code, username, mustChange);
            }
        }
    }

    /// <summary>
    /// Fase E8.1 de la jerarquía multinivel (ADR-013): garantiza que CADA tenant tenga un
    /// <see cref="Customer"/> RAÍZ (<c>ParentCustomerId == null</c>) y hace BACKFILL de los
    /// <see cref="Device"/> y <see cref="User"/> del tenant cuyo <c>CustomerId</c> siga nulo,
    /// asignándolos a ese raíz. Es NO DISRUPTIVA: no toca authz ni RLS; sólo rellena nulos.
    ///
    /// IDEMPOTENTE: si el raíz ya existe no se recrea; sólo se rellenan los <c>CustomerId</c>
    /// nulos (los ya asignados no se reasignan). Cada tenant recibe SU PROPIO raíz; no se cruzan
    /// datos entre tenants. Corre en scope de sistema (<c>IgnoreQueryFilters</c>) como el resto
    /// del sembrado.
    /// </summary>
    private async Task SeedRootCustomersAndBackfillAsync(CancellationToken cancellationToken)
    {
        var tenants = await _context.Tenants.IgnoreQueryFilters()
            .ToListAsync(cancellationToken);

        foreach (var tenant in tenants)
        {
            var root = await EnsureRootCustomerAsync(tenant.Id, cancellationToken);

            // Backfill residual (ruta de actualización desde bases anteriores a E8.4a): sólo filas
            // con CustomerId nulo (no reasigna las ya asignadas). Desde E8.4a el alta asigna siempre
            // un CustomerId, así que en operación normal aquí no queda nada por rellenar; la columna
            // NOT NULL (migración AddCustomerIdNotNull) es la garantía dura.
            var devices = await _context.Devices.IgnoreQueryFilters()
                .Where(d => d.TenantId == tenant.Id && d.CustomerId == null)
                .ToListAsync(cancellationToken);
            foreach (var device in devices)
                device.AssignToCustomer(root.Id);

            var users = await _context.Users.IgnoreQueryFilters()
                .Where(u => u.TenantId == tenant.Id && u.CustomerId == null)
                .ToListAsync(cancellationToken);
            foreach (var user in users)
                user.AssignToCustomer(root.Id);

            if (devices.Count > 0 || users.Count > 0)
            {
                await _context.SaveChangesAsync(cancellationToken);
                _logger.LogInformation(
                    "Backfill de jerarquía para el tenant {Code}: {Devices} dispositivos y {Users} usuarios asignados al raíz.",
                    tenant.Code, devices.Count, users.Count);
            }
        }
    }

    /// <summary>
    /// Get-or-create IDEMPOTENTE del <see cref="Customer"/> RAÍZ de un tenant (ADR-013 E8.4a). Se
    /// usa tanto en el backfill como en los bloques de siembra/alta que crean devices/users, para
    /// asignarles el raíz EN EL MOMENTO de crearlos y así no violar nunca el NOT NULL de
    /// <c>CustomerId</c>. Corre en el contexto admin del seeder (omite RLS).
    /// </summary>
    private async Task<Customer> EnsureRootCustomerAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var root = await _context.Customers.IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.TenantId == tenantId && c.ParentCustomerId == null, cancellationToken);
        if (root is not null)
            return root;

        var tenant = await _context.Tenants.IgnoreQueryFilters()
            .FirstOrDefaultAsync(t => t.Id == tenantId, cancellationToken)
            ?? throw new InvalidOperationException(
                $"No existe el tenant {tenantId} para crear su customer raíz.");

        var rootResult = Customer.Create(tenantId, tenant.Name, parent: null, code: "ROOT");
        if (rootResult.IsFailure)
            throw new InvalidOperationException(
                $"No se pudo crear el customer raíz del tenant '{tenant.Code}': {rootResult.Error.Message}");

        root = rootResult.Value;
        await _context.Customers.AddAsync(root, cancellationToken);
        await _context.SaveChangesAsync(cancellationToken);
        _logger.LogInformation("Customer raíz creado para el tenant {Code}.", tenant.Code);
        return root;
    }

    /// <summary>
    /// Sembrado de catálogo de permisos estándar y roles por tenant (ADR-013, Decisión D3).
    /// Asegura que el catálogo de permisos estándar exista en la tabla permissions,
    /// que cada tenant tenga sus roles estándar (Admin, Operator, Viewer) con sus RolePermissions asociados,
    /// y que los usuarios existentes sin RoleId queden vinculados a su Role correspondiente.
    /// </summary>
    private async Task SeedPermissionsAndRolesAsync(CancellationToken cancellationToken)
    {
        // 1. Catálogo global de permisos estándar
        var existingPerms = await _context.Permissions.IgnoreQueryFilters()
            .ToListAsync(cancellationToken);
        var permsByCode = existingPerms.ToDictionary(p => p.Code, StringComparer.OrdinalIgnoreCase);

        var newPerms = new List<Permission>();
        foreach (var (code, name, module, description) in StandardPermissions.All)
        {
            if (!permsByCode.TryGetValue(code, out var permEntity))
            {
                var permResult = Permission.Create(code, name, module, description);
                if (permResult.IsSuccess)
                {
                    newPerms.Add(permResult.Value);
                    permsByCode[code] = permResult.Value;
                }
            }
        }

        if (newPerms.Count > 0)
        {
            await _context.Permissions.AddRangeAsync(newPerms, cancellationToken);
            await _context.SaveChangesAsync(cancellationToken);
            _logger.LogInformation("Se sembraron {Count} nuevos permisos estándar en el catálogo.", newPerms.Count);
        }

        // 2. Roles estándar y permisos por cada Tenant
        var tenants = await _context.Tenants.IgnoreQueryFilters()
            .ToListAsync(cancellationToken);

        foreach (var tenant in tenants)
        {
            var existingRoles = await _context.Roles.IgnoreQueryFilters()
                .Where(r => r.TenantId == tenant.Id)
                .ToListAsync(cancellationToken);
            var rolesByName = existingRoles.ToDictionary(r => r.Name, StringComparer.OrdinalIgnoreCase);

            var standardRoles = new (string Name, string Description)[]
            {
                ("Admin", "Administrador de la organización con permisos de gestión completos"),
                ("Operator", "Operador de flota con permisos de monitoreo, telemetría y comandos"),
                ("Viewer", "Visualizador con permisos de solo lectura para mapa y reportes")
            };

            var rolesCreated = false;
            foreach (var (rName, rDesc) in standardRoles)
            {
                if (!rolesByName.TryGetValue(rName, out var roleEntity))
                {
                    var roleResult = Role.Create(tenant.Id, rName, rDesc, isSystemRole: false);
                    if (roleResult.IsSuccess)
                    {
                        await _context.Roles.AddAsync(roleResult.Value, cancellationToken);
                        rolesByName[rName] = roleResult.Value;
                        rolesCreated = true;
                    }
                }
            }

            if (rolesCreated)
            {
                await _context.SaveChangesAsync(cancellationToken);
            }

            // RolePermissions para cada rol del tenant
            var existingRolePerms = await _context.RolePermissions.IgnoreQueryFilters()
                .Where(rp => rp.TenantId == tenant.Id)
                .ToListAsync(cancellationToken);
            var existingRolePermSet = new HashSet<(Guid RoleId, Guid PermId)>(
                existingRolePerms.Select(rp => (rp.RoleId, rp.PermissionId)));

            var newRolePerms = new List<RolePermission>();
            foreach (var (rName, roleEntity) in rolesByName)
            {
                var defaultPermCodes = StandardPermissions.GetDefaultPermissionsForRole(rName);
                foreach (var code in defaultPermCodes)
                {
                    if (permsByCode.TryGetValue(code, out var permEntity))
                    {
                        if (!existingRolePermSet.Contains((roleEntity.Id, permEntity.Id)))
                        {
                            newRolePerms.Add(new RolePermission(Guid.NewGuid(), tenant.Id, roleEntity.Id, permEntity.Id));
                            existingRolePermSet.Add((roleEntity.Id, permEntity.Id));
                        }
                    }
                }
            }

            if (newRolePerms.Count > 0)
            {
                await _context.RolePermissions.AddRangeAsync(newRolePerms, cancellationToken);
                await _context.SaveChangesAsync(cancellationToken);
            }

            // Vincular RoleId en usuarios sin rol asignado
            var usersWithoutRole = await _context.Users.IgnoreQueryFilters()
                .Where(u => u.TenantId == tenant.Id && u.RoleId == null)
                .ToListAsync(cancellationToken);

            var usersUpdated = false;
            foreach (var user in usersWithoutRole)
            {
                if (rolesByName.TryGetValue(user.RoleName, out var roleEntity))
                {
                    user.AssignRole(roleEntity);
                    usersUpdated = true;
                }
                else if (rolesByName.TryGetValue("Operator", out var fallbackRole))
                {
                    user.AssignRole(fallbackRole);
                    usersUpdated = true;
                }
            }

            if (usersUpdated)
            {
                await _context.SaveChangesAsync(cancellationToken);
                _logger.LogInformation("Se vincularon roles para {Count} usuarios en el tenant {Code}.", usersWithoutRole.Count, tenant.Code);
            }
        }
    }

    /// <summary>Forma de cada entrada de <c>Bootstrap:Tenants</c> (alta de organización + admin de producción).</summary>
    private sealed class BootstrapTenantConfig
    {
        public string? Code { get; set; }
        public string? Name { get; set; }
        public string? AdminEmail { get; set; }
        public string? AdminUsername { get; set; }
    }

    /// <summary>Forma de cada entrada de <c>MqttBroker:BootstrapCredentials</c>.</summary>
    private sealed class BootstrapCredentialConfig
    {
        public string? TenantCode { get; set; }
        public string? ApiKey { get; set; }
    }

    /// <summary>
    /// Resuelve la contraseña inicial de los administradores sembrados.
    /// - Si "Seed:AdminPassword" (env Seed__AdminPassword) está configurada, se usa (validando complejidad).
    /// - En Development sin configuración: valor por defecto conocido para el login local.
    /// - En Producción sin configuración: FAIL-FAST (no se autogenera ni se escribe NINGUNA
    ///   credencial al log). El operador DEBE definir Seed:AdminPassword. Fuera de Development
    ///   se exige cambiarla en el primer inicio de sesión (mustChange).
    /// </summary>
    private (string Password, bool MustChange) ResolveSeedAdminPassword(bool isDevelopment)
    {
        string? configured = _configuration["Seed:AdminPassword"];

        if (!string.IsNullOrWhiteSpace(configured))
        {
            // Valida complejidad para fallar temprano si el operador define una débil.
            _passwordHasher.ValidateComplexity(configured);
            _logger.LogInformation("Contraseña de administrador inicial tomada de la configuración (Seed:AdminPassword).");
            return (configured, !isDevelopment);
        }

        if (isDevelopment)
        {
            _logger.LogWarning("Seed:AdminPassword no configurada; usando contraseña de desarrollo por defecto. NO usar en producción.");
            return ("Admin123$!", false);
        }

        // Producción sin contraseña: no autogenerar ni loguear secretos. Exigir configuración explícita.
        throw new InvalidOperationException(
            "Critical Security Failure: 'Seed:AdminPassword' (env Seed__AdminPassword) es obligatoria en producción. " +
            "Defina una contraseña inicial fuerte para los administradores sembrados.");
    }

}
