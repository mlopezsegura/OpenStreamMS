using OpenStreamMS.Core.Helpers;
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenStreamMS.Services.Sunshine
{
    /// <summary>
    /// Parchea la configuración de Sunshine (apps.json, sunshine.conf) antes del arranque.
    /// </summary>
    public static class SunshineConfigurator
    {
        private static readonly JsonSerializerOptions _writeOpts = new()
        {
            WriteIndented = true
        };

        /// <summary>
        /// Asegura que todas las apps en el config/apps.json de Sunshine tengan
        /// <c>virtual-display: true</c>. Crea el archivo si no existe.
        /// </summary>
        public static void EnableVirtualDisplay(string sunshineExePath)
        {
            string configDir  = Path.Combine(Path.GetDirectoryName(sunshineExePath)!, "config");
            string appsFile   = Path.Combine(configDir, "apps.json");
            string assetsFile = Path.Combine(Path.GetDirectoryName(sunshineExePath)!, "assets", "apps.json");

            Directory.CreateDirectory(configDir);

            JsonObject root;

            if (File.Exists(appsFile))
            {
                root = JsonNode.Parse(File.ReadAllText(appsFile))?.AsObject()
                       ?? new JsonObject();
            }
            else if (File.Exists(assetsFile))
            {
                root = JsonNode.Parse(File.ReadAllText(assetsFile))?.AsObject()
                       ?? new JsonObject();
                Logger.Log("[SunshineCfg] apps.json no existe, usando template de assets");
            }
            else
            {
                root = new JsonObject
                {
                    ["env"]  = new JsonObject(),
                    ["apps"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["name"]            = "Desktop",
                            ["image-path"]      = "desktop.png",
                            ["virtual-display"] = true
                        }
                    }
                };
                File.WriteAllText(appsFile, root.ToJsonString(_writeOpts));
                Logger.Log($"[SunshineCfg] apps.json creado con virtual-display en: {appsFile}");
                return;
            }

            bool modified = false;
            if (root["apps"] is JsonArray apps)
            {
                foreach (var node in apps)
                {
                    if (node is not JsonObject app) continue;
                    if (app["virtual-display"]?.GetValue<bool>() != true)
                    {
                        app["virtual-display"] = true;
                        modified = true;
                    }
                }
            }

            if (modified)
            {
                File.WriteAllText(appsFile, root.ToJsonString(_writeOpts));
                Logger.Log($"[SunshineCfg] apps.json actualizado con virtual-display en: {appsFile}");
            }
            else
            {
                Logger.Log("[SunshineCfg] apps.json ya tiene virtual-display configurado, sin cambios");
            }
        }

        /// <summary>
        /// Undo de Steam Big Picture que no bloquea a Sunshine. El original
        /// (<c>steam://close/bigpicture</c>) arranca Steam de nuevo si Sunshine ya lo ha
        /// matado al cerrar la app, y Sunshine espera a que ese steam.exe termine: nunca
        /// lo hace y la instancia queda colgada. Start-Process vuelve al instante, y solo
        /// se lanza si hay un steam.exe en la misma sesión de Windows que Sunshine (no
        /// toca el Steam del host ni el de otras sesiones de stream).
        /// </summary>
        private const string SafeSteamCloseUndo =
            "powershell -NoProfile -NonInteractive -WindowStyle Hidden -Command \"if (Get-Process steam -ErrorAction SilentlyContinue | Where-Object SessionId -eq ([Diagnostics.Process]::GetCurrentProcess().SessionId)) { Start-Process 'steam://close/bigpicture' }\"";

        private const string SteamOpenBigPicture = "steam://open/bigpicture";

        /// <summary>
        /// Ajusta la entrada de Steam Big Picture en <c>apps.json</c>: la abre a través de
        /// <c>steam\osms-steam.exe</c> (si está instalado) y con undo no bloqueante. En una
        /// sesión aislada ese lanzador inyecta osms-steamhook.dll en steam.exe para que su
        /// detección de instancia única (evento Global\ y valores de HKLM) quede dentro de la
        /// sesión: así no cierra ni recibe órdenes del Steam del host. Usa el IPC por defecto
        /// (con <c>-master_ipc_name_override</c> Big Picture no recibe el mando). Idempotente.
        /// </summary>
        public static void ConfigureSteamEntry(string sunshineExePath)
        {
            string appsFile = Path.Combine(Path.GetDirectoryName(sunshineExePath)!, "config", "apps.json");
            if (!File.Exists(appsFile))
                return;

            string launcher = Path.Combine(AppContext.BaseDirectory, "steam", "osms-steam.exe");
            string cmd = File.Exists(launcher) ? $"\"{launcher}\" {SteamOpenBigPicture}" : SteamOpenBigPicture;
            const string undo = SafeSteamCloseUndo;

            JsonObject root = JsonNode.Parse(File.ReadAllText(appsFile))?.AsObject() ?? new JsonObject();
            if (root["apps"] is not JsonArray apps)
                return;

            // Elimina la entrada heredada de versiones anteriores (ya no soportada)
            bool modified = false;
            for (int i = apps.Count - 1; i >= 0; i--)
            {
                if (apps[i] is JsonObject old &&
                    string.Equals(old["name"]?.GetValue<string>(), "Steam Big Picture (Sandboxed)", StringComparison.Ordinal))
                {
                    apps.RemoveAt(i);
                    modified = true;
                }
            }

            foreach (var node in apps)
            {
                if (node is not JsonObject app) continue;
                string? appCmd = app["cmd"]?.GetValue<string>();
                if (appCmd is null || !appCmd.Contains(SteamOpenBigPicture, StringComparison.OrdinalIgnoreCase)) continue;

                if (appCmd != cmd)
                {
                    app["cmd"] = cmd;
                    modified = true;
                }

                if (app["prep-cmd"] is not JsonArray prepCmds) continue;
                foreach (var cmdNode in prepCmds)
                {
                    if (cmdNode is not JsonObject prep) continue;
                    string? prepUndo = prep["undo"]?.GetValue<string>();
                    if (prepUndo is null || prepUndo == undo) continue;
                    if (!prepUndo.Contains("steam://close/bigpicture", StringComparison.OrdinalIgnoreCase)) continue;
                    prep["undo"] = undo;
                    modified = true;
                }
            }

            if (modified)
            {
                File.WriteAllText(appsFile, root.ToJsonString(_writeOpts));
                Logger.Log($"[SunshineCfg] Steam Big Picture configurado ('{cmd}') con undo no bloqueante: {appsFile}");
            }
        }

        /// <summary>
        /// Añade a <c>apps.json</c> las apps "Reboot" y "Power Off": lanzan
        /// <c>Scripts\osms-power.ps1</c>, que pide al servicio un apagado/reinicio forzado
        /// (el usuario de la sesión no puede apagar con otros usuarios conectados).
        /// Rutas absolutas a la instalación actual; las reescribe si cambian. Idempotente.
        /// </summary>
        public static void ConfigurePowerEntries(string sunshineExePath)
        {
            string appsFile = Path.Combine(Path.GetDirectoryName(sunshineExePath)!, "config", "apps.json");
            string script   = Path.Combine(AppContext.BaseDirectory, "Scripts", "osms-power.ps1");
            string assets   = Path.Combine(AppContext.BaseDirectory, "Sunshine", "assets");
            if (!File.Exists(appsFile) || !File.Exists(script))
                return;

            JsonObject root = JsonNode.Parse(File.ReadAllText(appsFile))?.AsObject() ?? new JsonObject();
            if (root["apps"] is not JsonArray apps)
                return;

            bool modified = false;
            foreach (var (name, action, image) in new[]
            {
                ("Reboot",    "restart",  "reboot.png"),
                ("Power Off", "shutdown", "power-off.png"),
            })
            {
                string cmd = $"powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{script}\" -Action {action}";
                string imagePath = Path.Combine(assets, image);

                var app = apps.OfType<JsonObject>()
                              .FirstOrDefault(a => string.Equals(a["name"]?.GetValue<string>(), name, StringComparison.Ordinal));
                if (app is null)
                {
                    apps.Add(app = new JsonObject { ["name"] = name });
                    modified = true;
                }
                if (app["cmd"]?.GetValue<string>() != cmd)              { app["cmd"] = cmd;              modified = true; }
                if (app["image-path"]?.GetValue<string>() != imagePath) { app["image-path"] = imagePath; modified = true; }
                if (app["auto-detach"]?.GetValue<bool>() != true)       { app["auto-detach"] = true;     modified = true; }
            }

            if (modified)
            {
                File.WriteAllText(appsFile, root.ToJsonString(_writeOpts));
                Logger.Log($"[SunshineCfg] Apps Reboot y Power Off configuradas: {appsFile}");
            }
        }

        /// <summary>
        /// Escribe en apps.json las variables de entorno gestionadas por OpenStreamMS.
        /// Sunshine las aplica a los comandos que lanza, reforzando la herencia del
        /// entorno del proceso sunshine.exe.
        /// </summary>
        public static void ConfigureAppEnvironment(
            string sunshineExePath,
            IReadOnlyDictionary<string, string>? envOverrides)
        {
            string configDir = Path.Combine(Path.GetDirectoryName(sunshineExePath)!, "config");
            string appsFile = Path.Combine(configDir, "apps.json");
            string assetsFile = Path.Combine(Path.GetDirectoryName(sunshineExePath)!, "assets", "apps.json");

            bool hasOverrides = envOverrides is { Count: > 0 };
            if (!File.Exists(appsFile) && !hasOverrides)
                return;

            Directory.CreateDirectory(configDir);

            JsonObject root;
            if (File.Exists(appsFile))
            {
                root = JsonNode.Parse(File.ReadAllText(appsFile))?.AsObject()
                       ?? new JsonObject();
            }
            else if (File.Exists(assetsFile))
            {
                root = JsonNode.Parse(File.ReadAllText(assetsFile))?.AsObject()
                       ?? new JsonObject();
            }
            else
            {
                root = new JsonObject
                {
                    ["env"] = new JsonObject(),
                    ["apps"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["name"] = "Desktop",
                            ["image-path"] = "desktop.png"
                        }
                    }
                };
            }

            bool modified = false;
            if (root["env"] is not JsonObject envObj)
            {
                envObj = new JsonObject();
                root["env"] = envObj;
                modified = true;
            }

            foreach (var key in StreamProfileSetup.ManagedEnvironmentKeys)
            {
                if (hasOverrides &&
                    envOverrides!.TryGetValue(key, out var value) &&
                    !string.IsNullOrEmpty(value))
                {
                    string? current = null;
                    if (envObj[key] is JsonValue currentValue &&
                        currentValue.TryGetValue<string>(out var currentString))
                        current = currentString;

                    if (!string.Equals(current, value, StringComparison.Ordinal))
                    {
                        envObj[key] = value;
                        modified = true;
                    }
                }
                else if (envObj.Remove(key))
                {
                    modified = true;
                }
            }

            if (!modified && File.Exists(appsFile))
                return;

            File.WriteAllText(appsFile, root.ToJsonString(_writeOpts));
            Logger.Log(hasOverrides
                ? $"[SunshineCfg] apps.json actualizado con entorno de perfil aislado: {appsFile}"
                : $"[SunshineCfg] apps.json actualizado sin entorno de perfil aislado: {appsFile}");
        }

        /// <summary>
        /// Opciones que OpenStreamMS preconfigura en <c>sunshine.conf</c> antes de arrancar
        /// cada instancia de Sunshine. Los campos <c>null</c> o vacíos no sobrescriben
        /// lo que el usuario haya guardado directamente desde Sunshine.
        /// </summary>
        public record SunshineConfig(
            /// <summary>
            /// Puerto base de Moonlight (<c>port</c> en <c>sunshine.conf</c>). Sunshine deriva
            /// de aquí el resto: panel HTTPS en <c>+1</c>, vídeo en <c>+9</c>, etc.
            /// </summary>
            int     StreamPort,
            string? Name               = null,
            string? Capture            = null,
            string? Encoder            = null,
            string? OutputName         = null,
            string? OriginWebUiAllowed = null,
            /// <summary>
            /// Dispositivo de audio que Sunshine captura (loopback).
            /// Null = Sunshine usa el dispositivo por defecto de la sesión.
            /// En Windows se prefiere el endpoint ID de tools\audio-info.exe para evitar
            /// ambiguedades con dispositivos que comparten nombre.
            /// </summary>
            string? AudioSink          = null,
            /// <summary>
            /// <c>stream_protocol</c>: <c>moonlight</c>, <c>webrtc</c> o <c>both</c>. Solo lo entiende
            /// Sunshine con WebRTC; un Sunshine sin el parche lo ignora con un aviso en su log.
            /// </summary>
            string? StreamProtocol     = null,
            /// <summary>Puerto TCP de señalización WebRTC (<c>webrtc_port</c>). 0 = no tocar.</summary>
            int     WebRtcPort         = 0,
            /// <summary>Rango UDP de media WebRTC (<c>webrtc_media_port_min/max</c>). 0 = no tocar.</summary>
            int     WebRtcMediaPortMin = 0,
            int     WebRtcMediaPortMax = 0);

        /// <summary>Clave de <c>sunshine.conf</c> con los protocolos que sirve Sunshine.</summary>
        public const string StreamProtocolKey = "stream_protocol";

        /// <summary>
        /// Escribe/actualiza claves en <c>sunshine.conf</c>.
        /// <para>
        /// <c>port</c> = <paramref name="cfg"/>.StreamPort. Además fuerza <c>upnp = disabled</c>
        /// para evitar que varias instancias peleen por redireccionar el mismo puerto en el router.
        /// </para>
        /// Las claves gestionadas se añaden o actualizan preservando el resto del fichero.
        /// </summary>
        public static void WriteConfig(string sunshineExePath, SunshineConfig cfg)
        {
            if (cfg.StreamPort <= 0)
                throw new ArgumentException("StreamPort debe ser > 0", nameof(cfg));

            string configDir = Path.Combine(Path.GetDirectoryName(sunshineExePath)!, "config");
            string confFile  = Path.Combine(configDir, "sunshine.conf");

            Directory.CreateDirectory(configDir);

            // Rutas absolutas fijadas dentro de la carpeta de la instancia: así Sunshine
            // no toca %APPDATA% y todo el estado queda junto al ejecutable de la sesión.
            // Se guardan con forward slashes para evitar problemas de escape en el .conf.
            static string AbsFor(string configDir, string relative) =>
                Path.Combine(configDir, relative).Replace('\\', '/');

            var overrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["port"]             = cfg.StreamPort.ToString(),
                ["upnp"]             = "disabled",
                ["file_state"]       = AbsFor(configDir, "sunshine_state.json"),
                ["file_apps"]        = AbsFor(configDir, "apps.json"),
                // Importante: credentials_file en archivo distinto a file_state. En
                // Sunshine 2026.x si ambos apuntan al mismo JSON, cada save de
                // credenciales reescribe TODO el fichero pisando root.named_devices.
                ["credentials_file"] = AbsFor(configDir, "credentials/sunshine_creds.json"),
                ["pkey"]             = AbsFor(configDir, "credentials/cakey.pem"),
                ["cert"]             = AbsFor(configDir, "credentials/cacert.pem"),
                ["log_path"]         = AbsFor(configDir, "sunshine.log"),
                // OJO: no capar codecs aqui (av1_mode/hevc_mode). El cliente Moonlight
                // elige el codec que SU hardware decodifica; capar AV1 forzo decode
                // software en el cliente (TV con decoder AV1 pero sin HEVC 4K60 util).
                ["av1_mode"]         = "0",
            };

            var optional = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["sunshine_name"]         = cfg.Name,
                ["capture"]               = cfg.Capture,
                ["encoder"]               = cfg.Encoder,
                ["output_name"]           = cfg.OutputName,
                ["origin_web_ui_allowed"] = cfg.OriginWebUiAllowed,
                ["audio_sink"]            = cfg.AudioSink,
                [StreamProtocolKey]       = cfg.StreamProtocol,
            };

            foreach (var (key, value) in optional)
            {
                if (!string.IsNullOrWhiteSpace(value))
                    overrides[key] = value.Trim();
            }

            // Valores por defecto: solo se escriben si la clave NO existe en el conf, asi
            // lo que el usuario cambie desde el panel de Sunshine se respeta.
            var defaults = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                // Sunshine 2026.9+ prioriza Virtual HID Driver (de pago, requiere licencia)
                // y deja ViGEmBus como fallback con avisos. OpenStreamMS instala ViGEmBus.
                ["gamepad_driver"] = "vigembus",
                // DS4 en vez de X360: es un HID normal, que GamepadSessionIsolation oculta
                // a las demás sesiones con HidHide (el XUSB del X360 no se puede ocultar y
                // el Steam del host se quedaba el mando).
                ["gamepad"] = "ds4",
                // AMF: preset "speed" reduce la latencia de encode por frame. A 4K60 el
                // bloque VCN se comparte con el H.264 del transporte RDP y con "balanced"
                // el encode se acerca al presupuesto de 16.6 ms. Ignorado por otros encoders.
                ["amd_quality"] = "speed",
            };

            // Puertos WebRTC: los gestiona OpenStreamMS (firewall y choques entre sesiones).
            if (cfg.WebRtcPort > 0)
                overrides["webrtc_port"] = cfg.WebRtcPort.ToString();
            if (cfg.WebRtcMediaPortMin > 0 && cfg.WebRtcMediaPortMax >= cfg.WebRtcMediaPortMin)
            {
                overrides["webrtc_media_port_min"] = cfg.WebRtcMediaPortMin.ToString();
                overrides["webrtc_media_port_max"] = cfg.WebRtcMediaPortMax.ToString();
            }

            var lines   = new List<string>();
            var applied = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (File.Exists(confFile))
            {
                foreach (var line in File.ReadAllLines(confFile))
                {
                    var trimmed = line.TrimStart();
                    var eqIdx   = trimmed.IndexOf('=');
                    if (eqIdx > 0 && !trimmed.StartsWith('#'))
                    {
                        var key = trimmed[..eqIdx].TrimEnd();
                        present.Add(key);
                        if (overrides.TryGetValue(key, out var newVal))
                        {
                            lines.Add($"{key} = {newVal}");
                            applied.Add(key);
                            continue;
                        }
                    }
                    lines.Add(line);
                }
            }

            foreach (var (key, val) in overrides)
                if (!applied.Contains(key))
                    lines.Add($"{key} = {val}");

            // Cada default se aplica UNA sola vez por instancia: el panel de Sunshine borra
            // del conf las claves que igualan SU default al guardar, asi que "clave ausente"
            // tambien puede significar "el usuario eligio el default de Sunshine".
            string markerFile = Path.Combine(configDir, "osms_defaults_applied.txt");
            var alreadyApplied = new HashSet<string>(
                File.Exists(markerFile) ? File.ReadAllLines(markerFile) : [],
                StringComparer.OrdinalIgnoreCase);
            var newlyApplied = new List<string>();

            foreach (var (key, val) in defaults)
            {
                if (overrides.ContainsKey(key) || alreadyApplied.Contains(key)) continue;
                // Si el usuario ya la tenia, solo se marca: no se pisa nunca su valor.
                if (!present.Contains(key)) lines.Add($"{key} = {val}");
                newlyApplied.Add(key);
            }

            if (newlyApplied.Count > 0)
            {
                File.AppendAllLines(markerFile, newlyApplied);
                var added = newlyApplied.Where(k => !present.Contains(k)).ToList();
                if (added.Count > 0)
                    Logger.Log($"[SunshineCfg] Defaults de OpenStreamMS aplicados: " +
                               string.Join(", ", added.Select(k => $"{k}={defaults[k]}")));
            }

            if (File.Exists(confFile) &&
                File.ReadAllLines(confFile).SequenceEqual(lines))
            {
                Logger.Log($"[SunshineCfg] sunshine.conf ya estaba actualizado: {confFile}");
                return;
            }

            File.WriteAllLines(confFile, lines);
            var summary = string.Join(", ", overrides.Select(kv => $"{kv.Key}={kv.Value}"));
            Logger.Log($"[SunshineCfg] sunshine.conf actualizado ({summary}) en: {confFile}");
        }

        /// <summary>
        /// Escribe claves sueltas en el <c>sunshine.conf</c> de una instancia, conservando el resto.
        /// Sirve para cambios en caliente (p.ej. el protocolo) sin regenerar toda la configuración.
        /// </summary>
        /// <returns>true si el fichero cambió.</returns>
        public static bool SetConfigValues(string sunshineExePath, IReadOnlyDictionary<string, string> values)
        {
            string configDir = Path.Combine(Path.GetDirectoryName(sunshineExePath)!, "config");
            Directory.CreateDirectory(configDir);
            var confFile = Path.Combine(configDir, "sunshine.conf");

            var lines   = new List<string>();
            var applied = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var current = File.Exists(confFile) ? File.ReadAllLines(confFile) : Array.Empty<string>();
            foreach (var line in current)
            {
                var trimmed = line.TrimStart();
                var eqIdx   = trimmed.IndexOf('=');
                if (eqIdx > 0 && !trimmed.StartsWith('#'))
                {
                    var key = trimmed[..eqIdx].TrimEnd();
                    var match = values.FirstOrDefault(kv => string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase));
                    if (match.Key is not null)
                    {
                        lines.Add($"{key} = {match.Value}");
                        applied.Add(match.Key);
                        continue;
                    }
                }
                lines.Add(line);
            }
            foreach (var (key, val) in values)
                if (!applied.Contains(key))
                    lines.Add($"{key} = {val}");

            if (File.Exists(confFile) && current.SequenceEqual(lines))
                return false;

            File.WriteAllLines(confFile, lines);
            Logger.Log($"[SunshineCfg] sunshine.conf actualizado ({string.Join(", ", values.Select(kv => $"{kv.Key}={kv.Value}"))}) en: {confFile}");
            return true;
        }

        /// <summary>
        /// Lee una clave del <c>sunshine.conf</c> de una instancia (la última si se repite).
        /// null si el fichero o la clave no existen.
        /// </summary>
        public static string? ReadConfigValue(string sunshineExePath, string key)
        {
            var confFile = Path.Combine(Path.GetDirectoryName(sunshineExePath)!, "config", "sunshine.conf");
            if (!File.Exists(confFile)) return null;

            string? value = null;
            foreach (var line in File.ReadAllLines(confFile))
            {
                var trimmed = line.TrimStart();
                var eqIdx   = trimmed.IndexOf('=');
                if (eqIdx <= 0 || trimmed.StartsWith('#')) continue;
                if (string.Equals(trimmed[..eqIdx].TrimEnd(), key, StringComparison.OrdinalIgnoreCase))
                    value = trimmed[(eqIdx + 1)..].Trim();
            }
            return value;
        }

        /// <summary>
        /// Credenciales pre-generadas para el panel web de Sunshine.
        /// </summary>
        public sealed record SunshineCredentials(string Username, string PlainPassword);

        /// <summary>
        /// Genera un par de credenciales aleatorias apto para inyectar en
        /// <c>sunshine_state.json</c>. El usuario es corto y legible; la contraseña
        /// son 24 caracteres base64url-safe.
        /// </summary>
        public static SunshineCredentials GenerateCredentials()
        {
            var rng = RandomNumberGenerator.Create();

            var userBytes = new byte[4];
            rng.GetBytes(userBytes);
            var user = "osms-" + Convert.ToHexString(userBytes).ToLowerInvariant();

            var passBytes = new byte[18]; // 18 → 24 chars en base64
            rng.GetBytes(passBytes);
            var pass = Convert.ToBase64String(passBytes)
                              .Replace('+', '-').Replace('/', '_').TrimEnd('=');

            return new SunshineCredentials(user, pass);
        }

        /// <summary>
        /// Escribe/actualiza las credenciales del panel en <c>sunshine_state.json</c> para
        /// que Sunshine arranque ya autenticado.
        /// Formato de hash compatible con Sunshine (<c>confighttp.cpp</c>):
        /// <c>util::hex(sha256(password + salt))</c> — OJO al orden: contraseña PRIMERO, salt DESPUÉS.
        /// <para>
        /// Las credenciales viven en el nivel RAÍZ del JSON (<c>username</c>/<c>salt</c>/<c>password</c>).
        /// El sub-objeto <c>root</c> contiene los dispositivos emparejados (<c>named_devices</c>) y NO
        /// se toca: tocarlo perdería los pareos hechos por el usuario.
        /// </para>
        /// </summary>
        public static void WriteCredentials(string sunshineExePath, SunshineCredentials creds)
        {
            string configDir = Path.Combine(Path.GetDirectoryName(sunshineExePath)!, "config");
            string stateFile = Path.Combine(configDir, "sunshine_state.json");

            Directory.CreateDirectory(configDir);

            JsonObject state;
            if (File.Exists(stateFile))
            {
                try { state = JsonNode.Parse(File.ReadAllText(stateFile))?.AsObject() ?? new JsonObject(); }
                catch { state = new JsonObject(); }
            }
            else
            {
                state = new JsonObject();
            }

            if (CredentialsMatch(state, creds))
            {
                if (!RemoveLegacyRootCredentials(state))
                {
                    Logger.Log($"[SunshineCfg] sunshine_state.json: credenciales ya actualizadas (user={creds.Username})");
                    return;
                }

                File.WriteAllText(stateFile, state.ToJsonString(_writeOpts));
                Logger.Log($"[SunshineCfg] sunshine_state.json: credenciales ya actualizadas; root limpiado (user={creds.Username})");
                return;
            }

            var rng      = RandomNumberGenerator.Create();
            var saltRaw  = new byte[16];
            rng.GetBytes(saltRaw);
            var saltHex  = Convert.ToHexString(saltRaw).ToLowerInvariant();

            // Sunshine hace: hash = hex(sha256(password + salt_as_stored_string))
            var hashInput = creds.PlainPassword + saltHex;
            var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(hashInput));
            var hashHex   = Convert.ToHexString(hashBytes).ToLowerInvariant();

            state["username"] = creds.Username;
            state["salt"]     = saltHex;
            state["password"] = hashHex;

            // Limpiar credenciales escritas por error dentro de root por versiones previas
            // (bug que tiraba named_devices al sobrescribir todo el sub-objeto root).
            RemoveLegacyRootCredentials(state);

            File.WriteAllText(stateFile, state.ToJsonString(_writeOpts));
            Logger.Log($"[SunshineCfg] sunshine_state.json: credenciales establecidas (user={creds.Username})");
        }

        private static bool CredentialsMatch(JsonObject state, SunshineCredentials creds)
        {
            if (!TryGetString(state, "username", out var username) ||
                !TryGetString(state, "salt", out var salt) ||
                !TryGetString(state, "password", out var storedHash))
                return false;

            var hashInput = creds.PlainPassword + salt;
            var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(hashInput));
            var hashHex   = Convert.ToHexString(hashBytes).ToLowerInvariant();

            return string.Equals(username, creds.Username, StringComparison.Ordinal) &&
                   string.Equals(storedHash, hashHex, StringComparison.OrdinalIgnoreCase);
        }

        private static bool RemoveLegacyRootCredentials(JsonObject state)
        {
            if (state["root"] is not JsonObject rootObj)
                return false;

            return rootObj.Remove("username") |
                   rootObj.Remove("salt") |
                   rootObj.Remove("password");
        }

        private static bool TryGetString(JsonObject obj, string key, out string value)
        {
            value = "";
            if (obj[key] is not JsonValue jsonValue ||
                !jsonValue.TryGetValue<string>(out var text) ||
                string.IsNullOrEmpty(text))
                return false;

            value = text;
            return true;
        }
    }
}
