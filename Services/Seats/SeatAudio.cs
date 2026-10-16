using NAudio.CoreAudioApi;

namespace OpenStreamMS.Services.Seats;

/// <summary>Enumeración de dispositivos de salida de audio para asignarlos a un puesto.</summary>
public static class SeatAudio
{
    /// <summary>Dispositivos de salida activos: (id MMDevice, nombre amigable).</summary>
    public static IReadOnlyList<(string Id, string Name)> Outputs()
    {
        try
        {
            using var mm = new MMDeviceEnumerator();
            return mm.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
                     .Select(d => (d.ID, d.FriendlyName))
                     .ToList();
        }
        catch (Exception ex)
        {
            Logger.Warning($"[Seats] No se pudieron enumerar las salidas de audio: {ex.Message}");
            return [];
        }
    }
}
