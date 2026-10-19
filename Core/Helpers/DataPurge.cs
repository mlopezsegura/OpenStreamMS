using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;

namespace OpenStreamMS.Core.Helpers
{
    /// <summary>
    /// Borra todo lo que OpenStreamMS deja fuera de su carpeta de instalación
    /// (<c>OpenStreamMS.exe --purge-data</c>, desde los desinstaladores). La carpeta de
    /// instalación la borra el desinstalador. Idempotente: se puede repetir sin efectos.
    /// </summary>
    public static class DataPurge
    {
        [DllImport("wtsapi32.dll", SetLastError = true)]
        static extern bool WTSEnumerateSessions(IntPtr server, int reserved, int version, out IntPtr sessions, out int count);

        [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "WTSQuerySessionInformationW")]
        static extern bool WTSQuerySessionInformation(IntPtr server, uint sessionId, int infoClass, out IntPtr buffer, out int bytes);

        [DllImport("wtsapi32.dll")]
        static extern void WTSFreeMemory(IntPtr memory);

        [DllImport("wtsapi32.dll", SetLastError = true)]
        static extern bool WTSLogoffSession(IntPtr server, uint sessionId, bool wait);

        [StructLayout(LayoutKind.Sequential)]
        struct WTS_SESSION_INFO { public uint SessionId; public IntPtr WinStationName; public int State; }

        const int WTSUserName = 5;
        const string ProfileList = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList";

        public static int Run()
        {
            Console.WriteLine("[Purge] Borrando los datos de OpenStreamMS fuera de la carpeta de instalación...");

            // Lo que los puestos cambian en el sistema se restaura antes de borrar los ficheros
            // de ProgramData donde se apunta (lo mismo hace --uninstall; repetirlo no hace nada).
            Step("Restaurar ratones/teclados, bloqueo de consola y derecho de apagar", () =>
            {
                OpenStreamMS.Services.Seats.UsbCapture.RestoreAll();
                OpenStreamMS.Services.Seats.ConsoleLockGuard.Release();
                ShutdownRight.Restore();
            });

            Step("Cerrar las sesiones de los usuarios osms-", LogoffIsolatedSessions);
            Step("Eliminar usuarios osms- y sus perfiles", RemoveIsolatedUsers);
            Step("Eliminar carpetas _OpenStreamMS_Stream de los perfiles", RemoveStreamProfiles);

            var programData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "OpenStreamMS");
            Step($"Eliminar {programData}", () => DeleteDir(programData));
            Step(@"Eliminar HKLM\SOFTWARE\OpenStreamMS", () =>
                Registry.LocalMachine.DeleteSubKeyTree(@"SOFTWARE\OpenStreamMS", throwOnMissingSubKey: false));
            Step("Eliminar ficheros temporales", RemoveTempFiles);

            Console.WriteLine("[Purge] Completado.");
            return 0;
        }

        static void Step(string what, Action action)
        {
            try { action(); Console.WriteLine($"[Purge] {what}: OK"); }
            catch (Exception ex) { Console.WriteLine($"[Purge] {what}: {ex.Message}"); }
        }

        /// <summary>Un perfil cargado no se puede borrar: primero se cierra su sesión.</summary>
        static void LogoffIsolatedSessions()
        {
            if (!WTSEnumerateSessions(IntPtr.Zero, 0, 1, out var buffer, out int count)) return;
            try
            {
                int size = Marshal.SizeOf<WTS_SESSION_INFO>();
                for (int i = 0; i < count; i++)
                {
                    var info = Marshal.PtrToStructure<WTS_SESSION_INFO>(buffer + i * size);
                    if (!WTSQuerySessionInformation(IntPtr.Zero, info.SessionId, WTSUserName, out var name, out _)) continue;
                    string user;
                    try { user = Marshal.PtrToStringUni(name) ?? ""; }
                    finally { WTSFreeMemory(name); }

                    if (user.StartsWith("osms-", StringComparison.OrdinalIgnoreCase))
                    {
                        WTSLogoffSession(IntPtr.Zero, info.SessionId, wait: true);
                        Console.WriteLine($"[Purge]   sesión {info.SessionId} de '{user}' cerrada");
                    }
                }
            }
            finally { WTSFreeMemory(buffer); }
        }

        /// <summary>Los usuarios aislados salen del Guid de cada sesión (sessions.json).</summary>
        static void RemoveIsolatedUsers()
        {
            var file = OpenStreamMS.Core.Api.StreamSessionService.SessionsFilePath;
            if (!File.Exists(file)) return;

            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            foreach (var s in doc.RootElement.EnumerateArray())
            {
                if (!s.TryGetProperty("Id", out var idProp) || !Guid.TryParse(idProp.GetString(), out var id)) continue;
                var username = IsolatedUserManager.GetUsername(id);
                try { new System.Security.Principal.NTAccount(username).Translate(typeof(System.Security.Principal.SecurityIdentifier)); }
                catch (System.Security.Principal.IdentityNotMappedException) { continue; }   // sesión sin usuario aislado

                try
                {
                    for (int attempt = 0; attempt < 5; attempt++)
                    {
                        if (IsolatedUserManager.TryDeleteProfile(id, out _)) break;
                        Thread.Sleep(2000);   // profsvc tarda unos segundos en soltar el perfil tras el logoff
                    }
                    IsolatedUserManager.RemoveUser(id);
                    Console.WriteLine($"[Purge]   {username} eliminado");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Purge]   {username}: {ex.Message}");
                }
            }
        }

        static void RemoveStreamProfiles()
        {
            using var list = Registry.LocalMachine.OpenSubKey(ProfileList);
            if (list is null) return;
            foreach (var sid in list.GetSubKeyNames())
            {
                using var key = list.OpenSubKey(sid);
                if (key?.GetValue("ProfileImagePath") is not string path) continue;
                var dir = Path.Combine(Environment.ExpandEnvironmentVariables(path), "_OpenStreamMS_Stream");
                if (Directory.Exists(dir))
                {
                    DeleteDir(dir);
                    Console.WriteLine($"[Purge]   {dir}");
                }
            }
        }

        /// <summary>
        /// Temporales del servicio (SYSTEM): .rdp de mstsc, descargas de drivers y de RDP Wrapper
        /// y el log de reserva. También los del usuario que desinstala.
        /// </summary>
        static void RemoveTempFiles()
        {
            var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            var dirs = new[] { Path.Combine(windows, "SystemTemp"), Path.Combine(windows, "Temp"), Path.GetTempPath() };
            foreach (var dir in dirs.Distinct(StringComparer.OrdinalIgnoreCase).Where(Directory.Exists))
                foreach (var pattern in new[] { "osms_*.rdp", "OpenStreamMS_*", "openstream*.log" })
                    foreach (var f in Directory.EnumerateFileSystemEntries(dir, pattern))
                    {
                        try
                        {
                            if (Directory.Exists(f)) Directory.Delete(f, recursive: true);
                            else File.Delete(f);
                        }
                        catch { /* en uso: se queda */ }
                    }
        }

        static void DeleteDir(string dir)
        {
            if (!Directory.Exists(dir)) return;
            // Los ficheros de solo lectura harían fallar Directory.Delete. Sin entrar en
            // junctions: en un perfil de usuario podrían apuntar a cualquier sitio. (Directory.Delete
            // tampoco las sigue: borra el enlace, no el destino.)
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                AttributesToSkip      = FileAttributes.ReparsePoint,
                IgnoreInaccessible    = true,
            };
            foreach (var f in Directory.EnumerateFiles(dir, "*", options))
                try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
            Directory.Delete(dir, recursive: true);
        }
    }
}
