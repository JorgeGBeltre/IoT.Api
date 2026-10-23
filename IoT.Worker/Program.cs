using IoT.Application;
using IoT.Infrastructure;
using IoT.Worker;

using IoT.Application.Common.Interfaces;

var builder = Host.CreateApplicationBuilder(args);

// Register layers & Tenant Context for Ingestion Scopes
builder.Services.AddScoped<ITenantContext, WorkerTenantContext>();
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

var host = builder.Build();
host.Run();
