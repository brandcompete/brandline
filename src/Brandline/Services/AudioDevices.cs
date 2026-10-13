using NAudio.CoreAudioApi;

namespace Brandline.Services;

public sealed record AudioDevice(string Id, string Name);

/// <summary>
/// Windows-Audiogeräte über WASAPI. Gespeichert wird die stabile Geräte-ID; leer = Windows-Standardgerät
/// für Kommunikation (das ist i. d. R. das Headset).
/// </summary>
public static class AudioDevices
{
    public const string DefaultLabel = "(Windows-Standardgerät)";

    public static List<AudioDevice> Outputs() => List(DataFlow.Render);
    public static List<AudioDevice> Inputs() => List(DataFlow.Capture);

    private static List<AudioDevice> List(DataFlow flow)
    {
        using var enumerator = new MMDeviceEnumerator();
        var devices = enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active);
        var list = new List<AudioDevice>();
        for (var i = 0; i < devices.Count; i++)
        {
            using var device = devices[i];
            list.Add(new AudioDevice(device.ID, device.FriendlyName));
        }
        return list;
    }

    /// <summary>
    /// Öffnet das gespeicherte Gerät. Fehlt es (abgesteckt), wird das Standardgerät genommen.
    /// Akzeptiert auch alte Einstellungen, die noch den (auf 31 Zeichen gekürzten) WinMM-Namen enthalten.
    /// </summary>
    public static MMDevice Open(DataFlow flow, string stored)
    {
        using var enumerator = new MMDeviceEnumerator();
        var id = Resolve(flow, stored);
        if (id != null)
        {
            try
            {
                var device = enumerator.GetDevice(id);
                if (device.State == DeviceState.Active) return device;
                device.Dispose();
            }
            catch
            {
                // Gerät nicht mehr vorhanden.
            }
        }
        return enumerator.GetDefaultAudioEndpoint(flow, Role.Communications);
    }

    /// <summary>Position des Geräts in der Liste aktiver Geräte (benötigt die Windows-Echounterdrückung).</summary>
    public static int ActiveIndexOf(DataFlow flow, string deviceId) =>
        List(flow).FindIndex(d => d.Id == deviceId);

    /// <summary>Liefert die Geräte-ID zu einem gespeicherten Wert (ID oder alter Name), sonst null.</summary>
    public static string? Resolve(DataFlow flow, string stored)
    {
        if (string.IsNullOrEmpty(stored)) return null;
        var devices = List(flow);
        return devices.FirstOrDefault(d => d.Id == stored)?.Id
               ?? devices.FirstOrDefault(d => d.Name.StartsWith(stored, StringComparison.OrdinalIgnoreCase))?.Id;
    }
}
