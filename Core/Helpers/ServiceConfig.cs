using System;
using System.IO;
using System.Text.Json;

namespace OpenStreamMS.Core.Helpers
{
    public class StreamUserConfig
    {
        public string Username { get; set; } = "";
        public string Domain   { get; set; } = ".";
        public string Password { get; set; } = "";
    }

    public class ServiceConfig
    {
        public StreamUserConfig StreamUser   { get; set; } = new();
        public string SunshineExePath        { get; set; } = "";
        /// <summary>true → mstsc se lanza minimizado/oculto.</summary>
        public bool RdpBackground            { get; set; } = true;
        /// <summary>true → parchea apps.json de Sunshine con virtual-display.</summary>
        public bool VddEnabled               { get; set; } = false;
        /// <summary>
        /// Cliente RDP para crear la sesion: "freerdp" (recomendado) o "mstsc".
        /// mstsc NO funciona lanzado desde el servicio (Session 0 es no-interactiva
        /// desde Vista: mstsc es GUI, se queda colgado sin window station y agota el
        /// timeout de 30 s antes de caer a FreeRDP). wfreerdp es consola y corre
        /// headless en Session 0. Requiere un wfreerdp con H264/AVC444 (build con
        /// WITH_MEDIA_FOUNDATION=ON) para decodificar por GPU; el binario antiguo sin
        /// H264 forzaba RemoteFX progressive por CPU y a 4K oscilaba 60→8 fps.
        /// </summary>
        public string RdpClient              { get; set; } = "freerdp";
        /// <summary>Puerto en que escucha la API REST (defecto: 5000).</summary>
        public int ApiPort                   { get; set; } = 5000;

        // ── Autenticación ────────────────────────────────────────────────────
        /// <summary>
        /// Nombre de usuario del administrador de la web/API.
        /// Vacío = primera ejecución, se solicitará configuración inicial.
        /// </summary>
        public string AdminUsername          { get; set; } = "";
        /// <summary>Hash SHA-256 de "username:password" (minúsculas).</summary>
        public string AdminPasswordHash      { get; set; } = "";
        /// <summary>Clave secreta HMAC para firmar los tokens de sesión.</summary>
        public string AuthSecret             { get; set; } = "";

        private static readonly JsonSerializerOptions JsonOpts =
            new() { WriteIndented = true };

        private static readonly string ConfigFile =
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "service.config.json");

        public static ServiceConfig Load()
        {
            if (!File.Exists(ConfigFile))
            {
                var def = new ServiceConfig
                {
                    StreamUser = new StreamUserConfig
                    {
                        Username = "streamuser",
                        Domain   = ".",
                        Password = "ChangeMe123!"
                    },
                    SunshineExePath = Path.Combine(
                        AppDomain.CurrentDomain.BaseDirectory, "Sunshine", "sunshine.exe"),
                    RdpBackground = true,
                    VddEnabled    = false,
                };
                File.WriteAllText(ConfigFile, JsonSerializer.Serialize(def, JsonOpts));
                Console.WriteLine($"[Config] Creado config por defecto en: {ConfigFile}");
                return def;
            }

            var cfg = JsonSerializer.Deserialize<ServiceConfig>(File.ReadAllText(ConfigFile))
                      ?? throw new InvalidOperationException("Config inválida");

            if (!Path.IsPathRooted(cfg.SunshineExePath))
                cfg.SunshineExePath = Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory, cfg.SunshineExePath);


            return cfg;
        }

        /// <summary>Persiste la configuración actual en service.config.json.</summary>
        public void Save()
        {
            File.WriteAllText(ConfigFile, JsonSerializer.Serialize(this, JsonOpts));
        }
    }
}
