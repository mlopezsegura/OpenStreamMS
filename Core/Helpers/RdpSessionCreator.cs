using Microsoft.Win32;
using OpenStreamMS.Services;
using System.Diagnostics;
using System.Runtime.InteropServices;
using ServiceController = System.ServiceProcess.ServiceController;
using ServiceControllerStatus = System.ServiceProcess.ServiceControllerStatus;

namespace OpenStreamMS.Core.Helpers
{
    /// <summary>
    /// Localiza o crea una sesion RDP para el usuario de stream usando FreeRDP.
    /// FreeRDP corre en Session 0 (el propio servicio Windows), sin necesitar
    /// ningun usuario logado en la consola ni ningun desktop interactivo.
    ///
    /// PIPELINE DE RENDIMIENTO:
    ///   1. ApplyFrameRatePolicy    — MaxFrameRate + DWMFRAMEINTERVAL (ms) en registro.
    ///   2. ApplyGpuCapturePolicy   — H.264/AVC hardware encoder + captura GPU vía DDA.
    ///   3. ApplyDisplayRefreshRateRegistry — VRefresh en RDPUDD antes de que arranque la sesión.
    ///   4. FreeRDP con /gfx /rfx /network:lan — codec correcto para framerate alto.
    ///   5. ApplyDisplayFrequency   — ChangeDisplaySettings dentro de la sesión ya iniciada,
    ///      con verificación del resultado (DISP_CHANGE_*) y de la frecuencia real aplicada.
    /// </summary>
    public static class RdpSessionCreator
    {
        // ── P/Invoke ─────────────────────────────────────────────────────────────

        [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true,
            EntryPoint = "WTSEnumerateSessionsW")]
        static extern bool WTSEnumerateSessions(
            IntPtr hServer, int Reserved, int Version,
            out IntPtr ppSessionInfo, out int pCount);

        [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true,
            EntryPoint = "WTSQuerySessionInformationW")]
        static extern bool WTSQuerySessionInformation(
            IntPtr hServer, int sessionId, WTS_INFO_CLASS wtsInfoClass,
            out IntPtr ppBuffer, out int pBytesReturned);

        [DllImport("wtsapi32.dll")]
        static extern void WTSFreeMemory(IntPtr pMemory);

        [DllImport("wtsapi32.dll", SetLastError = true)]
        static extern bool WTSDisconnectSession(IntPtr hServer, int SessionId, bool bWait);

        [DllImport("wtsapi32.dll", SetLastError = true)]
        static extern bool WTSLogoffSession(IntPtr hServer, int SessionId, bool bWait);

        // ── Structs / Enums ───────────────────────────────────────────────────────

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct WTS_SESSION_INFO
        {
            public int SessionId;
            public string pWinStationName;
            public int State;
        }

        enum WTS_INFO_CLASS
        {
            WTSUserName = 5,
            WTSDomainName = 7,
            WTSClientProtocolType = 16,
            WTSSessionInfoEx = 25
        }

        // WTSINFOEX_LEVEL1_W.SessionFlags. En Win10/11: 0 = bloqueada (lock screen),
        // 1 = desbloqueada. -1 = desconocido (algunas builds antiguas).
        const int WtsSessionStateLock   = 0;
        const int WtsSessionStateUnlock = 1;

        // ── Public types ──────────────────────────────────────────────────────────

        public sealed record RdpSessionInfo(
            int SessionId,
            string WinStationName,
            int State,
            string Username,
            string Domain,
            int Protocol);

        // ── Helpers ───────────────────────────────────────────────────────────────

        static string FreeRdpExe =>
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "FreeRDP", "wfreerdp.exe");

        /// <summary>
        /// Loopback alternativo: permite que el RDP local no choque con conexiones
        /// RDP reales a 127.0.0.1 y que cmdkey guarde credenciales para un target
        /// que no afecta a otros usos de TERMSRV.
        /// </summary>
        const string LoopbackHost = "127.0.0.2";

        // ── Public API ────────────────────────────────────────────────────────────

        /// <summary>
        /// Devuelve el ID de sesion del usuario. Si no existe, crea una via FreeRDP.
        /// </summary>
        public static uint GetOrCreateSession(StreamUserConfig user,
                                              int rdpWidth, int rdpHeight,
                                              int rdpFrameRate, int rdpColorDepth,
                                              out int freeRdpPid,
                                              bool preferMstsc = true)
        {
            freeRdpPid = 0;

            // Escritorio Remoto habilitado de forma NATIVA (sin RDPWrapper/TermWrap ni
            // ningun binario externo). Basta para UNA sesion RDP simultanea, que es lo
            // que Windows cliente permite de fabrica: la sesion de stream toma esa unica
            // sesion. Idempotente; se ejecuta antes de cada creacion/reutilizacion.
            EnsureRemoteDesktopEnabled();

            if (FindConflictingConsoleSession(user) is { } conflict)
                Logger.Log(
                    $"[RDP] Aviso: '{FormatPrincipal(user)}' ya tiene sesion de consola " +
                    $"(SessionId={conflict.SessionId}). El aislamiento de audio puede ser parcial.");

            int existing = FindSessionByUsername(user.Username);
            if (existing >= 0)
            {
                Logger.Log($"[RDP] Sesion existente para '{user.Username}': {existing}");
                return (uint)existing;
            }

            // mstsc preferido: trae decoder AVC (H.264) por hardware (DXVA). El wfreerdp
            // bundled se compilo SIN H264 (ver /buildconfig), asi que con el termsrv
            // cae a RemoteFX progressive por CPU: a 4K la composicion se desploma a
            // 8-15 fps con la imagen en movimiento (diente de sierra 60→8).
            if (preferMstsc)
            {
                try
                {
                    Logger.Log($"[RDP] Sin sesion para '{user.Username}', creando via mstsc (AVC444 hw)...");
                    return CreateSessionWithMstsc(user, rdpWidth, rdpHeight,
                                                  rdpFrameRate, rdpColorDepth, out freeRdpPid);
                }
                catch (Exception ex)
                {
                    Logger.Warning($"[RDP] mstsc fallo ({ex.Message}); reintentando con FreeRDP...");
                }
            }

            Logger.Log($"[RDP] Sin sesion para '{user.Username}', creando via FreeRDP...");
            return CreateSessionWithFreeRdp(user, rdpWidth, rdpHeight,
                                            rdpFrameRate, rdpColorDepth, out freeRdpPid);
        }

        public static RdpSessionInfo? FindConflictingConsoleSession(StreamUserConfig user)
        {
            foreach (var s in EnumerateSessions(logSessions: false))
            {
                if (s.SessionId == 0) continue;
                if (!SamePrincipal(s, user)) continue;
                if (s.WinStationName.Equals("Console", StringComparison.OrdinalIgnoreCase))
                    return s;
            }
            return null;
        }

        /// <summary>Desconecta el cliente FreeRDP pero mantiene la sesion Windows viva.</summary>
        public static void DisconnectRdpClient(uint sessionId)
        {
            if (WTSDisconnectSession(IntPtr.Zero, (int)sessionId, false))
                Logger.Log($"[RDP] Cliente FreeRDP desconectado; sesion {sessionId} sigue activa (VDD).");
            else
                Logger.Warning($"[RDP] WTSDisconnectSession warning: {Marshal.GetLastWin32Error()}");
        }

        /// <summary>Cierra la sesion Windows del usuario (logoff).</summary>
        public static void LogoffSession(uint sessionId)
        {
            if (sessionId == 0) return;
            if (WTSLogoffSession(IntPtr.Zero, (int)sessionId, false))
                Logger.Log($"[RDP] Sesion {sessionId} cerrada (logoff).");
            else
                Logger.Warning($"[RDP] WTSLogoffSession warning (sesion {sessionId}): {Marshal.GetLastWin32Error()}");
        }

        // ── Creacion de sesion ────────────────────────────────────────────────────

        static uint CreateSessionWithFreeRdp(StreamUserConfig user, int rdpWidth, int rdpHeight,
                                             int rdpFrameRate, int rdpColorDepth, out int freeRdpPid)
        {
            freeRdpPid = 0;

            if (!File.Exists(FreeRdpExe))
                throw new FileNotFoundException($"[RDP] No se encontro wfreerdp.exe en: {FreeRdpExe}", FreeRdpExe);

            rdpFrameRate = NormalizeFrameRate(rdpFrameRate);
            rdpColorDepth = NormalizeColorDepth(rdpColorDepth);

            // Aplicar politicas ANTES de lanzar FreeRDP — termsrv las lee al aceptar la conexion.
            ApplyFrameRatePolicy(rdpFrameRate);
            ApplyGpuCapturePolicy();
            ApplyDisplayRefreshRateRegistry(rdpFrameRate);

            string domain = string.IsNullOrEmpty(user.Domain) || user.Domain == "."
                ? Environment.MachineName
                : user.Domain;

            var psi = new ProcessStartInfo(FreeRdpExe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(FreeRdpExe)!
            };

            // ── Conexion ──────────────────────────────────────────────────────────
            psi.ArgumentList.Add($"/v:{LoopbackHost}");
            psi.ArgumentList.Add($"/u:{user.Username}");
            psi.ArgumentList.Add($"/d:{domain}");
            psi.ArgumentList.Add($"/p:{user.Password}");
            psi.ArgumentList.Add("/cert:ignore");

            // ── Display ───────────────────────────────────────────────────────────
            psi.ArgumentList.Add($"/w:{rdpWidth}");
            psi.ArgumentList.Add($"/h:{rdpHeight}");
            psi.ArgumentList.Add($"/bpp:{rdpColorDepth}");

            // ── Codec ─────────────────────────────────────────────────────────────
            // NO usar /gfx ni /rfx. El cliente wfreerdp corre OCULTO en Session 0:
            // nadie ve su ventana. Moonlight ve la captura DDA de Sunshine sobre el
            // mismo escritorio. /gfx fuerza encode H.264 server-side en termsrv que
            // COMPITE con Sunshine por la GPU → cae a CPU → 60→8 fps diente de sierra.
            // git bisect (01c44bc..70bd9df) senala el commit "Mejoras rdp" (0ad97d6)
            // que anadio /gfx + /rfx + /dynamic-resolution como la regresion exacta.
            // network:lan = desactiva throttle de ancho de banda (inofensivo, se queda).
            psi.ArgumentList.Add("/network:lan");

            // ── Audio ─────────────────────────────────────────────────────────────
            psi.ArgumentList.Add("/audio-mode:redirect");
            psi.ArgumentList.Add("/sound:sys:winmm");

            // ── Misc ──────────────────────────────────────────────────────────────
            // +disable-output = cliente wfreerdp oculto en Session 0 no decodifica ni
            // pinta frames (nadie ve su ventana). Ahorra CPU/GPU del decode redundante;
            // Sunshine captura el escritorio directo via DDA, no depende del output RDP.
            psi.ArgumentList.Add("+disable-output");
            psi.ArgumentList.Add("/log-level:ERROR");

            Logger.Log($"[RDP] Iniciando FreeRDP " +
                       $"({rdpWidth}x{rdpHeight}, {rdpFrameRate} fps, {rdpColorDepth} bpp, sin gfx)...");

            var proc = Process.Start(psi)
                ?? throw new InvalidOperationException("[RDP] No se pudo iniciar wfreerdp.exe");

            // Con /log-level:ERROR todo lo que llegue por stderr es un error real.
            proc.OutputDataReceived += (_, e) => { if (e.Data is not null) Logger.Log($"[FreeRDP] {e.Data}"); };
            proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) Logger.Warning($"[FreeRDP] {e.Data}"); };
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            int newSession = WaitForSession(proc, user.Username);

            if (newSession < 0)
            {
                if (proc.HasExited)
                    throw new InvalidOperationException(
                        $"[RDP] FreeRDP termino con ExitCode={proc.ExitCode} sin crear sesion para " +
                        $"'{user.Username}'. ExitCode=-1001 suele indicar un argumento no soportado " +
                        "(wfreerdp imprime la ayuda y sale); revisa tambien usuario/contraseña.");

                try { proc.Kill(entireProcessTree: true); } catch { /* ignorar */ }
                throw new TimeoutException(
                    $"[RDP] Sesion para '{user.Username}' no aparecio tras 30 s. " +
                    "Verifica: (1) usuario y contraseña correctos, " +
                    "(2) Escritorio Remoto habilitado, " +
                    "(3) usuario en grupo 'Usuarios de escritorio remoto'.");
            }

            freeRdpPid = proc.Id;
            return (uint)newSession;
        }

        // ── Creacion de sesion via mstsc ──────────────────────────────────────────

        /// <summary>
        /// Crea la sesion con el cliente RDP de Windows (mstsc). A diferencia del
        /// wfreerdp bundled (compilado sin H264), mstsc negocia AVC (H.264) y decodifica
        /// por DXVA en el bloque de video de la GPU: el transporte RDP deja de
        /// costar CPU y termsrv puede componer a 60 fps incluso a 4K.
        /// <para>
        /// Credenciales via cmdkey (TERMSRV/host en el almacen del usuario del
        /// servicio, borradas tras conectar). Ajustes via fichero .rdp temporal:
        /// authentication level 0 evita el dialogo de certificado (invisible en
        /// Session 0 → colgaria la conexion).
        /// </para>
        /// </summary>
        static uint CreateSessionWithMstsc(StreamUserConfig user, int rdpWidth, int rdpHeight,
                                           int rdpFrameRate, int rdpColorDepth, out int clientPid)
        {
            clientPid = 0;

            rdpFrameRate = NormalizeFrameRate(rdpFrameRate);
            rdpColorDepth = NormalizeColorDepth(rdpColorDepth);

            // Politicas ANTES de conectar — termsrv las lee al aceptar la conexion.
            ApplyFrameRatePolicy(rdpFrameRate);
            ApplyGpuCapturePolicy();
            ApplyAvcPolicy();
            ApplyMstscClientPolicies();
            ApplyDisplayRefreshRateRegistry(rdpFrameRate);

            string domain = NormalizeDomain(user.Domain);
            string principal = $@"{domain}\{user.Username}";
            string credTarget = $"TERMSRV/{LoopbackHost}";

            RunCmdKey("/generic:" + credTarget, "/user:" + principal, "/pass:" + user.Password);

            string rdpFile = Path.Combine(Path.GetTempPath(), $"osms_{user.Username}_{Guid.NewGuid():N}.rdp");

            // screen mode id 1 (ventana) + desktopwidth/height explicitos: la resolucion
            // del escritorio remoto no depende del "monitor" fantasma de Session 0.
            // networkautodetect/bandwidthautodetect a 0 + connection type 6 (LAN):
            // sin throttle por estimacion de ancho de banda en loopback.
            // Sin redirecciones de dispositivos: evita el dialogo de publisher.
            File.WriteAllLines(rdpFile, new[]
            {
                $"full address:s:{LoopbackHost}",
                $"username:s:{principal}",
                $"desktopwidth:i:{rdpWidth}",
                $"desktopheight:i:{rdpHeight}",
                $"session bpp:i:{rdpColorDepth}",
                "screen mode id:i:1",
                "use multimon:i:0",
                "authentication level:i:0",
                "prompt for credentials:i:0",
                "promptcredentialonce:i:0",
                "enablecredsspsupport:i:1",
                "audiomode:i:0",
                "audiocapturemode:i:0",
                "redirectclipboard:i:0",
                "redirectprinters:i:0",
                "redirectcomports:i:0",
                "redirectsmartcards:i:0",
                "networkautodetect:i:0",
                "bandwidthautodetect:i:0",
                "connection type:i:6",
                "compression:i:0",
                "smart sizing:i:0",
            });

            try
            {
                var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "mstsc.exe"))
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                psi.ArgumentList.Add(rdpFile);

                Logger.Log($"[RDP] Iniciando mstsc ({rdpWidth}x{rdpHeight}, {rdpFrameRate} fps, " +
                           $"{rdpColorDepth} bpp, AVC444 hw)...");

                var proc = Process.Start(psi)
                    ?? throw new InvalidOperationException("[RDP] No se pudo iniciar mstsc.exe");

                int newSession = WaitForSession(proc, user.Username);

                if (newSession < 0)
                {
                    if (proc.HasExited)
                        throw new InvalidOperationException(
                            $"[RDP] mstsc termino con ExitCode={proc.ExitCode} sin crear sesion " +
                            $"para '{user.Username}'. Posible dialogo invisible (cert/credenciales) " +
                            "o NLA rechazado.");

                    try { proc.Kill(entireProcessTree: true); } catch { /* ignorar */ }
                    throw new TimeoutException(
                        $"[RDP] Sesion para '{user.Username}' no aparecio tras 30 s con mstsc. " +
                        "Posible dialogo invisible en Session 0.");
                }

                clientPid = proc.Id;
                return (uint)newSession;
            }
            finally
            {
                // Credencial y .rdp solo hacen falta durante el handshake.
                RunCmdKey("/delete:" + credTarget);
                try { File.Delete(rdpFile); } catch { /* temp, no critico */ }
            }
        }

        /// <summary>Ejecuta cmdkey.exe con los argumentos dados y espera a que termine.</summary>
        static void RunCmdKey(params string[] args)
        {
            try
            {
                var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmdkey.exe"))
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                foreach (var a in args) psi.ArgumentList.Add(a);

                using var p = Process.Start(psi);
                p?.WaitForExit(10_000);
            }
            catch (Exception ex)
            {
                Logger.Warning($"[RDP] cmdkey {args.FirstOrDefault()} fallo: {ex.Message}");
            }
        }

        /// <summary>
        /// Espera hasta 30 s a que la sesion aparezca en WTS.
        /// Los primeros 10 intentos exigen protocolo RDP; del 11 al 20 acepta cualquier sesion
        /// no-consola (modo ampliado, por si termsrv reporta protocolo 0 brevemente).
        /// Devuelve -1 si FreeRDP murio o la sesion no aparecio (el llamador distingue
        /// ambos casos consultando proc.HasExited).
        /// </summary>
        static int WaitForSession(Process proc, string username)
        {
            for (int i = 0; i < 20; i++)
            {
                // WaitForExit con timeout: si FreeRDP muere, reaccionamos al instante
                // en vez de esperar a que venza el Sleep.
                if (proc.WaitForExit(1500))
                {
                    Logger.Warning($"[RDP] FreeRDP termino prematuramente. ExitCode={proc.ExitCode}");
                    return -1;
                }

                bool relaxed = i >= 10;

                // Solo volcamos el listado completo de sesiones en el primer intento
                // y al entrar en modo ampliado — evita 20 bloques identicos en el log.
                int newSession = FindSessionByUsername(username, rdpOnly: !relaxed,
                                                       logSessions: i == 0 || i == 10,
                                                       activeOnly: true);

                if (newSession >= 0)
                {
                    Logger.Log($"[RDP] Sesion detectada en intento {i + 1} " +
                               $"({(relaxed ? "modo ampliado" : "RDP")}): " +
                               $"{newSession} (FreeRDP PID={proc.Id})");
                    return newSession;
                }

                Logger.Log($"[RDP] Esperando sesion... intento {i + 1}/20");
            }
            return -1;
        }

        // ── Politicas de registro ─────────────────────────────────────────────────

        /// <summary>
        /// Escribe MaxFrameRate (fps directo, Win10 1903+) y DWMFRAMEINTERVAL (milisegundos,
        /// misma unidad en todas las rutas) en Policies, Terminal Services y WinStations,
        /// incluida la key per-connection RDP-Tcp que termsrv lee al aceptar cada conexion.
        /// <para>
        /// DWMFRAMEINTERVAL es un tope: redondear 16.67 → 17 ms limitaba a 58.8 fps y
        /// Sunshine veia la pantalla a 58 Hz (un frame repetido cada ~0.5 s = microsaltos).
        /// floor-1 deja margen por encima del objetivo (60 → 15, el valor que documenta
        /// Microsoft para 60 fps).
        /// </para>
        /// </summary>
        static void ApplyFrameRatePolicy(int frameRate)
        {
            var frameIntervalMs = Math.Max(1, (int)Math.Floor(1000.0 / frameRate) - 1);

            var entries = new (string Path, string Name, int Value, RegistryValueKind Kind)[]
            {
                // fps directo — la key mas efectiva en Win10 1903+
                (@"SOFTWARE\Policies\Microsoft\Windows NT\Terminal Services",
                    "MaxFrameRate",     frameRate,       RegistryValueKind.DWord),

                // DWMFRAMEINTERVAL en milisegundos — todas las rutas usan la misma unidad
                (@"SOFTWARE\Policies\Microsoft\Windows NT\Terminal Services",
                    "DWMFRAMEINTERVAL", frameIntervalMs, RegistryValueKind.DWord),
                (@"SOFTWARE\Microsoft\Windows NT\Terminal Services",
                    "DWMFRAMEINTERVAL", frameIntervalMs, RegistryValueKind.DWord),
                (@"SYSTEM\CurrentControlSet\Control\Terminal Server\WinStations",
                    "DWMFRAMEINTERVAL", frameIntervalMs, RegistryValueKind.DWord),

                // per-connection — leido por termsrv al aceptar cada conexion RDP entrante
                (@"SYSTEM\CurrentControlSet\Control\Terminal Server\WinStations\RDP-Tcp",
                    "DWMFRAMEINTERVAL", frameIntervalMs, RegistryValueKind.DWord),
            };

            foreach (var (path, name, value, kind) in entries)
                WriteRegistry(path, name, value, kind);

            Logger.Log($"[RDP] FrameRate={frameRate} fps | interval={frameIntervalMs} ms");
        }

        /// <summary>
        /// Habilita captura por GPU (DDA) en la sesion RDP-Tcp.
        /// <para>
        ///   fEnableHardwareGraphicsCapture = 1 → DDA en lugar de GDI mirror.
        ///   bEnumerateHWBeforeSW           = 1 → termsrv usa encoder hardware.
        ///     Probado con 0 (encode software): CPU al limite codificando 4K60 y
        ///     el stream de Sunshine empeoro (calidad y fps). El encode hardware
        ///     del stream wfreerdp comparte el bloque VCN con Sunshine, pero el
        ///     ritmo efectivo lo limita el decode software del cliente (~20-40 fps
        ///     via backpressure TCP), asi que la tajada de VCN es moderada. La
        ///     composicion DWM no se ve afectada (/gfx:frame-ack:off).
        /// </para>
        /// fEnableRemoteFXAdv fue removido en Win10 1903+ (RemoteFX esta deprecated);
        /// no se escribe para evitar confusiones en el log.
        /// </summary>
        static void ApplyGpuCapturePolicy()
        {
            // RDP-Tcp: leido por termsrv al aceptar la conexion entrante de FreeRDP.
            const string rdpTcp = @"SYSTEM\CurrentControlSet\Control\Terminal Server\WinStations\RDP-Tcp";

            // Policies: leido por DWM y por el subsistema de graficos remotos.
            const string policies = @"SOFTWARE\Policies\Microsoft\Windows NT\Terminal Services";

            WriteRegistry(rdpTcp, "fEnableHardwareGraphicsCapture", 1, RegistryValueKind.DWord);
            WriteRegistry(policies, "bEnumerateHWBeforeSW", 1, RegistryValueKind.DWord);
            WriteRegistry(policies, "ImageQuality", 80, RegistryValueKind.DWord);
        }

        /// <summary>
        /// Prioriza el modo grafico H.264/AVC 444 con encode hardware para conexiones
        /// RDP entrantes (GPO "Prioritize H.264/AVC 444" / "Configure H.264/AVC
        /// hardware encoding"). Solo surte efecto con clientes que decodifican AVC
        /// (mstsc); el wfreerdp bundled no lo soporta y negocia RemoteFX progressive.
        /// <para>
        /// Se probo AVC420 (menos carga en VCN) y coincidio con microsaltos en el stream
        /// de Sunshine: el ritmo de composicion de la sesion depende de este transporte.
        /// </para>
        /// </summary>
        static void ApplyAvcPolicy()
        {
            const string policies = @"SOFTWARE\Policies\Microsoft\Windows NT\Terminal Services";
            WriteRegistry(policies, "AVC444ModePreferred", 1, RegistryValueKind.DWord);
            WriteRegistry(policies, "AVCHardwareEncodePreferred", 1, RegistryValueKind.DWord);
        }

        /// <summary>
        /// Suprime los dialogos que mstsc muestra antes de conectar y que en
        /// Session 0 son invisibles (la conexion se queda colgada para siempre):
        /// <para>
        ///   AllowUnsignedFiles = 1  → sin aviso "publisher desconocido" para .rdp
        ///                             sin firmar (GPO RD Connection Client). Se
        ///                             escribe en HKLM y en el HKCU del usuario del
        ///                             servicio (SYSTEM), que es quien lanza mstsc.
        ///   AuthenticationLevelOverride = 0 → sin aviso de certificado no confiable
        ///                             (cinturon extra ademas de authentication
        ///                             level:i:0 en el .rdp).
        /// </para>
        /// </summary>
        static void ApplyMstscClientPolicies()
        {
            const string policies = @"SOFTWARE\Policies\Microsoft\Windows NT\Terminal Services";
            WriteRegistry(policies, "AllowUnsignedFiles", 1, RegistryValueKind.DWord);

            try
            {
                using (var key = Registry.CurrentUser.CreateSubKey(
                           @"SOFTWARE\Policies\Microsoft\Windows NT\Terminal Services"))
                    key?.SetValue("AllowUnsignedFiles", 1, RegistryValueKind.DWord);

                using (var key = Registry.CurrentUser.CreateSubKey(
                           @"Software\Microsoft\Terminal Server Client"))
                    key?.SetValue("AuthenticationLevelOverride", 0, RegistryValueKind.DWord);

                Logger.Log("[RDP] Politicas de cliente mstsc aplicadas (sin dialogos pre-conexion).");
            }
            catch (Exception ex)
            {
                Logger.Warning($"[RDP] No se pudieron aplicar politicas HKCU de mstsc: {ex.Message}");
            }
        }

        /// <summary>
        /// Escribe DefaultSettings.VRefresh en TODOS los adaptadores de video detectados
        /// como virtuales RDP, identificandolos por DriverDesc O por la ausencia de
        /// HardwareInformation.MemorySize (los VDD no reportan VRAM).
        /// RDPUDD fija la frecuencia al inicializar la sesion; ChangeDisplaySettings
        /// en runtime devuelve DISP_CHANGE_RESTART en ese driver, por eso esto debe
        /// aplicarse ANTES de lanzar FreeRDP.
        /// </summary>
        static void ApplyDisplayRefreshRateRegistry(int frameRate)
        {
            const string videoBase = @"SYSTEM\CurrentControlSet\Control\Video";

            try
            {
                using var videoKey = Registry.LocalMachine.OpenSubKey(videoBase);
                if (videoKey is null)
                {
                    Logger.Warning($"[RDP] HKLM\\{videoBase} no encontrado.");
                    return;
                }

                int applied = 0;

                foreach (var guidName in videoKey.GetSubKeyNames())
                {
                    using var guidKey = videoKey.OpenSubKey(guidName);
                    if (guidKey is null) continue;

                    foreach (var monName in guidKey.GetSubKeyNames())
                    {
                        string subPath = $@"{videoBase}\{guidName}\{monName}";

                        using var monKey = Registry.LocalMachine.OpenSubKey(subPath, writable: true);
                        if (monKey is null) continue;

                        if (!IsRdpVirtualAdapter(monKey)) continue;

                        monKey.SetValue("DefaultSettings.VRefresh", frameRate, RegistryValueKind.DWord);
                        var desc = GetAdapterDescription(monKey);
                        Logger.Log($"[RDP] VRefresh={frameRate} → HKLM\\{subPath} ({desc})");
                        applied++;
                    }
                }

                if (applied == 0)
                    Logger.Warning("[RDP] Ningun adaptador virtual RDP encontrado en Control\\Video — " +
                                   "VRefresh no cambiado. La sesion usara la frecuencia default del driver.");
                else
                    Logger.Log($"[RDP] VRefresh={frameRate} aplicado a {applied} adaptador(es) virtual(es).");
            }
            catch (Exception ex)
            {
                Logger.Warning($"[RDP] Error estableciendo VRefresh en registro: {ex.Message}");
            }
        }

        /// <summary>
        /// Determina si una subkey de Control\Video corresponde a un adaptador virtual RDP.
        /// Criterios (OR):
        ///   1. DriverDesc / Device Description contiene "remote" o "rdp" (case-insensitive).
        ///   2. No tiene HardwareInformation.MemorySize (los VDD no tienen VRAM fisica).
        ///      Solo aplica este criterio si el valor ProviderName tampoco existe (evita
        ///      falsos positivos con tarjetas con driver no estandar).
        /// </summary>
        static bool IsRdpVirtualAdapter(RegistryKey key)
        {
            var desc = GetAdapterDescription(key);

            if (desc.Contains("remote", StringComparison.OrdinalIgnoreCase) ||
                desc.Contains("rdp", StringComparison.OrdinalIgnoreCase))
                return true;

            // Heuristica secundaria: VDD sin VRAM y sin nombre de proveedor fisico.
            bool hasVram = key.GetValue("HardwareInformation.MemorySize") is not null;
            bool hasProvider = key.GetValue("ProviderName") is string p &&
                               !string.IsNullOrWhiteSpace(p);

            return !hasVram && !hasProvider;
        }

        static string GetAdapterDescription(RegistryKey key) =>
            ((key.GetValue("DriverDesc") ?? key.GetValue("Device Description")) as string) ?? "";

        // ── Cambio de frecuencia en runtime (dentro de la sesion) ─────────────────

        /// <summary>
        /// Ejecuta ChangeDisplaySettings dentro de la sesion RDP via un script PowerShell
        /// lanzado con ProcessInSession. Usa ENUM_CURRENT_SETTINGS (0xFFFFFFFE) para leer
        /// el modo activo y solo sobreescribe dmDisplayFrequency con el DEVMODE correcto.
        /// El script escribe "codigo;frecuenciaReal" en un fichero del TEMP del usuario
        /// de la sesion para que el servicio pueda verificar el resultado.
        ///
        /// NOTAS TECNICAS:
        ///   - DEVMODE Win32 tiene 220 bytes (dmSize=220) en la version completa. dmSize=124
        ///     cubre solo hasta dmDisplayFrequency (campo hz) si el struct esta correctamente
        ///     alineado; 220 es mas seguro y compatible con todos los drivers.
        ///   - DM_DISPLAYFREQUENCY = 0x400000. Sin este flag en dmFields el cambio es ignorado.
        ///   - CDS_UPDATEREGISTRY (flag 1) devuelve DISP_CHANGE_RESTART en RDPUDD; usar flag 0.
        /// </summary>
        public static void ApplyDisplayFrequency(uint sessionId, int frameRate)
        {
            frameRate = NormalizeFrameRate(frameRate);

            // Fichero de resultado en el TEMP del usuario de la sesion: el usuario puede
            // escribirlo y el servicio (SYSTEM) puede leerlo. Si no se puede resolver el
            // TEMP, se ejecuta igual pero sin verificacion.
            string? resultFile = null;
            try
            {
                var env = new ProcessInSession().GetSessionEnvVars(sessionId);
                if (env.TryGetValue("TEMP", out var tmp) && !string.IsNullOrWhiteSpace(tmp))
                    resultFile = Path.Combine(tmp, $"osms_vrefresh_{Guid.NewGuid():N}.txt");
            }
            catch (Exception ex)
            {
                Logger.Warning($"[RDP] No se pudo resolver TEMP de la sesion {sessionId}: {ex.Message}");
            }

            string reportLine = resultFile is null
                ? "exit $result"
                : $"Set-Content -LiteralPath '{resultFile}' -Value \"$result;$($dmVerify.dmDisplayFrequency)\" -Encoding ASCII\nexit $result";

            var script = $@"
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class DisplayHelper
{{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    public struct DEVMODE
    {{
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public ushort dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public uint   dmFields;
        public int    dmPositionX, dmPositionY;
        public uint   dmDisplayOrientation, dmDisplayFixedOutput;
        public short  dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public ushort dmLogPixels;
        public uint   dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public uint   dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2;
        public uint   dmPanningWidth, dmPanningHeight;
    }}

    [DllImport(""user32.dll"", CharSet = CharSet.Ansi)]
    public static extern bool EnumDisplaySettings(string lpszDeviceName, uint iModeNum, ref DEVMODE lpDevMode);

    [DllImport(""user32.dll"", CharSet = CharSet.Ansi)]
    public static extern int ChangeDisplaySettings(ref DEVMODE lpDevMode, uint dwFlags);

    public const uint ENUM_CURRENT_SETTINGS = 0xFFFFFFFE;
    public const uint DM_DISPLAYFREQUENCY   = 0x400000;
    public const uint DM_BITSPERPEL         = 0x000040;
    public const uint DM_PELSWIDTH          = 0x000080;
    public const uint DM_PELSHEIGHT         = 0x000100;
}}
'@

$dm = New-Object DisplayHelper+DEVMODE
$dm.dmSize = [System.Runtime.InteropServices.Marshal]::SizeOf($dm)

if (-not ([DisplayHelper]::EnumDisplaySettings($null, [DisplayHelper]::ENUM_CURRENT_SETTINGS, [ref]$dm))) {{
    Write-Error 'EnumDisplaySettings fallo'
    exit 1
}}

$dm.dmDisplayFrequency = {frameRate}
$dm.dmFields = [DisplayHelper]::DM_DISPLAYFREQUENCY -bor [DisplayHelper]::DM_BITSPERPEL -bor [DisplayHelper]::DM_PELSWIDTH -bor [DisplayHelper]::DM_PELSHEIGHT

$result = [DisplayHelper]::ChangeDisplaySettings([ref]$dm, 0)

# Releer el modo activo para conocer la frecuencia REAL tras el cambio.
$dmVerify = New-Object DisplayHelper+DEVMODE
$dmVerify.dmSize = [System.Runtime.InteropServices.Marshal]::SizeOf($dmVerify)
[void][DisplayHelper]::EnumDisplaySettings($null, [DisplayHelper]::ENUM_CURRENT_SETTINGS, [ref]$dmVerify)

{reportLine}
";

            var encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script));

            try
            {
                new ProcessInSession().LaunchInSession(
                    sessionId,
                    // Ruta completa: CreateProcessAsUser con lpApplicationName no busca en
                    // PATH ("powershell.exe" a secas → error 2, la frecuencia nunca se aplicaba).
                    Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"),
                    $"-NoProfile -NonInteractive -EncodedCommand {encoded}",
                    waitForExit: true,
                    waitTimeoutMs: 15_000);

                LogDisplayFrequencyResult(resultFile, frameRate, sessionId);
            }
            catch (Exception ex)
            {
                Logger.Warning($"[RDP] No se pudo cambiar frecuencia de display en sesion {sessionId}: {ex.Message}");
            }
        }

        /// <summary>
        /// Lee y borra el fichero de resultado escrito por el script de ApplyDisplayFrequency
        /// y logea el codigo DISP_CHANGE_* y la frecuencia real verificada.
        /// </summary>
        static void LogDisplayFrequencyResult(string? resultFile, int requestedHz, uint sessionId)
        {
            if (resultFile is null)
            {
                Logger.Log($"[RDP] ChangeDisplaySettings({requestedHz}Hz) lanzado en sesion {sessionId} (sin verificacion).");
                return;
            }

            try
            {
                if (!File.Exists(resultFile))
                {
                    Logger.Warning($"[RDP] ChangeDisplaySettings en sesion {sessionId}: " +
                                   "sin fichero de resultado — el script no llego a completarse.");
                    return;
                }

                var parts = File.ReadAllText(resultFile).Trim().Split(';');
                try { File.Delete(resultFile); } catch { /* temp del usuario, no critico */ }

                int code = int.TryParse(parts[0], out var c) ? c : int.MinValue;
                string actualHz = parts.Length > 1 ? parts[1] : "?";

                if (code == 0 && actualHz == requestedHz.ToString())
                    Logger.Log($"[RDP] ChangeDisplaySettings OK — display a {actualHz}Hz en sesion {sessionId}.");
                else if (code == 0)
                    Logger.Warning($"[RDP] ChangeDisplaySettings devolvio OK pero el display reporta " +
                                   $"{actualHz}Hz (pedido {requestedHz}Hz) en sesion {sessionId}.");
                else
                    Logger.Warning($"[RDP] ChangeDisplaySettings fallo en sesion {sessionId}: " +
                                   $"{DispChangeName(code)} (display a {actualHz}Hz).");
            }
            catch (Exception ex)
            {
                Logger.Warning($"[RDP] No se pudo leer resultado de ChangeDisplaySettings: {ex.Message}");
            }
        }

        static string DispChangeName(int code) => code switch
        {
            0 => "DISP_CHANGE_SUCCESSFUL",
            1 => "DISP_CHANGE_RESTART",
            -1 => "DISP_CHANGE_FAILED",
            -2 => "DISP_CHANGE_BADMODE",
            -3 => "DISP_CHANGE_NOTUPDATED",
            -4 => "DISP_CHANGE_BADFLAGS",
            -5 => "DISP_CHANGE_BADPARAM",
            -6 => "DISP_CHANGE_BADDUALVIEW",
            _ => $"codigo desconocido ({code})"
        };

        // ── Enumeracion de sesiones ───────────────────────────────────────────────

        /// <summary>Estado WTS_CONNECTSTATE_CLASS.WTSActive.</summary>
        const int WtsActive = 0;

        /// <param name="activeOnly">
        /// true → solo sesiones en estado Active (logon completado, cliente conectado).
        /// Imprescindible al esperar una sesion nueva: sin esto, una sesion
        /// desconectada antigua del mismo usuario (WinStationName vacio, proto 0)
        /// se cuela por el modo ampliado y se da por creada una sesion que el
        /// cliente nunca llego a conectar.
        /// </param>
        static int FindSessionByUsername(string username, bool rdpOnly = true, bool logSessions = true,
                                         bool activeOnly = false)
        {
            foreach (var info in EnumerateSessions(logSessions))
            {
                if (!string.Equals(info.Username, username, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (activeOnly && info.State != WtsActive)
                    continue;

                bool isRdp = info.Protocol == 2 ||
                             info.WinStationName.StartsWith("RDP", StringComparison.OrdinalIgnoreCase);
                bool isConsole = info.WinStationName.Equals("Console", StringComparison.OrdinalIgnoreCase);

                if (isRdp || (!rdpOnly && !isConsole))
                    return info.SessionId;
            }
            return -1;
        }

        static List<RdpSessionInfo> EnumerateSessions(bool logSessions)
        {
            var sessions = new List<RdpSessionInfo>();

            if (!WTSEnumerateSessions(IntPtr.Zero, 0, 1, out IntPtr pSessions, out int count))
            {
                Logger.Error($"[RDP] WTSEnumerateSessions error: {Marshal.GetLastWin32Error()}");
                return sessions;
            }

            int structSize = Marshal.SizeOf<WTS_SESSION_INFO>();
            IntPtr cur = pSessions;

            try
            {
                for (int i = 0; i < count; i++)
                {
                    var raw = Marshal.PtrToStructure<WTS_SESSION_INFO>(cur);
                    cur += structSize;
                    var user = QuerySessionString(raw.SessionId, WTS_INFO_CLASS.WTSUserName);
                    var domain = QuerySessionString(raw.SessionId, WTS_INFO_CLASS.WTSDomainName);
                    var protocol = QuerySessionProtocol(raw.SessionId);

                    if (logSessions)
                        Logger.Log($"[RDP] Sesion {raw.SessionId} ({raw.pWinStationName}) " +
                                   $"proto={protocol} usuario='{FormatPrincipal(domain, user)}'");

                    sessions.Add(new RdpSessionInfo(
                        raw.SessionId, raw.pWinStationName ?? "",
                        raw.State, user, domain, protocol));
                }
            }
            finally
            {
                WTSFreeMemory(pSessions);
            }

            return sessions;
        }

        static string QuerySessionString(int sessionId, WTS_INFO_CLASS infoClass)
        {
            if (!WTSQuerySessionInformation(IntPtr.Zero, sessionId, infoClass,
                    out IntPtr ppBuffer, out _))
                return "";
            try { return Marshal.PtrToStringUni(ppBuffer) ?? ""; }
            finally { WTSFreeMemory(ppBuffer); }
        }

        static int QuerySessionProtocol(int sessionId)
        {
            if (!WTSQuerySessionInformation(IntPtr.Zero, sessionId,
                    WTS_INFO_CLASS.WTSClientProtocolType, out IntPtr ppProto, out _))
                return -1;
            try { return Marshal.ReadInt16(ppProto); }
            finally { WTSFreeMemory(ppProto); }
        }

        // ── Estado de bloqueo de sesion (lock screen) ─────────────────────────────

        /// <summary>
        /// Lee WTSINFOEX_LEVEL1.SessionFlags de la sesion. 0 = bloqueada (lock/secure
        /// desktop), 1 = desbloqueada, -1 = desconocido. Devuelve el flag crudo o -1
        /// si la consulta falla.
        /// <para>
        /// CRITICO para el encoder: si Sunshine arranca mientras la sesion esta
        /// bloqueada, su sondeo de encoders falla con "Failed to Open Input Desktop
        /// [0x5]" / "Failed to locate an output device", descarta amdvce (h264_amf) y
        /// cae a libx264 (CPU) para TODA su vida → 4K por software = diente de sierra
        /// 60→8 fps. El SessionFlags se lee directo del buffer (offset 16): evita
        /// marshalar el struct WTSINFOEXW completo con sus LARGE_INTEGER.
        /// </para>
        /// </summary>
        static int QuerySessionLockFlag(uint sessionId)
        {
            if (!WTSQuerySessionInformation(IntPtr.Zero, (int)sessionId,
                    WTS_INFO_CLASS.WTSSessionInfoEx, out IntPtr buf, out int bytes))
                return -1;
            try
            {
                // Layout WTSINFOEXW: DWORD Level(0) + 4 pad → union en offset 8:
                // SessionId(8) SessionState(12) SessionFlags(16).
                if (bytes < 20) return -1;
                return Marshal.ReadInt32(buf, 16);
            }
            finally { WTSFreeMemory(buf); }
        }

        /// <summary>true si la sesion esta en el lock/secure desktop.</summary>
        public static bool IsSessionLocked(uint sessionId) =>
            QuerySessionLockFlag(sessionId) == WtsSessionStateLock;

        /// <summary>
        /// Espera a que la sesion quede desbloqueada (SessionFlags=1) hasta
        /// <paramref name="timeoutMs"/>. Debe llamarse ANTES de lanzar Sunshine para
        /// que el sondeo de encoders encuentre la GPU y use h264_amf en vez de caer a
        /// libx264. Si el flag es desconocido (-1) NO bloquea (asume usable): mejor
        /// arrancar que colgarse para siempre. Devuelve true si quedo desbloqueada.
        /// </summary>
        public static bool WaitForSessionUnlocked(uint sessionId, int timeoutMs = 20_000)
        {
            var sw = Stopwatch.StartNew();
            int flag = QuerySessionLockFlag(sessionId);
            while (flag == WtsSessionStateLock && sw.ElapsedMilliseconds < timeoutMs)
            {
                Thread.Sleep(500);
                flag = QuerySessionLockFlag(sessionId);
            }

            bool unlocked = flag != WtsSessionStateLock;   // 1 (unlock) o -1 (unknown)
            if (unlocked)
                Logger.Log($"[RDP] Sesion {sessionId} desbloqueada (flag={flag}) tras {sw.ElapsedMilliseconds} ms.");
            else
                Logger.Warning($"[RDP] Sesion {sessionId} SIGUE bloqueada tras {timeoutMs} ms: " +
                               "Sunshine puede caer a encode software (libx264). Revisa lock screen.");
            return unlocked;
        }

        /// <summary>
        /// Desactiva el auto-bloqueo por inactividad a nivel maquina para que la sesion
        /// del stream no caiga al lock screen (causa de "Failed to Open Input Desktop
        /// [0x5]" → encode por software). Idempotente.
        /// <list type="bullet">
        ///   <item>InactivityTimeoutSecs=0 → "Limite de inactividad de la maquina" = 0
        ///         = nunca bloquea por idle (machine-wide, aplica al stream user).</item>
        ///   <item>DisableLockWorkstation=1 → impide el bloqueo manual/programatico.</item>
        /// </list>
        /// </summary>
        public static void PreventSessionLock()
        {
            const string system = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System";
            WriteRegistry(system, "InactivityTimeoutSecs", 0, RegistryValueKind.DWord);
            WriteRegistry(system, "DisableLockWorkstation", 1, RegistryValueKind.DWord);
        }

        // ── Escritorio Remoto nativo (reemplazo casero de RDPWrapper) ─────────────

        /// <summary>
        /// Habilita Escritorio Remoto de forma NATIVA, sin RDPWrapper/TermWrap ni ningun
        /// programa externo. Suficiente para UNA sesion RDP simultanea (el limite de
        /// Windows cliente): la sesion de stream toma la unica sesion disponible. Si hay
        /// un usuario en la consola fisica, Windows lo desconecta — limitacion aceptada
        /// al renunciar al parche de termsrv.dll que permitiria sesiones concurrentes.
        /// <para>
        /// Pasos (idempotentes; se ejecutan antes de cada creacion de sesion):
        ///   1. fDenyTSConnections = 0        → termsrv acepta conexiones RDP entrantes.
        ///   2. RDP-Tcp\fEnableWinStation = 1 → listener RDP-Tcp activo.
        ///   3. TermService arrancado (y no Deshabilitado): el listener es trigger-start,
        ///      pero si algun endurecimiento previo dejo el servicio parado/off la
        ///      conexion loopback fallaria.
        /// </para>
        /// NO se abre el puerto 3389 en el firewall: la conexion es loopback (127.0.0.2)
        /// y el loopback no atraviesa Windows Firewall, asi que RDP no queda expuesto a la
        /// red. NO se toca el nivel NLA: mstsc y FreeRDP negocian CredSSP con las opciones
        /// del .rdp. Es NO destructivo: si RDPWrapper/TermWrap siguen instalados, estos
        /// cambios son inocuos y su parche de multi-sesion se conserva intacto.
        /// </summary>
        public static void EnsureRemoteDesktopEnabled()
        {
            WriteRegistry(@"SYSTEM\CurrentControlSet\Control\Terminal Server",
                          "fDenyTSConnections", 0, RegistryValueKind.DWord);
            WriteRegistry(@"SYSTEM\CurrentControlSet\Control\Terminal Server\WinStations\RDP-Tcp",
                          "fEnableWinStation", 1, RegistryValueKind.DWord);

            EnsureTermServiceRunning();
        }

        /// <summary>
        /// Garantiza que el servicio TermService (Servicios de Escritorio Remoto) esta en
        /// ejecucion. Si estuviera Deshabilitado (Start=4) lo sube a Manual (Start=3) via
        /// registro antes de arrancarlo. Best-effort: los fallos se logean sin lanzar,
        /// para no abortar el arranque de la sesion por un caso raro de endurecimiento.
        /// </summary>
        static void EnsureTermServiceRunning()
        {
            try
            {
                // Start: 2=Automatic, 3=Manual, 4=Disabled. Si estaba Deshabilitado, subir
                // a Manual (el listener RDP-Tcp lo dispara por trigger de todos modos).
                using (var key = Registry.LocalMachine.OpenSubKey(
                           @"SYSTEM\CurrentControlSet\Services\TermService", writable: true))
                {
                    if (key?.GetValue("Start") is int start && start == 4)
                    {
                        key.SetValue("Start", 3, RegistryValueKind.DWord);
                        Logger.Log("[RDP] TermService estaba Deshabilitado → cambiado a Manual.");
                    }
                }

                using var sc = new ServiceController("TermService");
                sc.Refresh();
                if (sc.Status != ServiceControllerStatus.Running &&
                    sc.Status != ServiceControllerStatus.StartPending)
                {
                    sc.Start();
                    sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(15));
                    Logger.Log("[RDP] TermService arrancado (Escritorio Remoto nativo).");
                }
            }
            catch (Exception ex)
            {
                Logger.Warning($"[RDP] No se pudo garantizar TermService en ejecucion: {ex.Message}. " +
                               "Si Escritorio Remoto esta apagado, la conexion loopback fallara; " +
                               "habilitalo en Sistema → Escritorio remoto o reinicia Windows.");
            }
        }

        // ── Helpers internos ──────────────────────────────────────────────────────

        /// <summary>
        /// Escribe un valor DWORD en HKLM. Traga excepciones y logea el resultado.
        /// </summary>
        static void WriteRegistry(string path, string name, int value, RegistryValueKind kind)
        {
            try
            {
                using var key = Registry.LocalMachine.CreateSubKey(path, writable: true)
                    ?? throw new InvalidOperationException($"no se pudo abrir HKLM\\{path}");
                key.SetValue(name, value, kind);
                Logger.Log($"[RDP] HKLM\\{path}\\{name} = {value}");
            }
            catch (Exception ex)
            {
                Logger.Warning($"[RDP] No se pudo escribir {name} en HKLM\\{path}: {ex.Message}");
            }
        }

        static bool SamePrincipal(RdpSessionInfo session, StreamUserConfig user)
        {
            if (!string.Equals(session.Username, user.Username, StringComparison.OrdinalIgnoreCase))
                return false;
            return string.IsNullOrWhiteSpace(NormalizeDomain(session.Domain)) ||
                   string.Equals(NormalizeDomain(session.Domain),
                                 NormalizeDomain(user.Domain),
                                 StringComparison.OrdinalIgnoreCase);
        }

        static string NormalizeDomain(string? domain) =>
            string.IsNullOrWhiteSpace(domain) || domain == "."
                ? Environment.MachineName
                : domain.Trim();

        static int NormalizeFrameRate(int frameRate) =>
            Math.Clamp(frameRate > 0 ? frameRate : 60, 1, 240);

        static int NormalizeColorDepth(int colorDepth) => colorDepth switch
        {
            8 or 15 or 16 or 24 or 32 => colorDepth,
            _ => 32
        };

        static string FormatPrincipal(StreamUserConfig user) =>
            FormatPrincipal(NormalizeDomain(user.Domain), user.Username);

        static string FormatPrincipal(string domain, string username) =>
            string.IsNullOrWhiteSpace(domain) ? username : $@"{domain}\{username}";
    }
}
