using IoT.Protocols.Contracts;

namespace IoT.Application.Protocols;

/// <summary>
/// Resuelve, para un tenant y una clave de perfil/protocolo, el <see cref="IProtocolConnector"/>
/// cuyo convertidor de subida es el script dinámico (Jint) que ese tenant tiene habilitado. Es la
/// costura por la que la ingesta consulta "¿este tenant personaliza la decodificación de este
/// protocolo?": si devuelve un conector, la ingesta decodifica con él; si devuelve null, usa el
/// adaptador nativo.
///
/// La resolución se CACHEA por (tenant, claveDePerfil, hash-del-script) para no tocar la base ni
/// re-preparar el AST en cada trama. El resultado queda acotado al tenant indicado (el que resolvió
/// el dispositivo en la ruta caliente); un tenant jamás obtiene el convertidor de otro.
/// </summary>
public interface ITenantConverterResolver
{
    Task<IProtocolConnector?> ResolveUplinkConnectorAsync(Guid tenantId, string profileKey, CancellationToken ct);
}
