using IoT.Application.Common.Interfaces;
using IoT.Domain.Repositories;
using IoT.Infrastructure.Mqtt;
using IoT.Infrastructure.Persistence;
using IoT.Infrastructure.Redis;
using IoT.Infrastructure.Repositories;
using IoT.Protocols.Contracts;
using IoT.Protocols.Profiles;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

namespace IoT.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        // PostgreSQL + PostGIS
        var connectionString = configuration.GetConnectionString("Postgres")
            ?? configuration.GetConnectionString("MqttBrokerConnection")
            ?? throw new InvalidOperationException("Critical Configuration Error: Database connection string 'Postgres' is not configured.");

        services.AddScoped<Persistence.Interceptors.TenantSessionInterceptor>();

        services.AddDbContext<ApplicationDbContext>((sp, options) =>
        {
            var interceptor = sp.GetRequiredService<Persistence.Interceptors.TenantSessionInterceptor>();
            options.AddInterceptors(interceptor);
            options.UseNpgsql(connectionString, npgsqlOptions =>
            {
                npgsqlOptions.UseNetTopologySuite();
                npgsqlOptions.MigrationsAssembly(typeof(ApplicationDbContext).Assembly.FullName);
                npgsqlOptions.EnableRetryOnFailure(maxRetryCount: 5, maxRetryDelay: TimeSpan.FromSeconds(10), errorCodesToAdd: null);
            });
        });

        services.AddScoped<IUnitOfWork>(sp => sp.GetRequiredService<ApplicationDbContext>());
        services.AddScoped<IDeviceRepository, DeviceRepository>();
        services.AddScoped<ITenantRepository, TenantRepository>();
        services.AddScoped<IDeviceCommandRepository, DeviceCommandRepository>();
        services.AddScoped<ITelemetryStore, TelemetryStore>();
        services.AddScoped<ITelemetryRetentionService, Retention.TelemetryRetentionService>();
        services.AddScoped<DatabaseSeeder>();

        // Security & Multi-Tenant Identity Repositories
        services.AddScoped<IUserRepository, UserRepository>();
        services.AddScoped<IRoleRepository, RoleRepository>();
        services.AddScoped<IOtpRepository, OtpRepository>();
        services.AddScoped<IAuditLogRepository, AuditLogRepository>();

        // Tenant Operations Repositories
        services.AddScoped<IMqttBrokerConfigRepository, MqttBrokerConfigRepository>();
        services.AddScoped<ITenantSettingsRepository, TenantSettingsRepository>();

        // Security & Communication Services
        services.AddSingleton<IPasswordHasher, Security.BCryptPasswordHasher>();
        services.AddSingleton<IJwtTokenGenerator, Security.JwtTokenGenerator>();
        services.AddSingleton<IDeviceApiKeyService, Security.DeviceApiKeyService>();
        services.AddSingleton<IMqttConnectionInfoProvider, Mqtt.MqttConnectionInfoProvider>();
        services.AddSingleton<IEmailService, Email.MailKitEmailService>();

        // Redis Connection
        var redisConnStr = configuration["Redis:ConnectionString"]
            ?? configuration.GetConnectionString("Redis")
            ?? configuration["Redis:InMemory:ConnectionString"]
            ?? "localhost:6379";

        services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(redisConnStr));
        services.AddSingleton<IRedisStateService, RedisStateService>();
        services.AddSingleton<IStateCacheService>(sp => sp.GetRequiredService<IRedisStateService>());

        // Broker MQTT EMBEBIDO (D1: broker propio de References/Mqtt-Broker, no externo).
        // El backend ES el broker: autentica, intercepta publishes → pipeline de ingesta,
        // y hace push server→device. Implementa IMqttPublisher.
        services.AddSingleton<IMqttClientMap, RedisMqttClientMap>();
        services.AddSingleton<EmbeddedMqttBroker>();
        services.AddSingleton<IMqttPublisher>(sp => sp.GetRequiredService<EmbeddedMqttBroker>());

        // Cliente MQTT saliente legacy (IDeviceTransport). Conservado para rutas de
        // transporte no-broker; no se usa ya como IMqttPublisher.
        services.AddSingleton<MqttDeviceTransport>();
        services.AddSingleton<IDeviceTransport>(sp => sp.GetRequiredService<MqttDeviceTransport>());

        // Declarative Device Profiles Registry & Topic Routing (E2-02, E3-02)
        services.AddSingleton<IProtocolRegistry>(_ => ProtocolRegistry.CreateDefault());
        services.AddSingleton<IoT.Protocols.Routing.ITopicRoutingService, IoT.Protocols.Routing.TopicRoutingService>();

        // HTTP Device Transport (E3-04). Timeout acotado: un endpoint de dispositivo lento
        // no debe bloquear el hilo de downlink indefinidamente.
        services.AddHttpClient("IoT.HttpDeviceTransport", c => c.Timeout = TimeSpan.FromSeconds(15));
        services.AddSingleton<Http.HttpDeviceTransport>();

        // Gateway TCP multi-protocolo (E5, ADR-007): un handler por familia + un servicio
        // con un listener por protocolo (puerto por protocolo). SOLO singletons aquí; el
        // HostedService se registra en AddDeviceFacingGateways (un solo proceso).
        services.AddSingleton<IoT.Protocols.Tcp.ITcpProtocolHandler, IoT.Protocols.Tcp.Gt06TcpHandler>();
        services.AddSingleton<IoT.Protocols.Tcp.ITcpProtocolHandler, IoT.Protocols.Tcp.QueclinkTcpHandler>();
        services.AddSingleton<IoT.Protocols.Tcp.ITcpProtocolHandler, IoT.Protocols.Tcp.CobanTcpHandler>();
        services.AddSingleton<IoT.Protocols.Tcp.ITcpProtocolHandler, IoT.Protocols.Tcp.IStartekTcpHandler>();
        services.AddSingleton<IoT.Protocols.Tcp.ITcpProtocolHandler, IoT.Protocols.Tcp.JointechTcpHandler>();
        services.AddSingleton<Tcp.MultiProtocolTcpGatewayService>();
        services.AddSingleton<ITcpCommandGateway>(sp => sp.GetRequiredService<Tcp.MultiProtocolTcpGatewayService>());

        return services;
    }

    /// <summary>
    /// Registra los gateways que escuchan a los dispositivos (broker MQTT embebido +
    /// gateway TCP GPS) como HostedServices. Debe invocarse en UN SOLO proceso
    /// (el API) para evitar doble-bind de puertos 1883/5023.
    /// </summary>
    public static IServiceCollection AddDeviceFacingGateways(this IServiceCollection services)
    {
        services.AddHostedService(sp => sp.GetRequiredService<EmbeddedMqttBroker>());
        services.AddHostedService(sp => sp.GetRequiredService<Tcp.MultiProtocolTcpGatewayService>());
        return services;
    }
}
