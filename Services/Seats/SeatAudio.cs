using NAudio.CoreAudioApi;

namespace OpenStreamMS.Services.Seats;

/// <summary>Enumeración de dispositivos de audio (salidas y micrófonos) para asignarlos a un puesto.</summary>
public static class SeatAudio
{
    /// <summary>Micrófonos activos: (id MMDevice, nombre amigable).</summary>
    public static IReadOnlyList<(string Id, string Name)> Inputs()
    {
        try
        {
            using var mm = new MMDeviceEnumerator();
            return mm.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
                     .Select(d => (d.ID, d.FriendlyName))
                     .ToList();
        }
        catch (Exception ex)
        {
            Logger.Warning($"[Seats] No se pudieron enumerar los micrófonos: {ex.Message}");
            return [];
        }
    }

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
/// Medidor de nivel de un micrófono para el editor de puestos. Abre el micrófono en el servicio
/// (sesión 0, SYSTEM), que es donde también lo abre el cliente FreeRDP del puesto: si aquí no se
/// puede, el puesto tampoco tendrá micrófono. Cada consulta devuelve el pico desde la anterior;
/// sin consultas durante unos segundos, el micrófono se cierra.
/// </summary>
public static class MicMeter
{
    private sealed class Probe
    {
        public required WasapiCapture Capture;
        public float Peak;
        public long LastPoll = Environment.TickCount64;
    }

    private static readonly Dictionary<string, Probe> _probes = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object _lock = new();
    private static readonly System.Threading.Timer _reaper = new(_ => Reap(), null, 2000, 2000);
    private const int IdleMs = 4000;

    /// <summary>Pico 0..1 desde la consulta anterior. Lanza si el micrófono no se puede abrir.</summary>
    public static float Level(string endpointId)
    {
        lock (_lock)
        {
            if (!_probes.TryGetValue(endpointId, out var probe))
            {
                using var mm = new MMDeviceEnumerator();
                var dev = mm.GetDevice(endpointId);
                var cap = new WasapiCapture(dev, false, 50);
                probe = new Probe { Capture = cap };
                var p = probe;
                cap.DataAvailable += (_, e) =>
                {
                    float peak = PeakOf(cap.WaveFormat, e.Buffer, e.BytesRecorded);
                    lock (_lock) if (peak > p.Peak) p.Peak = peak;
                };
                cap.StartRecording();
                _probes[endpointId] = probe;
            }
            probe.LastPoll = Environment.TickCount64;
            float level = probe.Peak;
            probe.Peak = 0;
            return level;
        }
    }

    private static float PeakOf(NAudio.Wave.WaveFormat wf, byte[] buf, int bytes)
    {
        float peak = 0;
        if (wf.Encoding == NAudio.Wave.WaveFormatEncoding.IeeeFloat ||
            (wf.Encoding == NAudio.Wave.WaveFormatEncoding.Extensible && wf.BitsPerSample == 32))
        {
            for (int i = 0; i + 4 <= bytes; i += 4)
                peak = Math.Max(peak, Math.Abs(BitConverter.ToSingle(buf, i)));
        }
        else if (wf.BitsPerSample == 16)
        {
            for (int i = 0; i + 2 <= bytes; i += 2)
                peak = Math.Max(peak, Math.Abs(BitConverter.ToInt16(buf, i) / 32768f));
        }
        return Math.Min(1f, peak);
    }

    private static void Reap()
    {
        List<WasapiCapture> stale = [];
        lock (_lock)
        {
            foreach (var (id, p) in _probes.ToList())
                if (Environment.TickCount64 - p.LastPoll > IdleMs) { stale.Add(p.Capture); _probes.Remove(id); }
        }
        foreach (var c in stale)
        {
            try { c.StopRecording(); } catch { }
            c.Dispose();
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
