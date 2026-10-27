using System.Linq.Expressions;
using IoT.Application.Common.Interfaces;
using IoT.Domain.Common;
using IoT.Domain.Entities;
using IoT.Domain.Repositories;
using IoT.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using NpgsqlTypes;

namespace IoT.Infrastructure.Persistence;

public class ApplicationDbContext : DbContext, IUnitOfWork
{
    private readonly ITenantContext? _tenantContext;

    public ApplicationDbContext(
        DbContextOptions<ApplicationDbContext> options,
        ITenantContext? tenantContext = null) : base(options)
    {
        _tenantContext = tenantContext;
    }

    public Guid CurrentTenantId => _tenantContext?.TenantId ?? Guid.Empty;

    /// <summary>
    /// Path <c>ltree</c> del subárbol visible del usuario (ADR-013). <c>null</c>/vacío = SIN
    /// restricción de subárbol (raíz, operación de sistema o token antiguo) = comportamiento NO
    /// DISRUPTIVO: el filtro de customer no recorta y sólo aplica el aislamiento por tenant.
    /// </summary>
    public string? CurrentCustomerPath => _tenantContext?.CustomerPath;

    /// <summary>
    /// Conjunto de <c>CustomerId</c> del subárbol del usuario (ADR-013), LEÍDO del
    /// <see cref="ITenantContext.CustomerSubtreeIds"/> que resolvió una vez por request el
    /// <c>TenantResolutionMiddleware</c> de forma asíncrona (E8.3b). Antes se ejecutaba aquí un
    /// <c>SqlQueryRaw</c> SÍNCRONO dentro de la evaluación del filtro global; eso podía reentrar y
    /// lanzar al materializar en streaming una entidad acotada con un reader abierto, tumbando TODAS
    /// las consultas de los usuarios acotados. Ahora el filtro sólo lee esta colección ya resuelta.
    /// Es DEFENSA EN PROFUNDIDAD del filtro EF; la línea dura es la RLS de PostgreSQL por
    /// <c>app.customer_path</c>. Devuelve vacío cuando no hay subárbol resuelto: en ese caso el
    /// filtro NO restringe por subárbol porque corta antes por <see cref="CurrentCustomerPath"/>
    /// null/vacío (raíz / sistema / token antiguo siguen viendo todo su tenant = NO DISRUPTIVO).
    /// </summary>
    public IReadOnlyCollection<Guid> CustomerSubtreeIds =>
        _tenantContext?.CustomerSubtreeIds ?? Array.Empty<Guid>();

    // Tenant & Devices
    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<DeviceGroup> DeviceGroups => Set<DeviceGroup>();
    public DbSet<Device> Devices => Set<Device>();
    public DbSet<DeviceCapability> DeviceCapabilities => Set<DeviceCapability>();
    public DbSet<DeviceCommand> DeviceCommands => Set<DeviceCommand>();
    public DbSet<PositionEntity> Positions => Set<PositionEntity>();
    public DbSet<TelemetryRecordEntity> TelemetryRecords => Set<TelemetryRecordEntity>();
    public DbSet<DeviceAttribute> DeviceAttributes => Set<DeviceAttribute>();

    // Identity, Roles & Security
    public DbSet<User> Users => Set<User>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<Permission> Permissions => Set<Permission>();
    public DbSet<RolePermission> RolePermissions => Set<RolePermission>();
    public DbSet<UserPermission> UserPermissions => Set<UserPermission>();
    public DbSet<OtpCode> OtpCodes => Set<OtpCode>();
    public DbSet<PasswordResetToken> PasswordResetTokens => Set<PasswordResetToken>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    // Tenant Operations
    public DbSet<MqttBrokerConfig> MqttBrokers => Set<MqttBrokerConfig>();
    public DbSet<TenantSettings> TenantSettings => Set<TenantSettings>();
    public DbSet<FirmwareVersionRecord> FirmwareVersionRecords => Set<FirmwareVersionRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // PostGIS extension support
        modelBuilder.HasPostgresExtension("postgis");
        // ltree: path materializado de la jerarquía Customer (ADR-013). La migración corre como
        // iot_admin, que puede crear extensiones.
        modelBuilder.HasPostgresExtension("ltree");

        // Tenant Configuration
        modelBuilder.Entity<Tenant>(builder =>
        {
            builder.ToTable("tenants");
            builder.HasKey(t => t.Id);
            builder.Property(t => t.Name).HasMaxLength(150).IsRequired();
            builder.Property(t => t.Code).HasMaxLength(50).IsRequired();
            builder.Property(t => t.BootstrapApiKeyHash).HasMaxLength(128);
            builder.Property(t => t.BootstrapApiKeyFingerprint).HasMaxLength(32);
            builder.Property(t => t.BootstrapApiKeyIssuedBy).HasMaxLength(200);
            builder.HasIndex(t => t.Code).IsUnique();
            // Índice para resolver la credencial de bootstrap por su hash en el CONNECT
            // (una lectura indexada, no un barrido). Único y FILTRADO a filas con hash: dos
            // tenants no pueden compartir credencial, pero varios pueden tenerla a null.
            builder.HasIndex(t => t.BootstrapApiKeyHash)
                .IsUnique()
                .HasFilter("\"BootstrapApiKeyHash\" IS NOT NULL");
        });

        // Customer Configuration (jerarquía multinivel bajo Tenant — ADR-013)
        modelBuilder.Entity<Customer>(builder =>
        {
            builder.ToTable("customers");
            builder.HasKey(c => c.Id);
            builder.Property(c => c.Name).HasMaxLength(150).IsRequired();
            builder.Property(c => c.Code).HasMaxLength(50);
            builder.Property(c => c.Level).IsRequired();
            builder.Property(c => c.IsActive).IsRequired();
            // Path materializado como ltree: convertido desde/hacia string en .NET.
            builder.Property(c => c.Path)
                .HasColumnType("ltree")
                .HasConversion(v => new LTree(v), v => v.ToString())
                .IsRequired();

            // Subárbol: GiST sobre Path para que el predicado "<@" (desciende de) sea indexado.
            builder.HasIndex(c => c.Path).HasMethod("gist");
            builder.HasIndex(c => new { c.TenantId, c.ParentCustomerId });
            // Code único DENTRO del tenant, sólo para filas con Code no nulo.
            builder.HasIndex(c => new { c.TenantId, c.Code })
                .IsUnique()
                .HasFilter("\"Code\" IS NOT NULL");

            // Auto-referencia (padre → hijos). Restrict: no borrar un nodo con descendientes.
            builder.HasOne<Customer>()
                .WithMany()
                .HasForeignKey(c => c.ParentCustomerId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // DeviceGroup Configuration
        modelBuilder.Entity<DeviceGroup>(builder =>
        {
            builder.ToTable("device_groups");
            builder.HasKey(g => g.Id);
            builder.Property(g => g.Name).HasMaxLength(100).IsRequired();
            builder.HasIndex(g => new { g.TenantId, g.Name }).IsUnique();
        });

        // Device Configuration
        modelBuilder.Entity<Device>(builder =>
        {
            builder.ToTable("devices");
            builder.HasKey(d => d.Id);
            builder.Property(d => d.ChipId).HasMaxLength(64).IsRequired();
            builder.Property(d => d.MacAddress).HasMaxLength(32).IsRequired();
            builder.Property(d => d.Name).HasMaxLength(100).IsRequired();
            builder.Property(d => d.Model).HasMaxLength(50).IsRequired();
            builder.Property(d => d.Manufacturer).HasMaxLength(50).IsRequired();
            builder.Property(d => d.MetadataJson).HasColumnType("jsonb");
            builder.Property(d => d.MqttApiKeyHash).HasMaxLength(128);
            builder.Property(d => d.MqttApiKeyFingerprint).HasMaxLength(32);
            builder.Property(d => d.MqttApiKeyIssuedBy).HasMaxLength(200);
            builder.Property(d => d.OtaTargetVersion).HasMaxLength(50);
            builder.Property(d => d.OtaErrorMessage).HasMaxLength(500);

            builder.HasMany(d => d.Capabilities)
                .WithOne()
                .HasForeignKey(c => c.DeviceId)
                .OnDelete(DeleteBehavior.Cascade);

            builder.HasIndex(d => new { d.TenantId, d.ChipId }).IsUnique();
            // ChipId/IMEI único GLOBAL: identidad de dispositivos sin JWT (TCP/HTTP).
            builder.HasIndex(d => d.ChipId).IsUnique();

            // Nodo dueño en la jerarquía (ADR-013). Columna NOT NULL desde E8.4a: la
            // creación/adopción asigna SIEMPRE un CustomerId (el del nodo de la sesión o el raíz del
            // tenant), así que ya no existen filas con CustomerId nulo. La propiedad CLR sigue siendo
            // Guid? (ICustomerScoped y el filtro EF por subárbol no cambian); sólo la columna es
            // obligatoria. FK con Restrict para no borrar un customer con dispositivos colgando.
            builder.Property(d => d.CustomerId).IsRequired();
            builder.HasOne<Customer>()
                .WithMany()
                .HasForeignKey(d => d.CustomerId)
                .OnDelete(DeleteBehavior.Restrict);
            builder.HasIndex(d => new { d.TenantId, d.CustomerId });
        });

        // DeviceCapability Configuration
        modelBuilder.Entity<DeviceCapability>(builder =>
        {
            builder.ToTable("device_capabilities");
            builder.HasKey(c => c.Id);
            builder.Property(c => c.ConfigurationJson).HasColumnType("jsonb");
            builder.HasIndex(c => new { c.DeviceId, c.CapabilityType }).IsUnique();
        });

        // DeviceCommand Configuration
        modelBuilder.Entity<DeviceCommand>(builder =>
        {
            builder.ToTable("device_commands");
            builder.HasKey(c => c.Id);
            builder.Property(c => c.CommandName).HasMaxLength(50).IsRequired();
            builder.Property(c => c.PayloadJson).HasColumnType("jsonb");
            builder.Property(c => c.ResultJson).HasColumnType("jsonb");
            builder.Property(c => c.ClientRequestId).HasMaxLength(100);
            builder.HasIndex(c => new { c.TenantId, c.DeviceId, c.Status });
            builder.HasIndex(c => new { c.TenantId, c.ClientRequestId });
        });

        // PositionEntity Configuration
        modelBuilder.Entity<PositionEntity>(builder =>
        {
            builder.ToTable("positions");
            builder.HasKey(p => new { p.TenantId, p.DeviceId, p.RecordedAt });
            builder.Property(p => p.Location).HasColumnType("geography(Point, 4326)");
            builder.Property(p => p.Source).HasMaxLength(20).IsRequired().HasDefaultValue("real");
            builder.Property(p => p.MessageId).HasMaxLength(128);
            builder.HasIndex(p => new { p.TenantId, p.DeviceId, p.RecordedAt });
            builder.HasIndex(p => new { p.TenantId, p.DeviceId, p.MessageId });
            builder.HasIndex(p => new { p.TenantId, p.Source, p.RecordedAt });
        });

        // TelemetryRecordEntity Configuration
        modelBuilder.Entity<TelemetryRecordEntity>(builder =>
        {
            builder.ToTable("telemetry_records");
            builder.HasKey(t => new { t.TenantId, t.DeviceId, t.RecordedAt });
            builder.Property(t => t.MetricsJson).HasColumnType("jsonb");
            builder.Property(t => t.RawPayload).HasColumnType("jsonb");
            builder.Property(t => t.Source).HasMaxLength(20).IsRequired().HasDefaultValue("real");
            builder.Property(t => t.MessageId).HasMaxLength(128);
            builder.HasIndex(t => new { t.TenantId, t.DeviceId, t.RecordedAt });
            builder.HasIndex(t => new { t.TenantId, t.DeviceId, t.MessageId });
            builder.HasIndex(t => new { t.TenantId, t.Source, t.RecordedAt });
        });

        // DeviceAttribute Configuration (modelo de atributos de ThingsBoard, P1a).
        // Dato PROPIEDAD DEL DEVICE: ITenantScoped (el filtro global por tenant se aplica por el
        // foreach de abajo), NO ICustomerScoped (su recorte por subárbol lo da la RLS vía el device,
        // como positions/telemetry). Value en text (ADR-014 D2): almacén clave-valor que acepta
        // cadenas/escalares directos y la cadena vacía (value null -> "" de la entidad); NO exige
        // JSON válido. Scope como string (legible en la BD).
        modelBuilder.Entity<DeviceAttribute>(builder =>
        {
            builder.ToTable("device_attributes");
            builder.HasKey(a => a.Id);
            builder.Property(a => a.Scope).HasConversion<string>().HasMaxLength(20).IsRequired();
            builder.Property(a => a.Key).HasMaxLength(128).IsRequired();
            builder.Property(a => a.Value).HasColumnType("text").IsRequired();

            // Un único valor por (tenant, device, scope, clave): el upsert reemplaza, no duplica.
            builder.HasIndex(a => new { a.TenantId, a.DeviceId, a.Scope, a.Key }).IsUnique();
            builder.HasIndex(a => new { a.TenantId, a.DeviceId, a.Scope });

            // FK al device dueño. Cascade: al borrar el device se borran sus atributos (coherente con
            // dato propiedad del device, igual que device_capabilities).
            builder.HasOne<Device>()
                .WithMany()
                .HasForeignKey(a => a.DeviceId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // User Configuration
        modelBuilder.Entity<User>(builder =>
        {
            builder.ToTable("users");
            builder.HasKey(u => u.Id);
            builder.Property(u => u.Username).HasMaxLength(100).IsRequired();
            builder.Property(u => u.Email).HasMaxLength(200).IsRequired();
            builder.Property(u => u.PasswordHash).HasMaxLength(256).IsRequired();
            builder.Property(u => u.FullName).HasMaxLength(200).IsRequired();
            builder.Property(u => u.RoleName).HasMaxLength(50).IsRequired();
            builder.HasIndex(u => new { u.TenantId, u.Username }).IsUnique();
            builder.HasIndex(u => new { u.TenantId, u.Email });

            builder.HasOne(u => u.Role)
                .WithMany(r => r.Users)
                .HasForeignKey(u => u.RoleId)
                .OnDelete(DeleteBehavior.SetNull);

            builder.HasMany(u => u.UserPermissions)
                .WithOne(up => up.User)
                .HasForeignKey(up => up.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            // Nodo al que pertenece el usuario en la jerarquía (ADR-013). Columna NOT NULL desde
            // E8.4a (el alta asigna SIEMPRE un CustomerId; ya no hay filas nulas). La propiedad CLR
            // sigue siendo Guid?; sólo la columna es obligatoria. FK con Restrict.
            builder.Property(u => u.CustomerId).IsRequired();
            builder.HasOne<Customer>()
                .WithMany()
                .HasForeignKey(u => u.CustomerId)
                .OnDelete(DeleteBehavior.Restrict);
            builder.HasIndex(u => new { u.TenantId, u.CustomerId });
        });

        // Role Configuration
        modelBuilder.Entity<Role>(builder =>
        {
            builder.ToTable("roles");
            builder.HasKey(r => r.Id);
            builder.Property(r => r.Name).HasMaxLength(100).IsRequired();
            builder.HasIndex(r => new { r.TenantId, r.Name });

            builder.HasMany(r => r.RolePermissions)
                .WithOne(rp => rp.Role)
                .HasForeignKey(rp => rp.RoleId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // Permission Configuration
        modelBuilder.Entity<Permission>(builder =>
        {
            builder.ToTable("permissions");
            builder.HasKey(p => p.Id);
            builder.Property(p => p.Code).HasMaxLength(100).IsRequired();
            builder.Property(p => p.Name).HasMaxLength(150).IsRequired();
            builder.Property(p => p.Module).HasMaxLength(50).IsRequired();
            builder.HasIndex(p => p.Code).IsUnique();
        });

        // RolePermission Configuration
        modelBuilder.Entity<RolePermission>(builder =>
        {
            builder.ToTable("role_permissions");
            builder.HasKey(rp => rp.Id);
            builder.HasIndex(rp => new { rp.TenantId, rp.RoleId, rp.PermissionId });
        });

        // UserPermission Configuration
        modelBuilder.Entity<UserPermission>(builder =>
        {
            builder.ToTable("user_permissions");
            builder.HasKey(up => up.Id);
            builder.HasIndex(up => new { up.TenantId, up.UserId, up.PermissionId });
        });

        // OtpCode Configuration
        modelBuilder.Entity<OtpCode>(builder =>
        {
            builder.ToTable("otp_codes");
            builder.HasKey(o => o.Id);
            builder.Property(o => o.Email).HasMaxLength(200).IsRequired();
            builder.Property(o => o.CodeHash).HasMaxLength(128).IsRequired();
            builder.HasIndex(o => new { o.TenantId, o.Email, o.Purpose });
        });

        // PasswordResetToken Configuration
        modelBuilder.Entity<PasswordResetToken>(builder =>
        {
            builder.ToTable("password_reset_tokens");
            builder.HasKey(t => t.Id);
            builder.Property(t => t.TokenHash).HasMaxLength(128).IsRequired();
            builder.HasIndex(t => new { t.TenantId, t.UserId });
        });

        // MqttBrokerConfig Configuration
        modelBuilder.Entity<MqttBrokerConfig>(builder =>
        {
            builder.ToTable("mqtt_brokers");
            builder.HasKey(b => b.Id);
            builder.Property(b => b.Name).HasMaxLength(120).IsRequired();
            builder.Property(b => b.Host).HasMaxLength(255).IsRequired();
            builder.Property(b => b.Protocol).HasMaxLength(20).IsRequired();
            builder.Property(b => b.AuthMethod).HasMaxLength(30).IsRequired();
            builder.HasIndex(b => new { b.TenantId, b.Name }).IsUnique();
            builder.HasIndex(b => b.TenantId);
        });

        // TenantSettings Configuration
        modelBuilder.Entity<TenantSettings>(builder =>
        {
            builder.ToTable("tenant_settings");
            builder.HasKey(s => s.Id);
            builder.Property(s => s.MapProvider).HasMaxLength(30).IsRequired();
            builder.Property(s => s.WebhookUrl).HasMaxLength(2048);
            builder.HasIndex(s => s.TenantId).IsUnique();
        });

        // FirmwareVersionRecord Configuration (OTA - portado de References/Mqtt-Broker)
        modelBuilder.Entity<FirmwareVersionRecord>(builder =>
        {
            builder.ToTable("firmware_version_records");
            builder.HasKey(f => f.Id);
            builder.Property(f => f.FirmwareVersion).HasMaxLength(50).IsRequired();
            builder.Property(f => f.Src).HasMaxLength(500).IsRequired();
            builder.Property(f => f.TargetModel).HasMaxLength(100);
            builder.Property(f => f.FeatureDescription).HasMaxLength(500);
            builder.Property(f => f.Sha256).HasMaxLength(64).IsRequired();
            builder.Property(f => f.UpdatedFromIp).HasMaxLength(45);
            builder.HasIndex(f => new { f.TenantId, f.FirmwareVersion }).IsUnique();
            builder.HasIndex(f => new { f.TenantId, f.TargetModel });
        });

        // AuditLog Configuration
        modelBuilder.Entity<AuditLog>(builder =>
        {
            builder.ToTable("audit_logs");
            builder.HasKey(a => a.Id);
            builder.Property(a => a.Username).HasMaxLength(100).IsRequired();
            builder.Property(a => a.Action).HasMaxLength(100).IsRequired();
            builder.HasIndex(a => new { a.TenantId, a.TimestampUtc });
        });

        // ======================================================================
        // Aislamiento Multi-Tenant Automático (Data Plane Isolation)
        // Todo registro que implementa ITenantScoped se filtra estrictamente por TenantId.
        // Además, las entidades ICustomerScoped (ADR-013) se acotan al SUBÁRBOL del usuario:
        // el filtro de tenant (ADR-003) se preserva intacto y el de customer se AÑADE con AND.
        // ======================================================================
        var containsMethod = typeof(Enumerable).GetMethods()
            .First(m => m.Name == nameof(Enumerable.Contains) && m.GetParameters().Length == 2)
            .MakeGenericMethod(typeof(Guid));

        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            if (typeof(ITenantScoped).IsAssignableFrom(entityType.ClrType))
            {
                var parameter = Expression.Parameter(entityType.ClrType, "e");
                var propertyMethodInfo = typeof(EF).GetMethod(nameof(EF.Property))?.MakeGenericMethod(typeof(Guid));
                if (propertyMethodInfo != null)
                {
                    var tenantProperty = Expression.Call(
                        null,
                        propertyMethodInfo,
                        parameter,
                        Expression.Constant(nameof(ITenantScoped.TenantId)));

                    var currentTenantProperty = Expression.Property(
                        Expression.Constant(this),
                        nameof(CurrentTenantId));

                    Expression body = Expression.Equal(tenantProperty, currentTenantProperty);

                    // Subárbol (ADR-013): sólo para ICustomerScoped. Semántica NO DISRUPTIVA:
                    //   path null/vacío  → sin restricción (raíz, sistema, token antiguo);
                    //   en otro caso     → CustomerId debe pertenecer al subárbol resuelto.
                    // La rama "CustomerId null → visible" se eliminó en E8.4a: desde
                    // AddCustomerIdNotNull la columna es NOT NULL en las únicas entidades
                    // ICustomerScoped (Device, User), así que era código muerto.
                    if (typeof(ICustomerScoped).IsAssignableFrom(entityType.ClrType))
                    {
                        var nullableGuidProperty = typeof(EF).GetMethod(nameof(EF.Property))!
                            .MakeGenericMethod(typeof(Guid?));
                        var customerIdProperty = Expression.Call(
                            null,
                            nullableGuidProperty,
                            parameter,
                            Expression.Constant(nameof(ICustomerScoped.CustomerId)));

                        var pathProperty = Expression.Property(
                            Expression.Constant(this), nameof(CurrentCustomerPath));
                        var pathIsNull = Expression.Equal(pathProperty, Expression.Constant(null, typeof(string)));
                        var pathIsEmpty = Expression.Equal(pathProperty, Expression.Constant(string.Empty, typeof(string)));

                        var subtreeIdsProperty = Expression.Property(
                            Expression.Constant(this), nameof(CustomerSubtreeIds));
                        var customerIdValue = Expression.Property(customerIdProperty, "Value");
                        var inSubtree = Expression.Call(containsMethod, subtreeIdsProperty, customerIdValue);

                        var customerMatch = Expression.OrElse(
                            Expression.OrElse(pathIsNull, pathIsEmpty),
                            inSubtree);

                        body = Expression.AndAlso(body, customerMatch);
                    }

                    var lambda = Expression.Lambda(body, parameter);
                    modelBuilder.Entity(entityType.ClrType).HasQueryFilter(lambda);
                }
            }
        }
    }
}
