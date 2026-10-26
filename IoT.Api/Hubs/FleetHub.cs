using IoT.Domain.Repositories;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace IoT.Api.Hubs;

[Authorize]
public sealed class FleetHub : Hub
{
    private const string TenantClaim = "tenant_id";

    private readonly IDeviceRepository _deviceRepository;
    private readonly ILogger<FleetHub> _logger;

    public FleetHub(IDeviceRepository deviceRepository, ILogger<FleetHub> logger)
    {
        _deviceRepository = deviceRepository;
        _logger = logger;
    }

    public async Task JoinTenantFleet(string tenantId)
    {
        var userTenantId = GetUserTenantId();
        if (userTenantId is null
            || !Guid.TryParse(tenantId, out var requestedTenant)
            || requestedTenant != userTenantId.Value)
        {
            _logger.LogWarning("Security Violation: Connection {ConnectionId} with tenant {UserTenant} attempted unauthorized join to tenant fleet {TargetTenant}",
                Context.ConnectionId, userTenantId, tenantId);
            throw new HubException("No tienes autorización para unirte a la flota de este tenant.");
        }

        // Nombre de grupo normalizado (Guid "D", minúsculas) para coincidir exactamente con
        // el que usa SignalRFleetNotificationService al difundir.
        string groupName = $"tenant_{requestedTenant}";
        await Groups.AddToGroupAsync(Context.ConnectionId, groupName);
        _logger.LogInformation("Client {ConnectionId} joined fleet group {GroupName}", Context.ConnectionId, groupName);
    }

    public async Task LeaveTenantFleet(string tenantId)
    {
        if (!Guid.TryParse(tenantId, out var parsed))
        {
            return;
        }

        string groupName = $"tenant_{parsed}";
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, groupName);
        _logger.LogInformation("Client {ConnectionId} left fleet group {GroupName}", Context.ConnectionId, groupName);
    }

    public async Task JoinDeviceLive(string deviceId)
    {
        var userTenantId = GetUserTenantId();
        if (userTenantId is null)
        {
            throw new HubException("Se requiere un tenant válido para suscribirse a dispositivos.");
        }

        // B-01: antes bastaba con tener CUALQUIER tenant para unirse a device_{id} y recibir
        // posiciones, telemetría y progreso OTA de equipos de otra organización. Ahora el
        // dispositivo debe pertenecer al tenant del JWT. Mismo mensaje para "no existe" y
        // "es de otro tenant" (sin oráculo de existencia).
        if (!Guid.TryParse(deviceId, out var parsedDeviceId)
            || !await _deviceRepository.BelongsToTenantAsync(parsedDeviceId, userTenantId.Value, Context.ConnectionAborted))
        {
            _logger.LogWarning("Security Violation: Connection {ConnectionId} with tenant {UserTenant} attempted to subscribe to device {DeviceId} outside its tenant",
                Context.ConnectionId, userTenantId, deviceId);
            throw new HubException("No tienes autorización para suscribirte a este dispositivo.");
        }

        string groupName = $"device_{parsedDeviceId}";
        await Groups.AddToGroupAsync(Context.ConnectionId, groupName);
        _logger.LogInformation("Client {ConnectionId} (Tenant {Tenant}) subscribed to live device {GroupName}", Context.ConnectionId, userTenantId, groupName);
    }

    public async Task LeaveDeviceLive(string deviceId)
    {
        if (!Guid.TryParse(deviceId, out var parsed))
        {
            return;
        }

        string groupName = $"device_{parsed}";
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, groupName);
        _logger.LogInformation("Client {ConnectionId} unsubscribed from live device {GroupName}", Context.ConnectionId, groupName);
    }

    public override Task OnConnectedAsync()
    {
        _logger.LogInformation("SignalR client connected: {ConnectionId}", Context.ConnectionId);
        return base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        _logger.LogInformation("SignalR client disconnected: {ConnectionId}", Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }

    private Guid? GetUserTenantId()
    {
        var raw = Context.User?.FindFirst(TenantClaim)?.Value;
        return Guid.TryParse(raw, out var tenant) && tenant != Guid.Empty ? tenant : null;
    }
}
