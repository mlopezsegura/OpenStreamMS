using System;
using System.IO;

namespace OpenStreamMS.Services
{
    public enum LogLevel
    {
        Info,
        Warning,
        Error
    }

    /// <summary>
    /// Nivel mínimo que se escribe. Global: lo fija el panel y vale para el servicio, la bandeja,
    /// los agentes de puesto y Sunshine. <see cref="Off"/> no escribe nada, ni los errores.
    /// </summary>
    public enum LogThreshold
    {
        Info,
        Warning,
        Error,
        Off
    }

    public static class Logger
    {
        private static readonly string GlobalLogFile = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "openstream.log");
        private static string _globalLogFile = GlobalLogFile;

        private static readonly object _lock = new();

        // ── Nivel mínimo ─────────────────────────────────────────────────────────
        // El servicio lo fija al arrancar y al cambiarlo en el panel, y lo publica en el registro
        // (HKLM\SOFTWARE\OpenStreamMS\LogLevel). Los demás procesos (bandeja, agentes y
        // presentadores de puesto) no pueden leer service.config.json: lo leen de ahí, como
        // mucho cada 10 s, así un cambio les llega sin reiniciarlos.
        private const string RegistryKey   = @"SOFTWARE\OpenStreamMS";
        private const string RegistryValue = "LogLevel";
        private static LogThreshold _threshold = LogThreshold.Info;
        private static bool _thresholdFixed;              // true en el servicio: manda su config
        private static long _thresholdReadAt = long.MinValue;

        /// <summary>Nivel mínimo actual.</summary>
        public static LogThreshold Threshold
        {
            get
            {
                if (!_thresholdFixed && Environment.TickCount64 - _thresholdReadAt > 10_000)
                {
                    _thresholdReadAt = Environment.TickCount64;
                    _threshold = ReadPublishedThreshold() ?? _threshold;
                }
                return _threshold;
            }
        }

        /// <summary>Fija el nivel en este proceso y lo publica para los demás (solo el servicio).</summary>
        public static void SetThreshold(LogThreshold threshold)
        {
            _threshold = threshold;
            _thresholdFixed = true;
            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.CreateSubKey(RegistryKey);
                key.SetValue(RegistryValue, threshold.ToString(), Microsoft.Win32.RegistryValueKind.String);
            }
            catch { /* sin permiso: los demás procesos se quedan con el último publicado */ }
        }

        /// <summary>Texto de configuración ("Info", "Warning", "Error", "Off") a nivel; null si no es válido.</summary>
        public static LogThreshold? ParseThreshold(string? value) =>
            Enum.TryParse<LogThreshold>(value?.Trim(), ignoreCase: true, out var t) && Enum.IsDefined(t) ? t : null;

        private static LogThreshold? ReadPublishedThreshold()
        {
            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(RegistryKey);
                return ParseThreshold(key?.GetValue(RegistryValue) as string);
            }
            catch { return null; }
        }

        private static bool Enabled(LogLevel level) => (int)level >= (int)Threshold;

        // AsyncLocal permite que cada tarea en segundo plano tenga su propio contexto de sesión,
        // haciendo que todas las llamadas a Log() dentro de esa tarea también escriban al log
        // específico de la sesión (incluidas las de RdpSessionCreator, SunshineManager, etc.).
        private static readonly AsyncLocal<Guid?> _sessionContext = new();

        public static void Log(string message, Guid? sessionId = null) =>
            Log(message, LogLevel.Info, sessionId);

        public static void Info(string message, Guid? sessionId = null) =>
            Log(message, LogLevel.Info, sessionId);

        public static void Warning(string message, Guid? sessionId = null) =>
            Log(message, LogLevel.Warning, sessionId);

        public static void Error(string message, Guid? sessionId = null) =>
            Log(message, LogLevel.Error, sessionId);

        public static void Log(string message, LogLevel level, Guid? sessionId = null)
        {
            if (!Enabled(level)) return;
            string line = $"[{DateTime.Now:HH:mm:ss.fff}] [{FormatLevel(level)}] {message}";
            if (level == LogLevel.Error)
                Console.Error.WriteLine(line);
            else
                Console.WriteLine(line);

            try
            {
                lock (_lock)
                {
                    try { AppendWithRotation(_globalLogFile, line + Environment.NewLine); }
                    catch (UnauthorizedAccessException) when (_globalLogFile == GlobalLogFile)
                    {
                        // Procesos en sesiones de usuario (agente de puesto, bandeja): no pueden
                        // escribir junto al exe en Program Files; usan el TEMP del usuario.
                        _globalLogFile = Path.Combine(Path.GetTempPath(), "openstream.log");
                        AppendWithRotation(_globalLogFile, line + Environment.NewLine);
                    }

                    var effectiveId = sessionId ?? _sessionContext.Value;
                    if (effectiveId.HasValue)
                        AppendWithRotation(SessionLogPath(effectiveId.Value), line + Environment.NewLine);
                }
            }
            catch { /* no bloquear si el disco falla */ }
        }

        private static string FormatLevel(LogLevel level) => level switch
        {
            LogLevel.Info => "INFO",
            LogLevel.Warning => "WARNING",
            LogLevel.Error => "ERROR",
            _ => level.ToString().ToUpperInvariant()
        };

        /// <summary>
        /// Establece el contexto de sesión para el flujo async actual.
        /// Todas las llamadas a Log() en este contexto también escribirán al log de sesión.
        /// </summary>
        public static void SetSessionContext(Guid? sessionId) => _sessionContext.Value = sessionId;

        /// <summary>
        /// Lee las últimas <paramref name="lines"/> líneas del log de la sesión indicada.
        /// </summary>
        public static string[] ReadSessionLogs(Guid sessionId, int lines = 200)
        {
            var path = SessionLogPath(sessionId);
            if (!File.Exists(path)) return [];
            return ReadTail(path, lines);
        }

        // Tope por fichero: al superarlo se renombra a .old (se conserva una generacion).
        private const long MaxLogBytes = 10 * 1024 * 1024;

        private static void AppendWithRotation(string path, string text)
        {
            var fi = new FileInfo(path);
            if (fi.Exists && fi.Length > MaxLogBytes)
            {
                try { File.Move(path, path + ".old", overwrite: true); }
                catch { /* otro proceso lo tiene abierto: se reintenta en la siguiente linea */ }
            }
            File.AppendAllText(path, text);
        }

        /// <summary>
        /// Lee las ultimas <paramref name="lines"/> lineas leyendo bloques desde el final,
        /// sin recorrer el fichero entero (el panel web lo consulta cada 2.5 s).
        /// </summary>
        private static string[] ReadTail(string path, int lines)
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            const int block = 64 * 1024;
            long start = fs.Length;
            int newlines = 0;
            var buf = new byte[block];

            // Retroceder hasta tener lines+1 saltos de linea (o llegar al principio).
            while (start > 0 && newlines <= lines)
            {
                int toRead = (int)Math.Min(block, start);
                start -= toRead;
                fs.Seek(start, SeekOrigin.Begin);
                fs.ReadExactly(buf, 0, toRead);
                for (int i = toRead - 1; i >= 0; i--)
                {
                    if (buf[i] != (byte)'\n') continue;
                    if (++newlines > lines) { start += i + 1; break; }
                }
            }

            fs.Seek(start, SeekOrigin.Begin);
            using var sr = new StreamReader(fs);
            var result = new List<string>(lines);
            while (sr.ReadLine() is { } l) result.Add(l);
            return result.Count > lines ? result.GetRange(result.Count - lines, lines).ToArray() : result.ToArray();
        }

        internal static string SessionLogPath(Guid sessionId) =>
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, $"openstream-{sessionId:N}.log");
    }
}
