namespace IoT.Application.Common.Interfaces;

/// <summary>
/// Limita la auditoría de dispositivos NO aprovisionados a una fila por identificador y por
/// ventana de tiempo.
///
/// La ingesta escribía una fila de AuditLog MÁS un SaveChanges por CADA trama cuyo
/// identificador no resuelve a un dispositivo registrado (LOW-3). Para una conexión sin
/// identidad de transporte (credencial de bootstrap o modo anónimo) eso significaba crecimiento
/// no acotado de la tabla de auditoría y presión de escritura sostenida sobre PostgreSQL, con el
/// canal de ingesta descartando telemetría real por backpressure. El operador quiere saber que
/// un equipo desconocido está intentando conectar, no una fila por paquete.
/// </summary>
public interface IUnprovisionedDeviceAuditThrottle
{
    /// <summary>
    /// ¿Debe auditarse AHORA este identificador no aprovisionado? Devuelve true la primera vez
    /// que se ve y luego, como mucho, una vez por ventana; registra el instante. Es un
    /// cortafuegos de proceso: es seguro que varios tenants compartan la instancia porque un
    /// equipo no aprovisionado no pertenece a ninguno.
    /// </summary>
    bool ShouldAudit(string identifier);
}
