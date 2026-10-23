namespace IoT.Application.Common.Interfaces;

/// <summary>
/// Genera y verifica las ApiKeys MQTT por dispositivo (modelo de adopción por ApiKey).
/// La clave es un secreto aleatorio de alta entropía; se almacena sólo su hash
/// (SHA-256, rápido y adecuado porque no hay riesgo de fuerza bruta sobre un
/// secreto aleatorio de 256 bits) y una huella corta no reversible para la UI.
/// </summary>
public interface IDeviceApiKeyService
{
    /// <summary>Genera una nueva ApiKey. Devuelve el texto plano (se muestra una sola vez), su hash y su huella.</summary>
    (string PlainKey, string Hash, string Fingerprint) Generate();

    /// <summary>Verifica (comparación en tiempo constante) una ApiKey presentada contra un hash almacenado.</summary>
    bool Verify(string plainKey, string hash);
}
