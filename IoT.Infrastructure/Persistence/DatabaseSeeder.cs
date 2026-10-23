using System.Security.Cryptography;
using IoT.Application.Common.Interfaces;
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
    private readonly IConfiguration _configuration;
    private readonly IHostEnvironment _environment;
    private readonly ILogger<DatabaseSeeder> _logger;

    public DatabaseSeeder(
        ApplicationDbContext context,
        IPasswordHasher passwordHasher,
        IConfiguration configuration,
        IHostEnvironment environment,
        ILogger<DatabaseSeeder> logger)
    {
        _context = context;
        _passwordHasher = passwordHasher;
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

            var tenant1Id = Guid.Parse("11111111-1111-1111-1111-111111111111");
            var tenant2Id = Guid.Parse("22222222-2222-2222-2222-222222222222");
            var tenant3Id = Guid.Parse("33333333-3333-3333-3333-333333333333");

            // 1. Seed Tenants
            if (!await _context.Tenants.IgnoreQueryFilters().AnyAsync(cancellationToken))
            {
                _logger.LogInformation("Sembrando tenants de demostración...");
                var tenants = new List<Tenant>
                {
                    Tenant.CreateWithId(tenant1Id, "Logística & Transporte Caribe", "CARIBE-LOG").Value,
                    Tenant.CreateWithId(tenant2Id, "Gas & Energía Dominicana GLP", "DOM-GLP").Value,
                    Tenant.CreateWithId(tenant3Id, "Smart Infrastructure & Actuators", "SMART-ACT").Value
                };
                await _context.Tenants.AddRangeAsync(tenants, cancellationToken);
                await _context.SaveChangesAsync(cancellationToken);
            }

            // 2. Seed Users
            if (!await _context.Users.IgnoreQueryFilters().AnyAsync(cancellationToken))
            {
                _logger.LogInformation("Sembrando usuarios administradores iniciales...");

                bool isDevelopment = _environment.IsDevelopment();
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

                var adminTenant2 = User.Create(
                    tenant2Id,
                    "admin_glp",
                    "admin@glp.do",
                    _passwordHasher.Hash(password),
                    "Administrador GLP",
                    "Admin",
                    mustChangePassword: mustChange).Value;

                var adminTenant3 = User.Create(
                    tenant3Id,
                    "admin_smart",
                    "admin@smart.do",
                    _passwordHasher.Hash(password),
                    "Administrador Actuadores",
                    "Admin",
                    mustChangePassword: mustChange).Value;

                await _context.Users.AddRangeAsync(new[] { adminTenant1, adminTenant2, adminTenant3 }, cancellationToken);
                await _context.SaveChangesAsync(cancellationToken);
            }

            // 3. Seed Devices
            if (!await _context.Devices.IgnoreQueryFilters().AnyAsync(cancellationToken))
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

                var dev3 = Device.Create(
                    tenant2Id,
                    "GLP-TANK01",
                    "7C:DF:A1:90:22:01",
                    "Tanque Estacionario 5000 Gal",
                    "MQTT-PressureLevelSensor",
                    "Custom IoT").Value;

                var dev4 = Device.Create(
                    tenant3Id,
                    "RELAY-CTRL01",
                    "80:65:99:33:44:01",
                    "Controlador Bombas de Agua",
                    "MQTT-Relay-BLE",
                    "Custom IoT").Value;

                await _context.Devices.AddRangeAsync(new[] { dev1, dev2, dev3, dev4 }, cancellationToken);
                await _context.SaveChangesAsync(cancellationToken);
            }

            _logger.LogInformation("Inicialización y sembrado de base de datos completado.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error durante la inicialización de la base de datos.");
            throw;
        }
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
