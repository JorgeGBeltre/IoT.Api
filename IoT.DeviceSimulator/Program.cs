using IoT.DeviceSimulator;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

// El simulador abre UNA conexión MQTT por tenant (cada una con la credencial de bootstrap
// de su tenant), porque la credencial ya no es global y no puede publicar como equipos de
// otro tenant (MEDIUM-2).
builder.Services.AddSingleton<TenantMqttFleet>();

// Register Simulator Background Worker
builder.Services.AddHostedService<SimulatedDeviceWorker>();

var host = builder.Build();
host.Run();
