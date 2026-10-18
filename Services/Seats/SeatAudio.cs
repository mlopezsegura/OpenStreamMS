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

/// <summary>
/// Búfer anti-cortes para el audio que llega de la sesión por la tubería. Los paquetes llegan
/// cada ~10 ms con algo de variación; reproducirlos en cuanto llegan deja el búfer vacío a
/// menudo y cada hueco suena como un "pop". Aquí se espera a tener <c>targetMs</c> antes de
/// sonar, un vaciado se suaviza con un fundido y se vuelve a llenar, y si se acumula retraso
/// (los relojes de la sesión y de la tarjeta no van exactamente igual) se descarta lo que sobra.
/// </summary>
internal sealed class SeatJitterBuffer : NAudio.Wave.IWaveProvider
{
    private readonly NAudio.Wave.BufferedWaveProvider _buf;
    private readonly int _target, _max, _block;
    private readonly bool _float;
    private volatile bool _playing;
    private bool _fadeIn;

    public int Underruns, Drops;

    public SeatJitterBuffer(NAudio.Wave.WaveFormat format, int targetMs = 30, int maxMs = 100)
    {
        _buf = new NAudio.Wave.BufferedWaveProvider(format)
        {
            BufferDuration = TimeSpan.FromSeconds(1),
            DiscardOnBufferOverflow = true,
            ReadFully = false,
        };
        _block = format.BlockAlign;
        _target = Align(format.AverageBytesPerSecond * targetMs / 1000);
        _max = Align(format.AverageBytesPerSecond * maxMs / 1000);
        _float = format.Encoding == NAudio.Wave.WaveFormatEncoding.IeeeFloat && format.BitsPerSample == 32;
    }

    public NAudio.Wave.WaveFormat WaveFormat => _buf.WaveFormat;

    private int Align(int bytes) => bytes / _block * _block;

    public void Add(byte[] data)
    {
        _buf.AddSamples(data, 0, data.Length);
        int excess = _buf.BufferedBytes - _max;
        if (excess > 0)
        {
            // Volver al objetivo de golpe: un solo salto en vez de ir arrastrando retraso.
            var trash = new byte[Align(_buf.BufferedBytes - _target)];
            _buf.Read(trash, 0, trash.Length);
            Drops++;
        }
    }

    public int Read(byte[] buffer, int offset, int count)
    {
        if (!_playing)
        {
            if (_buf.BufferedBytes < _target) { Array.Clear(buffer, offset, count); return count; }
            _playing = true;
            _fadeIn = true;
        }

        int n = _buf.Read(buffer, offset, count);
        if (_fadeIn) { Fade(buffer, offset, n, rising: true); _fadeIn = false; }
        if (n < count)
        {
            // Vacío: fundido de salida de lo que quedaba (sin chasquido) y a rellenar otra vez.
            Fade(buffer, offset, n, rising: false);
            Array.Clear(buffer, offset + n, count - n);
            _playing = false;
            Underruns++;
        }
        return count;
    }

    /// <summary>Rampa lineal sobre (como mucho) los primeros/últimos 5 ms del bloque.</summary>
    private void Fade(byte[] buffer, int offset, int bytes, bool rising)
    {
        if (!_float || bytes <= 0) return;
        int channels = WaveFormat.Channels;
        var samples = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(buffer.AsSpan(offset, bytes / 4 * 4));
        int frames = samples.Length / channels;
        int ramp = Math.Min(frames, WaveFormat.SampleRate / 200);
        if (ramp == 0) return;
        for (int f = 0; f < ramp; f++)
        {
            int frame = rising ? f : frames - ramp + f;
            float gain = rising ? (float)f / ramp : 1f - (float)(f + 1) / ramp;
            for (int c = 0; c < channels; c++) samples[frame * channels + c] *= gain;
        }
    }
}
