using IoT.Domain.Common;

namespace IoT.Domain.Entities;

/// <summary>
/// Preferencias operativas por tenant: política de retención, cola offline de
/// comandos, proveedor de mapas, webhook de eventos. Uno-a-uno con <see cref="Tenant"/>.
/// </summary>
public sealed class TenantSettings : AggregateRoot<Guid>, ITenantScoped
{
    public Guid TenantId { get; private set; }
    public int RetentionDays { get; private set; }
    public bool AllowOfflineQueue { get; private set; }
    public string MapProvider { get; private set; }
    public string WebhookUrl { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime UpdatedAt { get; private set; }

    public const string MapProviderMapbox = "mapbox";
    public const string MapProviderMapLibre = "maplibre";

    private static readonly HashSet<string> AllowedMapProviders = new(StringComparer.OrdinalIgnoreCase)
    {
        MapProviderMapbox, MapProviderMapLibre
    };

    private static readonly int[] AllowedRetentionDays = { 30, 60, 90, 180, 365 };

    private TenantSettings(
        Guid id,
        Guid tenantId,
        int retentionDays,
        bool allowOfflineQueue,
        string mapProvider,
        string webhookUrl) : base(id)
    {
        TenantId = tenantId;
        RetentionDays = retentionDays;
        AllowOfflineQueue = allowOfflineQueue;
        MapProvider = mapProvider;
        WebhookUrl = webhookUrl;
        CreatedAt = DateTime.UtcNow;
        UpdatedAt = DateTime.UtcNow;
    }

    public static Result<TenantSettings> CreateDefault(Guid tenantId)
    {
        if (tenantId == Guid.Empty)
            return Result<TenantSettings>.Failure(new Error("TenantSettings.InvalidTenant", "TenantId no puede estar vacío."));
        return new TenantSettings(Guid.NewGuid(), tenantId, 90, true, MapProviderMapbox, string.Empty);
    }

    public Result Update(int retentionDays, bool allowOfflineQueue, string mapProvider, string webhookUrl)
    {
        if (!AllowedRetentionDays.Contains(retentionDays))
            return Result.Failure(new Error("TenantSettings.InvalidRetention",
                $"RetentionDays debe ser uno de: {string.Join(", ", AllowedRetentionDays)}."));
        if (!AllowedMapProviders.Contains(mapProvider))
            return Result.Failure(new Error("TenantSettings.InvalidMapProvider",
                $"MapProvider '{mapProvider}' no soportado."));

        if (!string.IsNullOrWhiteSpace(webhookUrl))
        {
            if (!Uri.TryCreate(webhookUrl, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            {
                return Result.Failure(new Error("TenantSettings.InvalidWebhook",
                    "La URL del webhook debe ser una URL HTTP(S) válida."));
            }
        }

        RetentionDays = retentionDays;
        AllowOfflineQueue = allowOfflineQueue;
        MapProvider = mapProvider.ToLowerInvariant();
        WebhookUrl = webhookUrl?.Trim() ?? string.Empty;
        UpdatedAt = DateTime.UtcNow;
        return Result.Success();
    }
}
