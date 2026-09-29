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

    public static class Logger
    {
        private static readonly string GlobalLogFile = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "openstream.log");

        private static readonly object _lock = new();

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
            string line = $"[{DateTime.Now:HH:mm:ss.fff}] [{FormatLevel(level)}] {message}";
            if (level == LogLevel.Error)
                Console.Error.WriteLine(line);
            else
                Console.WriteLine(line);

            try
            {
                lock (_lock)
                {
                    AppendWithRotation(GlobalLogFile, line + Environment.NewLine);

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
