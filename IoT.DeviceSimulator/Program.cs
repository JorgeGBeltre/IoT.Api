using IoT.Application.Common.Interfaces;
using IoT.DeviceSimulator;
using IoT.Infrastructure.Mqtt;
using IoT.Protocols.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

// Register MQTT Publisher Transport for independent external communication
builder.Services.AddSingleton<MqttDeviceTransport>();
builder.Services.AddSingleton<IMqttPublisher>(sp => sp.GetRequiredService<MqttDeviceTransport>());
builder.Services.AddSingleton<IDeviceTransport>(sp => sp.GetRequiredService<MqttDeviceTransport>());

// Register Simulator Background Worker
builder.Services.AddHostedService<SimulatedDeviceWorker>();

var host = builder.Build();
host.Run();
