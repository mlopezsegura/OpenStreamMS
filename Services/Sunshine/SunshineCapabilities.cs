using System.Collections.Concurrent;
using System.Text;

namespace OpenStreamMS.Services.Sunshine
{
    /// <summary>
    /// Detecta qué sabe hacer un <c>sunshine.exe</c> concreto. Hoy solo una cosa: si es la build de
    /// sunshine-webrtc, que entiende <c>stream_protocol</c> y los puertos de media WebRTC.
    /// </summary>
    public static class SunshineCapabilities
    {
        // Nombre de opción que solo existe en sunshine-webrtc con selector de protocolo.
        private static readonly byte[] WebRtcMarker = Encoding.ASCII.GetBytes("webrtc_media_port_min");

        private static readonly ConcurrentDictionary<string, (long Length, DateTime WriteUtc, bool Supported)> _cache =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// true si el ejecutable trae el servidor WebRTC con selector de protocolo. El resultado se
        /// cachea por ruta, tamaño y fecha, así que reemplazar el binario se detecta solo.
        /// </summary>
        public static bool SupportsWebRtc(string? sunshineExePath)
        {
            if (string.IsNullOrWhiteSpace(sunshineExePath)) return false;
            try
            {
                var info = new FileInfo(sunshineExePath);
                if (!info.Exists) return false;

                if (_cache.TryGetValue(info.FullName, out var cached) &&
                    cached.Length == info.Length && cached.WriteUtc == info.LastWriteTimeUtc)
                    return cached.Supported;

                var supported = ContainsMarker(info.FullName);
                _cache[info.FullName] = (info.Length, info.LastWriteTimeUtc, supported);
                return supported;
            }
            catch
            {
                return false;
            }
        }

        private static bool ContainsMarker(string path)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                                              1 << 20, FileOptions.SequentialScan);
            var buffer  = new byte[1 << 20];
            var overlap = WebRtcMarker.Length - 1;
            var filled  = 0;
            int read;
            while ((read = stream.Read(buffer, filled, buffer.Length - filled)) > 0)
            {
                var length = filled + read;
                if (buffer.AsSpan(0, length).IndexOf(WebRtcMarker) >= 0)
                    return true;

                // Conservar la cola por si el marcador cae entre dos lecturas.
                var keep = Math.Min(overlap, length);
                Buffer.BlockCopy(buffer, length - keep, buffer, 0, keep);
                filled = keep;
            }
            return false;
        }
    }
}
