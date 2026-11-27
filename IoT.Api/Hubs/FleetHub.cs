using IoT.Application.Common.Interfaces;
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
    private readonly ITenantContext? _tenantContext;
    private readonly ICustomerRepository? _customerRepository;

    public FleetHub(
        IDeviceRepository deviceRepository,
        ILogger<FleetHub> logger,
        ITenantContext? tenantContext = null,
        ICustomerRepository? customerRepository = null)
    {
        _deviceRepository = deviceRepository;
        _logger = logger;
        _tenantContext = tenantContext;
        _customerRepository = customerRepository;
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

        if (Context.User is not null && IoT.Api.Common.AuthorizationPolicies.IsSubCustomerScoped(Context.User))
        {
            _logger.LogWarning("Security Violation: Sub-customer connection {ConnectionId} attempted to join root tenant group tenant_{TenantId}",
                Context.ConnectionId, requestedTenant);
            throw new HubException("Los usuarios acotados a un sub-cliente deben suscribirse por dispositivo visible.");
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

        if (_tenantContext is not null)
        {
            _tenantContext.TenantId = userTenantId.Value;
            _tenantContext.IsSystemOperation = false;
            var customerPath = Context.User?.FindFirst("customer_path")?.Value;
            var rawCustomerId = Context.User?.FindFirst("customer_id")?.Value;
            if (!string.IsNullOrWhiteSpace(customerPath) && Guid.TryParse(rawCustomerId, out var cid) && cid != Guid.Empty)
            {
                _tenantContext.CustomerId = cid;
                _tenantContext.CustomerPath = customerPath;
                if (_customerRepository is not null)
                {
                    _tenantContext.CustomerSubtreeIds = await _customerRepository.GetSubtreeIdsAsync(
                        customerPath, Context.ConnectionAborted);
                }
            }
        }

        // B-01 + ADR-013: validar tanto pertenencia al tenant como visibilidad en el subárbol del cliente.
        bool isSubCustomer = Context.User is not null && IoT.Api.Common.AuthorizationPolicies.IsSubCustomerScoped(Context.User);
        bool allowed = Guid.TryParse(deviceId, out var parsedDeviceId)
            && await _deviceRepository.BelongsToTenantAsync(parsedDeviceId, userTenantId.Value, Context.ConnectionAborted)
            && (!isSubCustomer || await _deviceRepository.GetByIdAsync(parsedDeviceId, Context.ConnectionAborted) is not null);
        if (!allowed)
        {
            _logger.LogWarning("Security Violation: Connection {ConnectionId} with tenant {UserTenant} attempted to subscribe to device {DeviceId} outside its scope",
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
