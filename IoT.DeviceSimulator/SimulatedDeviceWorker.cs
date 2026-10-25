using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IoT.DeviceSimulator;

public sealed class SimulatedDeviceWorker : BackgroundService
{
    // Cada equipo simulado pertenece a un tenant; se publica por la conexión de ESE tenant,
    // porque la plataforma rechaza publicar como un equipo de otro tenant (MEDIUM-2). Los
    // códigos coinciden con los sembrados en DatabaseSeeder.
    private const string TenantCaribe = "CARIBE-LOG";  // GPS-DEMO01, GPS-DEMO02
    private const string TenantGlp = "DOM-GLP";         // GLP-TANK01
    private const string TenantSmart = "SMART-ACT";     // RELAY-CTRL01

    private readonly TenantMqttFleet _fleet;
    private readonly IConfiguration _configuration;
    private readonly ILogger<SimulatedDeviceWorker> _logger;

    public SimulatedDeviceWorker(
        TenantMqttFleet fleet,
        IConfiguration configuration,
        ILogger<SimulatedDeviceWorker> logger)
    {
        _fleet = fleet;
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Dedicated Device Simulator starting... (Publishing external MQTT traffic marked as source: simulated)");

        int intervalSeconds = _configuration.GetValue<int>("Simulator:IntervalSeconds", 4);

        await _fleet.ConnectAllAsync(stoppingToken);
        if (_fleet.ConnectedTenants.Count == 0)
        {
            _logger.LogError("El simulador no logró conectar a ningún tenant; no hay nada que publicar.");
            return;
        }

        var waypoints = new (double Lat, double Lng, float Heading)[]
        {
            (18.4861, -69.9312, 90f),
            (18.4865, -69.9280, 95f),
            (18.4870, -69.9240, 85f),
            (18.4855, -69.9200, 140f),
            (18.4820, -69.9190, 180f),
            (18.4780, -69.9210, 210f),
            (18.4750, -69.9250, 240f),
            (18.4760, -69.9300, 310f),
            (18.4810, -69.9330, 350f)
        };

        int waypointIndex = 0;
        double currentGlpPressure = 6.2;
        double currentGlpVolume = 82.5;

        await Task.Delay(2000, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // 1. Simulate GPS Unit 1 (Camión Distribuidor)
                var wp = waypoints[waypointIndex];
                waypointIndex = (waypointIndex + 1) % waypoints.Length;

                var gpsPayload = new
                {
                    Device = new { ChipId = "GPS-DEMO01" },
                    Timestamp = DateTime.UtcNow.ToString("o"),
                    Details = new
                    {
                        accident = new
                        {
                            gps_data = new
                            {
                                latitude = wp.Lat + (Random.Shared.NextDouble() - 0.5) * 0.0005,
                                longitude = wp.Lng + (Random.Shared.NextDouble() - 0.5) * 0.0005,
                                speed = 42.0 + Random.Shared.NextDouble() * 15.0,
                                heading = wp.Heading,
                                altitude = 35.0,
                                satellites = 14,
                                is_valid = true
                            }
                        }
                    }
                };

                await PublishSimulatedAsync(TenantCaribe, "telemetry", JsonSerializer.Serialize(gpsPayload), stoppingToken);

                // 2. Simulate GPS Unit 2 (Furgón Logístico)
                var gps2Payload = new
                {
                    Device = new { ChipId = "GPS-DEMO02" },
                    Timestamp = DateTime.UtcNow.ToString("o"),
                    Details = new
                    {
                        accident = new
                        {
                            gps_data = new
                            {
                                latitude = 18.4700 + (Random.Shared.NextDouble() * 0.01),
                                longitude = -69.9400 + (Random.Shared.NextDouble() * 0.01),
                                speed = 55.0,
                                heading = 45f,
                                altitude = 28.0,
                                satellites = 12,
                                is_valid = true
                            }
                        }
                    }
                };
                await PublishSimulatedAsync(TenantCaribe, "telemetry", JsonSerializer.Serialize(gps2Payload), stoppingToken);

                // 3. Simulate GLP Tank Monitor
                currentGlpPressure += (Random.Shared.NextDouble() - 0.5) * 0.08;
                currentGlpVolume -= 0.05;
                if (currentGlpVolume < 20.0) currentGlpVolume = 85.0;

                var glpPayload = new
                {
                    Device = new { DeviceId = "GLP-TANK01", ChipId = "GLP-TANK01" },
                    Timestamp = DateTime.UtcNow.ToString("o"),
                    Details = new
                    {
                        sensors_raw = new
                        {
                            level_mm = 850.0 + (Random.Shared.NextDouble() * 5.0),
                            pressure_bar = Math.Round(currentGlpPressure + 0.05, 2),
                            temp_celsius = Math.Round(28.4 + (Random.Shared.NextDouble() * 1.2), 1)
                        },
                        sensors_filtered = new
                        {
                            level_mm = 850.0,
                            pressure_bar = Math.Round(currentGlpPressure, 2),
                            temp_celsius = 28.2,
                            kalman_uncertainty_mm = 0.04
                        },
                        volume = new
                        {
                            percentage = Math.Round(currentGlpVolume, 1),
                            liters = Math.Round(currentGlpVolume * 2.5, 1),
                            mass_kg = Math.Round(currentGlpVolume * 1.4, 1)
                        },
                        rssi = -72,
                        free_heap = 195000,
                        uptime = 7200
                    }
                };
                await PublishSimulatedAsync(TenantGlp, "glp/telemetria/GLP-TANK01", JsonSerializer.Serialize(glpPayload), stoppingToken);

                // 4. Simulate WiFi Relay Actuator
                var relayPayload = new
                {
                    Device = new { ChipId = "RELAY-CTRL01" },
                    Timestamp = DateTime.UtcNow.ToString("o"),
                    Details = new
                    {
                        devices_count = 2,
                        rssi = -64.0,
                        uptime = 14400.0,
                        devices = new
                        {
                            device_switch1 = new { state = "on", desired_state = "on" },
                            device_switch2 = new { state = "off", desired_state = "off" }
                        }
                    }
                };
                await PublishSimulatedAsync(TenantSmart, "relay/telemetria/RELAY-CTRL01", JsonSerializer.Serialize(relayPayload), stoppingToken);

                _logger.LogInformation("Dedicated Simulator: Published telemetry frames for GPS01, GPS02, GLP Tank, Relay Ctrl via MQTT.");
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Dedicated Simulator iteration error: {Message}", ex.Message);
            }

            await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), stoppingToken);
        }
    }

    private static readonly IReadOnlyDictionary<string, string> SimulatedUserProperties =
        new Dictionary<string, string>
        {
            ["source"] = "simulated",
            ["simulator_version"] = "3.0"
        };

    private Task PublishSimulatedAsync(string tenantCode, string topic, string payload, CancellationToken cancellationToken)
        => _fleet.PublishAsync(tenantCode, topic, payload, SimulatedUserProperties, cancellationToken);
}
