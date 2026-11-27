using IoT.Application;
using IoT.Infrastructure;
using IoT.Worker;

using IoT.Application.Common.Interfaces;

var builder = Host.CreateApplicationBuilder(args);

// Register layers & Tenant Context for Ingestion Scopes
builder.Services.AddScoped<ITenantContext, WorkerTenantContext>();
builder.Services.AddSingleton<IFleetNotificationService, IoT.Infrastructure.Redis.RedisFleetNotificationPublisher>();
builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);

// Hosted Services (Production)
// La ingesta MQTT la realiza AHORA el broker embebido en el proceso API
// (intercepta publishes → pipeline). El TelemetryIngestionWorker (suscripción a
// broker externo) queda deshabilitado por defecto; se habilita sólo si se usa un
// broker externo poniendo Mqtt:ExternalSubscriptionEnabled=true.
if (builder.Configuration.GetValue<bool>("Mqtt:ExternalSubscriptionEnabled", false))
{
    builder.Services.AddHostedService<TelemetryIngestionWorker>();
}
builder.Services.AddHostedService<DataRetentionWorker>();
builder.Services.AddHostedService<DevicePresenceWorker>();

// Telemetry Stream Partitioned Consumer (ThingsBoard Pattern 2: Sequential by Originator, ADR-016)
if (!string.Equals(builder.Configuration["TelemetryQueue:Engine"], "Direct", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddHostedService<TelemetryStreamConsumerWorker>();
}

// Latido de liveness para el HEALTHCHECK del contenedor (el worker no expone HTTP).
builder.Services.AddHostedService<HeartbeatWorker>();

var host = builder.Build();

using (var scope = host.Services.CreateScope())
{
    var sp = scope.ServiceProvider;
    var config = sp.GetRequiredService<IConfiguration>();
    var env = sp.GetRequiredService<IHostEnvironment>();
    var runtimeConn = config.GetConnectionString("Postgres");
    if (!string.IsNullOrWhiteSpace(runtimeConn))
    {
        await IoT.Infrastructure.Persistence.RlsStartupGuard.EnsureIsolationEnforcedAsync(
            runtimeConn,
            env.IsProduction(),
            config.GetValue<bool>("Platform:Rls:Enforce", false),
            sp.GetRequiredService<ILoggerFactory>().CreateLogger("IoT.Infrastructure.Persistence.RlsStartupGuard"));
    }
}

await host.RunAsync();
