using System.Text.Json;
using FluentAssertions;
using IoT.Protocols.Profiles;
using IoT.Protocols.Tcp;
using Xunit;

namespace IoT.Tests.Integration;

/// <summary>
/// Cableado entre las TRES piezas que tienen que coincidir para que un dispositivo TCP
/// llegue a conectar: el perfil que se elige al darlo de alta, el handler que decodifica su
/// familia, y el listener que abre el puerto.
///
/// Esto existe por un fallo real: el perfil sólo declaraba una etiqueta de display
/// ("TCP (Benway)", "TCP (NT20)" — familias que no existen como handler), el backend no
/// publicaba los puertos en ninguna parte, y el formulario de alta del frontend los traía
/// cableados a mano. Los SEIS puertos estaban mal y dos apuntaban al listener de otra
/// familia, así que cualquier equipo dado de alta siguiendo la UI nunca conectaba.
/// </summary>
public class ProtocolFamilyWiringTests
{
    private static IReadOnlyList<ITcpProtocolHandler> AllHandlers() =>
    [
        new Gt06TcpHandler(),
        new MictrackTcpHandler(),
        new QueclinkTcpHandler(),
        new CobanTcpHandler(),
        new IStartekTcpHandler(),
        new JointechTcpHandler()
    ];

    /// <summary>Sube por el árbol hasta encontrar el appsettings.json del API.</summary>
    private static string FindApiAppSettings()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, "IoT.Api", "appsettings.json");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }

        throw new FileNotFoundException("No se encontró IoT.Api/appsettings.json subiendo desde " + AppContext.BaseDirectory);
    }

    private static Dictionary<string, int> ConfiguredListeners()
    {
        // Se toleran comentarios y comas finales: el proveedor de configuración de .NET los
        // admite, así que el fichero puede tenerlos y el test no debe ser más estricto que
        // la aplicación que lo lee.
        var options = new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
        using var doc = JsonDocument.Parse(File.ReadAllText(FindApiAppSettings()), options);
        var listeners = doc.RootElement.GetProperty("TcpGateway").GetProperty("Listeners");

        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in listeners.EnumerateArray())
        {
            result[entry.GetProperty("Protocol").GetString()!] = entry.GetProperty("Port").GetInt32();
        }
        return result;
    }

    [Fact]
    public void EveryProfileTcpFamilyIsDecodedByARegisteredHandler()
    {
        var handlerFamilies = AllHandlers().Select(h => h.Family).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var declared = ProtocolRegistry.CreateDefault().GetAllProfiles()
            .Where(p => !string.IsNullOrWhiteSpace(p.TcpFamily))
            .Select(p => new { p.ProfileId, Family = p.TcpFamily! })
            .ToList();

        declared.Should().NotBeEmpty();

        foreach (var profile in declared)
        {
            handlerFamilies.Should().Contain(profile.Family,
                $"el perfil '{profile.ProfileId}' declara la familia '{profile.Family}' y ningún ITcpProtocolHandler la decodifica");
        }
    }

    [Fact]
    public void EveryProfileTcpFamilyHasAListenerOnAPort()
    {
        var listeners = ConfiguredListeners();

        var declared = ProtocolRegistry.CreateDefault().GetAllProfiles()
            .Where(p => !string.IsNullOrWhiteSpace(p.TcpFamily))
            .Select(p => new { p.ProfileId, Family = p.TcpFamily! })
            .ToList();

        foreach (var profile in declared)
        {
            listeners.Keys.Should().Contain(profile.Family,
                $"el perfil '{profile.ProfileId}' es de la familia '{profile.Family}' y no hay listener que abra su puerto: " +
                "el operador no tendría a dónde apuntar el equipo");
        }
    }

    [Fact]
    public void EveryConfiguredListenerHasAHandler_SoNoPortAcceptsFramesNobodyDecodes()
    {
        var handlerFamilies = AllHandlers().Select(h => h.Family).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var (protocol, port) in ConfiguredListeners())
        {
            handlerFamilies.Should().Contain(protocol,
                $"el listener '{protocol}' abre el puerto {port} y no hay handler que decodifique esa familia");
        }
    }

    [Fact]
    public void NoTwoListenersShareAPort()
    {
        // Dos familias en el mismo puerto significa que una de ellas decodifica tramas
        // ajenas: es exactamente el fallo que tenía la UI (Queclink apuntado al 5001, que
        // es el listener de iStartek).
        var listeners = ConfiguredListeners();

        listeners.Values.Should().OnlyHaveUniqueItems(
            "cada familia necesita su propio puerto (decisión de diseño: un puerto por protocolo)");
    }

    [Fact]
    public void ProfilesWithoutATcpChannelDeclareNoFamily()
    {
        var profiles = ProtocolRegistry.CreateDefault().GetAllProfiles().ToList();

        // El Queclink GL300 reporta por SMS y no existe ningún canal SMS en el backend:
        // declarar familia y puerto daría a entender que basta apuntarlo a un TCP.
        var gl300 = profiles.Single(p => p.ProfileId == "queclink-gl300");
        gl300.TcpFamily.Should().BeNull("el GL300 reporta por SMS y la plataforma no tiene canal SMS");

        // Los dispositivos MQTT nativos tampoco entran por el gateway TCP.
        foreach (var profileId in new[] { "glp-pressure", "relay-switch", "ticket-validator", "access-control" })
        {
            var profile = profiles.FirstOrDefault(p => p.ProfileId == profileId);
            if (profile is null) continue;
            profile.TcpFamily.Should().BeNull($"'{profileId}' es MQTT nativo, no entra por un puerto TCP");
        }
    }

    [Fact]
    public void TheMictrackDialectHasItsOwnListener_SeparateFromGt06()
    {
        // El dialecto Mictrack reinterpreta 0x22/0x26 como posición/alarma. Si compartiese
        // puerto con GT06, un X3Tech NT20 en modo "new frame" (que usa 0x22 con OTRO
        // layout) se decodificaría con los offsets equivocados y publicaría posiciones falsas.
        var listeners = ConfiguredListeners();

        listeners.Should().ContainKey("Mictrack");
        listeners.Should().ContainKey("GT06");
        listeners["Mictrack"].Should().NotBe(listeners["GT06"]);

        var profiles = ProtocolRegistry.CreateDefault().GetAllProfiles().ToList();
        profiles.Single(p => p.ProfileId == "mictrack-m558g-v2").TcpFamily.Should().Be("Mictrack");
        // El NT20 se decodifica con el GT06 estándar: sus propios ejemplos usan 0x12.
        profiles.Single(p => p.ProfileId == "x3tech-nt20").TcpFamily.Should().Be("GT06");
    }
}
