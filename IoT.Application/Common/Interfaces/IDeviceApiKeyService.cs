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

    /// <summary>
    /// Deriva el hash y la huella de una clave YA conocida (gestionada fuera de la plataforma),
    /// para IMPORTARLA como credencial de un dispositivo sin acuñar una aleatoria. Usa la MISMA
    /// derivación que <see cref="Generate"/> (SHA-256 hex + huella = primeros 8 hex), de modo que
    /// la credencial importada es indistinguible de una acuñada frente al broker. No genera clave.
    /// </summary>
    (string Hash, string Fingerprint) DeriveFromKnownKey(string plainKey);

    /// <summary>
    /// Calcula el hash de una clave para BUSCARLA por él (lectura indexada). Se expone,
    /// frente a <see cref="Verify"/>, para resolver qué tenant posee una credencial de
    /// bootstrap sin barrer todos los tenants. Es seguro porque la clave es un secreto
    /// aleatorio de alta entropía: el hash no es atacable por fuerza bruta y una igualdad
    /// exacta por hash no abre un oráculo de temporización como sí lo haría comparar
    /// secretos de baja entropía. Para validar una clave YA conocida, usar Verify.
    /// </summary>
    string ComputeHash(string plainKey);
}
