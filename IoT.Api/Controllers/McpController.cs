using System.Text.Json;
using IoT.Api.Common;
using IoT.Application.Common.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace IoT.Api.Controllers;

/// <summary>
/// Endpoint del Servidor MCP (Model Context Protocol) para IA supervisada sobre la flota vehicular y dispositivos.
/// Permite a clientes de IA (Claude Desktop, Cursor, agentes autónomos) consultar estado, telemetría y emitir comandos.
/// </summary>
[Authorize]
[RequireTenantDataPlane]
[ApiController]
[Route("api/v1/mcp")]
public sealed class McpController : ControllerBase
{
    private readonly IMcpFleetService _mcpFleetService;
    private readonly ITenantContext _tenantContext;
    private readonly ILogger<McpController> _logger;

    public McpController(
        IMcpFleetService mcpFleetService,
        ITenantContext tenantContext,
        ILogger<McpController> logger)
    {
        _mcpFleetService = mcpFleetService;
        _tenantContext = tenantContext;
        _logger = logger;
    }

    [HttpGet]
    public IActionResult GetServerInfo()
    {
        return Ok(new
        {
            name = "IoT-Nexus-MCP-Server",
            version = "1.0.0",
            protocolVersion = "2024-11-05",
            tenantId = _tenantContext.TenantId,
            tools = new[]
            {
                "list_fleet_status",
                "get_device_telemetry",
                "get_device_events",
                "send_device_command"
            }
        });
    }

    [HttpPost]
    public async Task<IActionResult> HandleJsonRpc(
        [FromBody] JsonRpcRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.Method))
        {
            return BadRequest(BuildErrorResponse(request?.Id, -32600, "Invalid Request"));
        }

        var tenantId = _tenantContext.TenantId;
        if (tenantId == Guid.Empty)
        {
            return Unauthorized(BuildErrorResponse(request.Id, -32001, "Tenant no identificado en la sesión."));
        }

        try
        {
            switch (request.Method.ToLowerInvariant())
            {
                case "initialize":
                    return Ok(new
                    {
                        jsonrpc = "2.0",
                        id = request.Id,
                        result = new
                        {
                            protocolVersion = "2024-11-05",
                            capabilities = new
                            {
                                tools = new { }
                            },
                            serverInfo = new
                            {
                                name = "IoT-Nexus-MCP-Server",
                                version = "1.0.0"
                            }
                        }
                    });

                case "notifications/initialized":
                    return Ok();

                case "tools/list":
                    return Ok(new
                    {
                        jsonrpc = "2.0",
                        id = request.Id,
                        result = new
                        {
                            tools = GetToolCatalog()
                        }
                    });

                case "tools/call":
                    var callResult = await ExecuteToolCallAsync(tenantId, request.Params, cancellationToken);
                    return Ok(new
                    {
                        jsonrpc = "2.0",
                        id = request.Id,
                        result = callResult
                    });

                default:
                    return Ok(BuildErrorResponse(request.Id, -32601, $"Method '{request.Method}' not found."));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error procesando petición MCP JSON-RPC {Method}", request.Method);
            return Ok(BuildErrorResponse(request.Id, -32603, ex.Message));
        }
    }

    private async Task<object> ExecuteToolCallAsync(Guid tenantId, JsonElement? parameters, CancellationToken cancellationToken)
    {
        if (parameters == null || !parameters.Value.TryGetProperty("name", out var nameProp))
        {
            return BuildMcpToolResult("Parámetro 'name' faltante en tools/call.", isError: true);
        }

        string toolName = nameProp.GetString() ?? string.Empty;
        var args = parameters.Value.TryGetProperty("arguments", out var argsProp) ? argsProp : default;

        switch (toolName.ToLowerInvariant())
        {
            case "list_fleet_status":
            {
                string? statusFilter = null;
                if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("statusFilter", out var sf))
                {
                    statusFilter = sf.GetString();
                }

                var data = await _mcpFleetService.ListFleetStatusAsync(tenantId, statusFilter, cancellationToken);
                return BuildMcpToolResult(JsonSerializer.Serialize(data, JsonOpts));
            }

            case "get_device_telemetry":
            {
                if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty("deviceId", out var dIdProp))
                {
                    return BuildMcpToolResult("El argumento 'deviceId' es obligatorio.", isError: true);
                }

                string deviceId = dIdProp.GetString() ?? string.Empty;
                var data = await _mcpFleetService.GetDeviceTelemetryAsync(tenantId, deviceId, cancellationToken);
                if (data == null)
                {
                    return BuildMcpToolResult($"Dispositivo '{deviceId}' no encontrado o no pertenece a tu organización.", isError: true);
                }

                return BuildMcpToolResult(JsonSerializer.Serialize(data, JsonOpts));
            }

            case "get_device_events":
            {
                Guid? targetDeviceId = null;
                int limit = 20;

                if (args.ValueKind == JsonValueKind.Object)
                {
                    if (args.TryGetProperty("deviceId", out var dIdProp) && Guid.TryParse(dIdProp.GetString(), out var parsedId))
                    {
                        targetDeviceId = parsedId;
                    }
                    if (args.TryGetProperty("limit", out var limProp) && limProp.TryGetInt32(out var parsedLim))
                    {
                        limit = parsedLim;
                    }
                }

                var data = await _mcpFleetService.GetDeviceEventsAsync(tenantId, targetDeviceId, limit, cancellationToken);
                return BuildMcpToolResult(JsonSerializer.Serialize(data, JsonOpts));
            }

            case "send_device_command":
            {
                if (args.ValueKind != JsonValueKind.Object ||
                    !args.TryGetProperty("deviceId", out var dIdProp) ||
                    !Guid.TryParse(dIdProp.GetString(), out var deviceGuid) ||
                    !args.TryGetProperty("commandName", out var cmdProp))
                {
                    return BuildMcpToolResult("Argumentos 'deviceId' (UUID) y 'commandName' son obligatorios.", isError: true);
                }

                string commandName = cmdProp.GetString() ?? string.Empty;
                string? payloadJson = null;
                if (args.TryGetProperty("payloadJson", out var pjProp))
                {
                    payloadJson = pjProp.GetString();
                }

                var result = await _mcpFleetService.SendDeviceCommandAsync(tenantId, deviceGuid, commandName, payloadJson, cancellationToken);
                if (result.IsFailure)
                {
                    return BuildMcpToolResult($"Fallo al despachar comando: {result.Error.Message}", isError: true);
                }

                return BuildMcpToolResult(JsonSerializer.Serialize(new
                {
                    commandId = result.Value,
                    status = "QueuedOrDispatched",
                    deviceId = deviceGuid,
                    commandName,
                    dispatchedAtUtc = DateTime.UtcNow
                }, JsonOpts));
            }

            default:
                return BuildMcpToolResult($"Herramienta '{toolName}' desconocida.", isError: true);
        }
    }

    private static object BuildMcpToolResult(string text, bool isError = false)
    {
        return new
        {
            content = new[]
            {
                new
                {
                    type = "text",
                    text
                }
            },
            isError
        };
    }

    private static object BuildErrorResponse(object? id, int code, string message)
    {
        return new
        {
            jsonrpc = "2.0",
            id,
            error = new
            {
                code,
                message
            }
        };
    }

    private static object[] GetToolCatalog()
    {
        return new object[]
        {
            new
            {
                name = "list_fleet_status",
                description = "Lista el estado general de la flota vehicular y dispositivos IoT del tenant autenticado.",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        statusFilter = new
                        {
                            type = "string",
                            description = "Filtro opcional por estado operativo ('Online', 'Offline', 'Unknown')"
                        }
                    }
                }
            },
            new
            {
                name = "get_device_telemetry",
                description = "Obtiene la telemetría más reciente, métricas y última posición GPS de un dispositivo por su ID o ChipId.",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        deviceId = new
                        {
                            type = "string",
                            description = "UUID o ChipId/IMEI del dispositivo"
                        }
                    },
                    required = new[] { "deviceId" }
                }
            },
            new
            {
                name = "get_device_events",
                description = "Consulta las últimas alertas operativas y eventos de auditoría/comandos del dispositivo o flota.",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        deviceId = new
                        {
                            type = "string",
                            description = "UUID opcional del dispositivo"
                        },
                        limit = new
                        {
                            type = "integer",
                            description = "Cantidad máxima de eventos (1-100)"
                        }
                    }
                }
            },
            new
            {
                name = "send_device_command",
                description = "Despacha un comando de control remoto supervisado a un dispositivo (ej. corte de combustible, reinicio, seteo de intervalo).",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        deviceId = new
                        {
                            type = "string",
                            description = "UUID del dispositivo destino"
                        },
                        commandName = new
                        {
                            type = "string",
                            description = "Nombre del comando (ej. 'CUT_FUEL', 'RESTORE_FUEL', 'REBOOT', 'SET_INTERVAL')"
                        },
                        payloadJson = new
                        {
                            type = "string",
                            description = "Payload JSON opcional con parámetros adicionales"
                        }
                    },
                    required = new[] { "deviceId", "commandName" }
                }
            }
        };
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
}

public sealed class JsonRpcRequest
{
    public string Jsonrpc { get; set; } = "2.0";
    public object? Id { get; set; }
    public string Method { get; set; } = string.Empty;
    public JsonElement? Params { get; set; }
}
