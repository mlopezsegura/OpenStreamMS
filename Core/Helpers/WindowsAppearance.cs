using System.Security.Principal;
using Microsoft.Win32;

namespace OpenStreamMS.Core.Helpers
{
    /// <summary>
    /// Color de acento y modo claro/oscuro de Windows del usuario de la consola, para el
    /// tema "Windows" del panel. El servicio corre como SYSTEM, así que se lee del registro
    /// de ese usuario en HKEY_USERS (cargado mientras tiene la sesión iniciada). Solo lectura.
    /// </summary>
    public static class WindowsAppearance
    {
        const string AccentKey      = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent";
        const string DwmKey         = @"Software\Microsoft\Windows\DWM";
        const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

        /// <summary>Acento como "#rrggbb" y si las apps usan el modo claro; null lo que no se pueda leer.</summary>
        public static (string? Accent, bool? LightTheme) Read()
        {
            try
            {
                if (!ConsoleSession.TryGetUser(out _, out var domain, out var user))
                    return (null, null);
                string account = string.IsNullOrEmpty(domain) ? user : $@"{domain}\{user}";
                string sid = new NTAccount(account).Translate(typeof(SecurityIdentifier)).Value;

                string? accent = null;
                // AccentPalette: 8 colores RGBA del más claro al más oscuro; el 4.º es el acento elegido.
                using (var key = Registry.Users.OpenSubKey($@"{sid}\{AccentKey}"))
                {
                    if (key?.GetValue("AccentPalette") is byte[] { Length: >= 16 } palette)
                        accent = Hex(palette[12], palette[13], palette[14]);
                }
                // Si no hay paleta, el color de las barras de título (DWORD 0xAABBGGRR).
                if (accent is null)
                {
                    using var dwm = Registry.Users.OpenSubKey($@"{sid}\{DwmKey}");
                    if (dwm?.GetValue("AccentColor") is int abgr)
                        accent = Hex((byte)abgr, (byte)(abgr >> 8), (byte)(abgr >> 16));
                }

                bool? light = null;
                using (var key = Registry.Users.OpenSubKey($@"{sid}\{PersonalizeKey}"))
                {
                    if (key?.GetValue("AppsUseLightTheme") is int v) light = v != 0;
                }
                return (accent, light);
            }
            catch
            {
                return (null, null);   // sin usuario en la consola o sin permiso: el panel usa su acento
            }
        }

        static string Hex(byte r, byte g, byte b) => $"#{r:x2}{g:x2}{b:x2}";
    }
}
