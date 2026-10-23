using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace IoT.Api.Hubs;

[Authorize]
public sealed class FleetHub : Hub
{
    private readonly ILogger<FleetHub> _logger;

    public FleetHub(ILogger<FleetHub> logger)
    {
        _logger = logger;
    }

    public async Task JoinTenantFleet(string tenantId)
    {
        var userTenantId = Context.User?.FindFirst("tenant_id")?.Value;
        if (string.IsNullOrWhiteSpace(userTenantId) || !string.Equals(userTenantId, tenantId, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("Security Violation: Connection {ConnectionId} with tenant {UserTenant} attempted unauthorized join to tenant fleet {TargetTenant}",
                Context.ConnectionId, userTenantId, tenantId);
            throw new HubException("No tienes autorización para unirte a la flota de este tenant.");
        }

        string groupName = $"tenant_{tenantId}";
        await Groups.AddToGroupAsync(Context.ConnectionId, groupName);
        _logger.LogInformation("Client {ConnectionId} joined fleet group {GroupName}", Context.ConnectionId, groupName);
    }

    public async Task LeaveTenantFleet(string tenantId)
    {
        string groupName = $"tenant_{tenantId}";
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, groupName);
        _logger.LogInformation("Client {ConnectionId} left fleet group {GroupName}", Context.ConnectionId, groupName);
    }

    public async Task JoinDeviceLive(string deviceId)
    {
        var userTenantId = Context.User?.FindFirst("tenant_id")?.Value;
        if (string.IsNullOrWhiteSpace(userTenantId))
        {
            throw new HubException("Se requiere un tenant válido para suscribirse a dispositivos.");
        }

        string groupName = $"device_{deviceId}";
        await Groups.AddToGroupAsync(Context.ConnectionId, groupName);
        _logger.LogInformation("Client {ConnectionId} (Tenant {Tenant}) subscribed to live device {GroupName}", Context.ConnectionId, userTenantId, groupName);
    }

    public async Task LeaveDeviceLive(string deviceId)
    {
        string groupName = $"device_{deviceId}";
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
}
