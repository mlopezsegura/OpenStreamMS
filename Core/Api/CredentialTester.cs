using System.Runtime.InteropServices;

namespace OpenStreamMS.Core.Api;

/// <summary>
/// Valida credenciales de usuario Windows usando LogonUser con LOGON_NETWORK.
/// No requiere privilegios especiales ni crea una sesión de escritorio.
/// </summary>
public static class CredentialTester
{
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool LogonUser(
        string lpszUsername,
        string lpszDomain,
        string lpszPassword,
        int    dwLogonType,
        int    dwLogonProvider,
        out    IntPtr phToken);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr hObject);

    const int LOGON32_LOGON_NETWORK    = 3;   // sin cargar perfil, solo valida credencial
    const int LOGON32_PROVIDER_DEFAULT = 0;

    public record Result(bool Success, string Message);

    /// <summary>
    /// Intenta un logon de red con las credenciales indicadas.
    /// Devuelve <c>Success=true</c> si son válidas.
    /// </summary>
    public static Result Test(string username, string domain, string password)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
            return new(false, "El usuario y la contraseña son obligatorios.");

        var effectiveDomain = string.IsNullOrWhiteSpace(domain) ? "." : domain;

        bool ok = LogonUser(username, effectiveDomain, password,
                            LOGON32_LOGON_NETWORK, LOGON32_PROVIDER_DEFAULT,
                            out var token);
        if (ok)
        {
            CloseHandle(token);
            return new(true, $"Credenciales válidas para {username}@{effectiveDomain}.");
        }

        int err = Marshal.GetLastWin32Error();
        string msg = err switch
        {
            1326 => "Usuario o contraseña incorrectos.",
            1327 => "La cuenta no tiene permisos de inicio de sesión en red.",
            1328 => "La restricción de tiempo no permite el acceso ahora.",
            1329 => "Esta estación de trabajo no está autorizada.",
            1330 => "La contraseña ha expirado. Cámbiala antes de continuar.",
            1331 => "La cuenta está deshabilitada.",
            1332 => "No se encontró ninguna cuenta con ese nombre.",
            1314 => "El proceso no tiene privilegios suficientes (requiere ejecutarse como SYSTEM).",
            _    => $"Error de Windows #{err}."
        };

        return new(false, msg);
    }
}
