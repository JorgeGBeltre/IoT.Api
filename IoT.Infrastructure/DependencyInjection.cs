using IoT.Application.Common.Interfaces;
using IoT.Domain.Repositories;
using IoT.Infrastructure.Mqtt;
using IoT.Infrastructure.Persistence;
using IoT.Infrastructure.Redis;
using IoT.Infrastructure.Repositories;
using IoT.Infrastructure.TelemetryQueue;
using IoT.Protocols.Contracts;
using IoT.Protocols.Profiles;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
        services.AddScoped<IDeviceAttributeRepository, DeviceAttributeRepository>();
        services.AddScoped<ITelemetryStore, TelemetryStore>();
        services.AddScoped<ITelemetryRetentionService, Retention.TelemetryRetentionService>();
        services.AddScoped<DatabaseSeeder>();

        // Security & Multi-Tenant Identity Repositories
        services.AddScoped<IUserRepository, UserRepository>();
        services.AddScoped<ICustomerRepository, CustomerRepository>();
        services.AddScoped<IRoleRepository, RoleRepository>();
        services.AddScoped<IPermissionService, Services.PermissionService>();
        services.AddScoped<IOtpRepository, OtpRepository>();
        services.AddScoped<IAuditLogRepository, AuditLogRepository>();

        // Tenant Operations Repositories
        services.AddScoped<IEventQueryService, Services.EventQueryService>();
        services.AddScoped<IOutboundWebhookDispatcher, Services.OutboundWebhookDispatcher>();
        services.AddScoped<IMcpFleetService, Services.McpFleetService>();
        services.AddScoped<IMqttBrokerConfigRepository, MqttBrokerConfigRepository>();
        services.AddScoped<ITenantSettingsRepository, TenantSettingsRepository>();
        services.AddScoped<IProtocolConverterScriptRepository, ProtocolConverterScriptRepository>();
        services.AddScoped<IFirmwareVersionRecordRepository, FirmwareVersionRecordRepository>();
        services.AddScoped<IFirmwareOtaService, Services.FirmwareOtaService>();
        services.AddScoped<IDevicePresenceService, Services.DevicePresenceService>();
        services.AddScoped<ISharedAttributePushDispatcher, Services.SharedAttributePushDispatcher>();
        services.AddScoped<ILanZeroconfConnectorService, Services.LanZeroconfConnectorService>();
        // Limitador de transmisiones OTA simultaneas. Singleton a proposito: el limite es del
        // PROCESO, no de la peticion que origino el rollout.
        services.AddSingleton<Services.OtaTransmissionThrottle>();
        // Registro de campanas OTA vivas: como mucho UNA bomba por dispositivo. Singleton por
        // la misma razon que el limitador; el servicio OTA es scoped y no puede guardar este
        // estado de proceso.
        services.AddSingleton<Services.OtaCampaignRegistry>();
        // Cortafuegos de auditoría de equipos no aprovisionados: una fila por identificador y
        // por ventana, no una por trama. Singleton (estado de proceso), como el resto.
        services.AddSingleton<IUnprovisionedDeviceAuditThrottle, Services.UnprovisionedDeviceAuditThrottle>();

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

        // Telemetry Queue & Sequential Partitioning (ADR-016: Sequential by Originator)
        string queueEngine = configuration["TelemetryQueue:Engine"] ?? "RedisStreams";
        int partitionCount = int.TryParse(configuration["TelemetryQueue:PartitionCount"], out var pc) ? pc : 8;

        if (string.Equals(queueEngine, "InMemory", StringComparison.OrdinalIgnoreCase))
        {
            services.AddSingleton<InMemoryTelemetryQueueService>(sp =>
                new InMemoryTelemetryQueueService(
                    sp.GetRequiredService<ILogger<InMemoryTelemetryQueueService>>(),
                    partitionCount));
            services.AddSingleton<ITelemetryQueuePublisher>(sp => sp.GetRequiredService<InMemoryTelemetryQueueService>());
            services.AddSingleton<ITelemetryQueueConsumer>(sp => sp.GetRequiredService<InMemoryTelemetryQueueService>());
        }
        else if (string.Equals(queueEngine, "Direct", StringComparison.OrdinalIgnoreCase))
        {
            services.AddSingleton<ITelemetryQueuePublisher, DirectTelemetryQueuePublisher>();
        }
        else
        {
            int maxLen = int.TryParse(configuration["TelemetryQueue:MaxStreamLength"], out var ml) ? ml : 10000;
            string? consumerGroup = configuration["TelemetryQueue:ConsumerGroup"];
            services.AddSingleton<RedisTelemetryStreamService>(sp =>
                new RedisTelemetryStreamService(
                    sp.GetRequiredService<IConnectionMultiplexer>(),
                    sp.GetRequiredService<ILogger<RedisTelemetryStreamService>>(),
                    partitionCount,
                    maxLen,
                    consumerGroup));
            services.AddSingleton<ITelemetryQueuePublisher>(sp => sp.GetRequiredService<RedisTelemetryStreamService>());
            services.AddSingleton<ITelemetryQueueConsumer>(sp => sp.GetRequiredService<RedisTelemetryStreamService>());
        }

        // Broker MQTT EMBEBIDO (D1: broker propio de References/Mqtt-Broker, no externo).
        // El backend ES el broker: autentica, intercepta publishes → pipeline de ingesta,
        // y hace push server→device. Implementa IMqttPublisher.
        services.AddSingleton<IMqttClientMap, RedisMqttClientMap>();
        services.AddSingleton<EmbeddedMqttBroker>();
        services.AddSingleton<IMqttPublisher>(sp => sp.GetRequiredService<EmbeddedMqttBroker>());
        services.AddSingleton<IMqttSessionRevoker>(sp => sp.GetRequiredService<EmbeddedMqttBroker>());
        services.AddSingleton<IMqttPeerEndpointRegistry>(sp => sp.GetRequiredService<EmbeddedMqttBroker>());

        // Cliente MQTT saliente legacy (IDeviceTransport). Conservado para rutas de
        // transporte no-broker; no se usa ya como IMqttPublisher.
        services.AddSingleton<MqttDeviceTransport>();
        services.AddSingleton<IDeviceTransport>(sp => sp.GetRequiredService<MqttDeviceTransport>());

        // Declarative Device Profiles Registry & Topic Routing (E2-02, E3-02)
        services.AddSingleton<IProtocolRegistry>(_ => ProtocolRegistry.CreateDefault());

        // Convertidores dinámicos por tenant (adopción ThingsBoard, TB-2): la caché de conectores
        // resueltos es de PROCESO (singleton); el resolver es scoped porque consulta la base vía el
        // repositorio (DbContext scoped) al expirar el TTL.
        services.AddSingleton<Protocols.ITenantConverterScriptCache, Protocols.TenantConverterScriptCache>();
        services.AddScoped<IoT.Application.Protocols.ITenantConverterResolver, Protocols.TenantConverterResolver>();
        services.AddSingleton<IoT.Protocols.Routing.ITopicRoutingService, IoT.Protocols.Routing.TopicRoutingService>();

        // HTTP Device Transport (E3-04). Timeout acotado: un endpoint de dispositivo lento
        // no debe bloquear el hilo de downlink indefinidamente.
        services.AddHttpClient("IoT.HttpDeviceTransport", c => c.Timeout = TimeSpan.FromSeconds(15));
        services.AddHttpClient("OutboundWebhookClient", c => c.Timeout = TimeSpan.FromSeconds(5));
        services.AddSingleton<Http.HttpDeviceTransport>();

        // Gateway TCP multi-protocolo (E5, ADR-007): un handler por familia + un servicio
        // con un listener por protocolo (puerto por protocolo). SOLO singletons aquí; el
        // HostedService se registra en AddDeviceFacingGateways (un solo proceso).
        services.AddSingleton<IoT.Protocols.Tcp.ITcpProtocolHandler, IoT.Protocols.Tcp.Gt06TcpHandler>();
        // Dialecto Mictrack: mismo cuerpo de trama que GT06 pero posición 0x22 y alarma 0x26.
        // Va en su propio listener porque el 0x22 del X3Tech NT20 tiene otro layout.
        services.AddSingleton<IoT.Protocols.Tcp.ITcpProtocolHandler, IoT.Protocols.Tcp.MictrackTcpHandler>();
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
        // Cierra las campanas OTA que quedaron a medias por un reinicio del proceso. Va aqui
        // porque la transmision vive en este mismo proceso.
        services.AddHostedService<Services.OtaStartupReconciliationService>();
        // Vigilante del proceso EN MARCHA: cierra campañas OTA colgadas (equipo que nunca
        // respondió, o reporte de éxito perdido en QoS 0). La reconciliación de arriba sólo
        // cubre el reinicio del proceso.
        services.AddHostedService<Services.OtaCampaignWatchdogService>();
        services.AddHostedService(sp => sp.GetRequiredService<EmbeddedMqttBroker>());
        services.AddHostedService(sp => sp.GetRequiredService<Tcp.MultiProtocolTcpGatewayService>());
        return services;
    }
}
