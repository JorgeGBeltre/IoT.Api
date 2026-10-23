namespace IoT.Application.Common.Interfaces;

/// <summary>
/// Contract for sending commands to devices connected via TCP Binary Gateway (e.g. SEEWORLD R12L-F / GT06).
/// </summary>
public interface ITcpCommandGateway
{
    /// <summary>
    /// Checks if a device with the given IMEI has an active, open TCP connection.
    /// </summary>
    bool IsConnected(string imei);

    /// <summary>
    /// Sends a binary command packet (Protocol 0x80) to the connected device.
    /// </summary>
    Task<bool> SendCommandAsync(string imei, string commandText, CancellationToken cancellationToken = default);
}
