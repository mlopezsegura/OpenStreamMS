using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using OpenStreamMS.Core.Api;
using Timer = System.Threading.Timer;

namespace OpenStreamMS.Services.Metrics;

/// <summary>Uso del equipo y de cada sesión en marcha, para el panel.</summary>
/// <param name="Cpu">CPU total en %.</param>
/// <param name="Gpu">Motor 3D más cargado de la GPU en % (como el Administrador de tareas).</param>
/// <param name="GpuEncode">Codificador de vídeo de la GPU en %.</param>
public sealed record MetricsDto(double? Cpu, double? Gpu, double? GpuEncode, double MemUsedGb, double MemTotalGb,
                                Dictionary<Guid, SessionMetricsDto> Sessions);

/// <param name="Cpu">CPU de todos los procesos de la sesión de Windows, en % del equipo.</param>
/// <param name="Gpu">GPU 3D de la sesión (el juego) en %.</param>
/// <param name="GpuEncode">Codificador de vídeo usado por la sesión (Sunshine) en %.</param>
/// <param name="Encoder">Encoder de Sunshine (h264_nvenc, hevc_amf, libx264…); null si aún no se sabe.</param>
/// <param name="EncoderFamily">nvenc, quicksync, amdvce, software…</param>
/// <param name="Software">Codifica por CPU: el stream irá a tirones con resoluciones altas.</param>
/// <param name="EncoderActive">El encoder es el del stream en curso (no solo el detectado al arrancar).</param>
public sealed record SessionMetricsDto(double? Cpu, double? Gpu, double? GpuEncode,
                                       string? Encoder, string? EncoderFamily, bool Software, bool EncoderActive);

/// <summary>
/// Muestrea cada 2 s CPU, GPU y memoria, pero solo mientras alguien consulta <c>/api/metrics</c>
/// (panel abierto y visible): a los 20 s sin consultas se para. El panel se abre en el mismo PC
/// que codifica el vídeo, así que no se gasta nada si nadie mira.
/// </summary>
public sealed class SystemMetrics(StreamSessionService sessions) : IDisposable
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan IdleStop = TimeSpan.FromSeconds(20);

    private readonly object _lock = new();
    private Timer? _timer;
    private DateTime _lastRequest;
    private MetricsDto _last = new(null, null, null, 0, 0, []);

    private readonly Pdh _pdh = new();
    private readonly Dictionary<int, (DateTime Start, TimeSpan Cpu)> _procCpu = [];
    private DateTime _lastSample;
    private readonly Dictionary<Guid, (long Size, DateTime Write, EncoderInfo Info)> _encoders = [];

    /// <summary>Últimos valores; arranca el muestreo si estaba parado.</summary>
    public MetricsDto Get()
    {
        lock (_lock)
        {
            _lastRequest = DateTime.UtcNow;
            if (_timer is null)
            {
                _timer = new Timer(_ => Sample(), null, TimeSpan.Zero, Interval);
                Logger.Log("[Metrics] Muestreo de uso iniciado (panel abierto).");
            }
            return _last;
        }
    }

    private void Sample()
    {
        lock (_lock)
        {
            if (DateTime.UtcNow - _lastRequest > IdleStop)
            {
                _timer?.Dispose();
                _timer = null;
                _procCpu.Clear();
                Logger.Log("[Metrics] Muestreo de uso parado (nadie mira el panel).");
                return;
            }
        }
        try
        {
            var now = DateTime.UtcNow;
            double elapsed = _lastSample == default ? 0 : (now - _lastSample).TotalMilliseconds;
            _lastSample = now;

            // Procesos: CPU (diferencia de tiempo de CPU) y sesión de Windows de cada uno.
            var pidSession = new Dictionary<int, int>();
            var cpuBySession = new Dictionary<int, double>();
            var seen = new HashSet<int>();
            foreach (var p in Process.GetProcesses())
            {
                using (p)
                {
                    try
                    {
                        pidSession[p.Id] = p.SessionId;
                        var start = p.StartTime;
                        var cpu = p.TotalProcessorTime;
                        seen.Add(p.Id);
                        if (_procCpu.TryGetValue(p.Id, out var prev) && prev.Start == start && elapsed > 0)
                        {
                            var pct = (cpu - prev.Cpu).TotalMilliseconds / elapsed / Environment.ProcessorCount * 100;
                            cpuBySession[p.SessionId] = cpuBySession.GetValueOrDefault(p.SessionId) + Math.Max(0, pct);
                        }
                        _procCpu[p.Id] = (start, cpu);
                    }
                    catch { /* proceso protegido o que acaba de salir */ }
                }
            }
            foreach (var pid in _procCpu.Keys.Where(k => !seen.Contains(k)).ToList()) _procCpu.Remove(pid);

            // GPU: motores por proceso (pid_N_..._eng_M_engtype_X).
            var engines = _pdh.GpuEngines();
            double? Engine(Func<string, bool> type, Func<int, bool> pidFilter)
            {
                if (engines is null) return null;
                var perEngine = engines.Where(e => type(e.Type) && pidFilter(e.Pid))
                                       .GroupBy(e => e.Engine).Select(g => g.Sum(e => e.Value));
                return Math.Min(100, perEngine.DefaultIfEmpty(0).Max());
            }

            var result = new Dictionary<Guid, SessionMetricsDto>();
            foreach (var s in sessions.GetAll().Where(s => s.State == SessionState.Running))
            {
                int winSession = s.RdpSessionId > 0 ? (int)s.RdpSessionId
                               : s.SunshinePid > 0 && pidSession.TryGetValue(s.SunshinePid, out var ws) ? ws : -1;
                if (winSession < 0) continue;
                bool InSession(int pid) => pidSession.TryGetValue(pid, out var sid) && sid == winSession;
                var enc = Encoder(s.Id);
                result[s.Id] = new SessionMetricsDto(
                    elapsed > 0 ? Round(Math.Min(100, cpuBySession.GetValueOrDefault(winSession))) : null,
                    Round(Engine(Is3D, InSession)),
                    Round(Engine(IsEncode, InSession)),
                    enc.Name, enc.Family, enc.Software, enc.Active);
            }

            var mem = Memory();
            var dto = new MetricsDto(Round(_pdh.CpuTotal()), Round(Engine(Is3D, _ => true)),
                                     Round(Engine(IsEncode, _ => true)), mem.Used, mem.Total, result);
            lock (_lock) _last = dto;
        }
        catch (Exception ex)
        {
            Logger.Warning($"[Metrics] No se pudo medir el uso: {ex.Message}");
        }
    }

    private static bool Is3D(string type) => type == "3D";
    /// <summary>NVIDIA e Intel: "VideoEncode"; AMD: "Video Codec 0" (codifica y descodifica).</summary>
    private static bool IsEncode(string type) =>
        type.Contains("Encode", StringComparison.OrdinalIgnoreCase) ||
        type.StartsWith("Video Codec", StringComparison.OrdinalIgnoreCase);

    private static double? Round(double? v) => v is { } x ? Math.Round(x, 1) : null;

    // ── Encoder de Sunshine (de su log) ───────────────────────────────────────

    private sealed record EncoderInfo(string? Name, string? Family, bool Software, bool Active);

    private static readonly Regex FoundRx  = new(@"Found (?:H\.264|HEVC|AV1) encoder: (\S+) \[(\w+)\]", RegexOptions.Compiled);
    private static readonly Regex CreateRx = new(@"Creating encoder \[(\S+)\]", RegexOptions.Compiled);

    /// <summary>
    /// Al arrancar, Sunshine prueba encoders ("Creating encoder") y anota los que valen ("Found
    /// … encoder"). Un "Creating encoder" posterior a esas líneas es el del stream en curso.
    /// </summary>
    private EncoderInfo Encoder(Guid sessionId)
    {
        var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "sessions", sessionId.ToString(),
                                "Sunshine", "config", "sunshine.log");
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists) return new(null, null, false, false);
            if (_encoders.TryGetValue(sessionId, out var c) && c.Size == fi.Length && c.Write == fi.LastWriteTimeUtc)
                return c.Info;

            string text;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                fs.Seek(Math.Max(0, fs.Length - 256 * 1024), SeekOrigin.Begin);
                using var sr = new StreamReader(fs);
                text = sr.ReadToEnd();
            }

            string? name = null, family = null;
            bool active = false;
            int lastFound = -1;
            foreach (Match m in FoundRx.Matches(text)) { name = m.Groups[1].Value; family = m.Groups[2].Value; lastFound = m.Index; }
            foreach (Match m in CreateRx.Matches(text))
                if (m.Index > lastFound && lastFound >= 0) { name = m.Groups[1].Value; active = true; }
            if (name is not null && family is null) family = FamilyOf(name);
            if (active) family = FamilyOf(name!) ?? family;
            bool software = family == "software" || name is not null && name.StartsWith("lib", StringComparison.OrdinalIgnoreCase);

            var info = new EncoderInfo(name, family, software, active);
            _encoders[sessionId] = (fi.Length, fi.LastWriteTimeUtc, info);
            return info;
        }
        catch { return new(null, null, false, false); }
    }

    private static string? FamilyOf(string name) =>
        name.Contains("nvenc", StringComparison.OrdinalIgnoreCase) ? "nvenc" :
        name.Contains("qsv", StringComparison.OrdinalIgnoreCase) ? "quicksync" :
        name.Contains("amf", StringComparison.OrdinalIgnoreCase) ? "amdvce" :
        name.Contains("mf", StringComparison.OrdinalIgnoreCase) ? "mediafoundation" :
        name.StartsWith("lib", StringComparison.OrdinalIgnoreCase) ? "software" : null;

    // ── Memoria ───────────────────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);

    private static (double Used, double Total) Memory()
    {
        var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!GlobalMemoryStatusEx(ref m)) return (0, 0);
        const double gb = 1024d * 1024 * 1024;
        return (Math.Round((m.ullTotalPhys - m.ullAvailPhys) / gb, 1), Math.Round(m.ullTotalPhys / gb, 1));
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _pdh.Dispose();
    }

    // ── Contadores de rendimiento (PDH) ───────────────────────────────────────

    private sealed record GpuEngineSample(int Pid, string Engine, string Type, double Value);

    /// <summary>Consulta PDH con nombres en inglés (vale en Windows de cualquier idioma).</summary>
    private sealed class Pdh : IDisposable
    {
        [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern uint PdhOpenQueryW(string? src, IntPtr user, out IntPtr query);
        [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern uint PdhAddEnglishCounterW(IntPtr query, string path, IntPtr user, out IntPtr counter);
        [DllImport("pdh.dll")] static extern uint PdhCollectQueryData(IntPtr query);
        [DllImport("pdh.dll")] static extern uint PdhCloseQuery(IntPtr query);
        [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
        static extern uint PdhGetFormattedCounterArrayW(IntPtr counter, uint format, ref uint size, out uint count, IntPtr buffer);
        [DllImport("pdh.dll")]
        static extern uint PdhGetFormattedCounterValue(IntPtr counter, uint format, out uint type, out FmtValue value);

        [StructLayout(LayoutKind.Sequential)]
        private struct FmtValue { public uint CStatus; public double Value; }
        [StructLayout(LayoutKind.Sequential)]
        private struct FmtItem { public IntPtr Name; public FmtValue Value; }

        const uint PDH_FMT_DOUBLE = 0x200, PDH_FMT_NOCAP100 = 0x8000, PDH_MORE_DATA = 0x800007D2;
        const uint PDH_CSTATUS_VALID_DATA = 0, PDH_CSTATUS_NEW_DATA = 1;

        private static readonly Regex EngineRx = new(@"^pid_(\d+)_luid_(\w+)_(\w+)_phys_(\d+)_eng_(\d+)_engtype_(.+)$", RegexOptions.Compiled);

        private IntPtr _query, _cpu, _gpu;
        private bool _ok, _collected;

        public Pdh()
        {
            try
            {
                _ok = PdhOpenQueryW(null, IntPtr.Zero, out _query) == 0;
                if (!_ok) return;
                if (PdhAddEnglishCounterW(_query, @"\Processor Information(_Total)\% Processor Utility", IntPtr.Zero, out _cpu) != 0 &&
                    PdhAddEnglishCounterW(_query, @"\Processor(_Total)\% Processor Time", IntPtr.Zero, out _cpu) != 0)
                    _cpu = IntPtr.Zero;
                // Sin el contador (Windows sin WDDM 2.x o GPU sin driver) no hay datos de GPU.
                if (PdhAddEnglishCounterW(_query, @"\GPU Engine(*)\Utilization Percentage", IntPtr.Zero, out _gpu) != 0)
                    _gpu = IntPtr.Zero;
            }
            catch (Exception ex)
            {
                _ok = false;
                Logger.Warning($"[Metrics] Contadores de rendimiento no disponibles: {ex.Message}");
            }
        }

        private void Collect()
        {
            if (_collected) return;
            PdhCollectQueryData(_query);
            _collected = true;
        }

        public double? CpuTotal()
        {
            if (!_ok || _cpu == IntPtr.Zero) return null;
            Collect();
            _collected = false;   // la siguiente lectura (en el próximo muestreo) vuelve a recoger
            if (PdhGetFormattedCounterValue(_cpu, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, out _, out var v) != 0) return null;
            return v.CStatus is PDH_CSTATUS_VALID_DATA or PDH_CSTATUS_NEW_DATA ? Math.Min(100, v.Value) : null;
        }

        public List<GpuEngineSample>? GpuEngines()
        {
            if (!_ok || _gpu == IntPtr.Zero) return null;
            Collect();
            uint size = 0;
            uint rc = PdhGetFormattedCounterArrayW(_gpu, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, ref size, out _, IntPtr.Zero);
            if (rc != PDH_MORE_DATA || size == 0) return [];
            var buf = Marshal.AllocHGlobal((int)size);
            try
            {
                if (PdhGetFormattedCounterArrayW(_gpu, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, ref size, out var count, buf) != 0) return [];
                var list = new List<GpuEngineSample>((int)count);
                int itemSize = Marshal.SizeOf<FmtItem>();
                for (int i = 0; i < count; i++)
                {
                    var item = Marshal.PtrToStructure<FmtItem>(buf + i * itemSize);
                    if (item.Value.CStatus is not (PDH_CSTATUS_VALID_DATA or PDH_CSTATUS_NEW_DATA)) continue;
                    var name = Marshal.PtrToStringUni(item.Name);
                    if (name is null || EngineRx.Match(name) is not { Success: true } m) continue;
                    list.Add(new GpuEngineSample(int.Parse(m.Groups[1].Value),
                        $"{m.Groups[2].Value}_{m.Groups[3].Value}_{m.Groups[4].Value}_{m.Groups[5].Value}",
                        m.Groups[6].Value, item.Value.Value));
                }
                return list;
            }
            finally { Marshal.FreeHGlobal(buf); }
        }

        public void Dispose()
        {
            if (_query != IntPtr.Zero) PdhCloseQuery(_query);
            _query = IntPtr.Zero;
        }
    }
}
