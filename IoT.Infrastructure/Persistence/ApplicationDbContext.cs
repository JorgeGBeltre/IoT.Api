using System.Linq.Expressions;
using IoT.Application.Common.Interfaces;
using IoT.Domain.Common;
using IoT.Domain.Entities;
using IoT.Domain.Repositories;
using IoT.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

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

    // Tenant & Devices
    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<DeviceGroup> DeviceGroups => Set<DeviceGroup>();
    public DbSet<Device> Devices => Set<Device>();
    public DbSet<DeviceCapability> DeviceCapabilities => Set<DeviceCapability>();
    public DbSet<DeviceCommand> DeviceCommands => Set<DeviceCommand>();
    public DbSet<PositionEntity> Positions => Set<PositionEntity>();
    public DbSet<TelemetryRecordEntity> TelemetryRecords => Set<TelemetryRecordEntity>();

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

        // Tenant Configuration
        modelBuilder.Entity<Tenant>(builder =>
        {
            builder.ToTable("tenants");
            builder.HasKey(t => t.Id);
            builder.Property(t => t.Name).HasMaxLength(150).IsRequired();
            builder.Property(t => t.Code).HasMaxLength(50).IsRequired();
            builder.HasIndex(t => t.Code).IsUnique();
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
        // Todo registro que implementa ITenantScoped se filtra estrictamente por TenantId
        // ======================================================================
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

                    var isMatch = Expression.Equal(tenantProperty, currentTenantProperty);
                    var lambda = Expression.Lambda(isMatch, parameter);

                    modelBuilder.Entity(entityType.ClrType).HasQueryFilter(lambda);
                }
            }
        }
    }
}
