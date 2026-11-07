using System.Runtime.InteropServices;
using OpenStreamMS.Services.Localization;

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
            return new(false, RequestCulture.T("credentials.required"));

        var effectiveDomain = string.IsNullOrWhiteSpace(domain) ? "." : domain;

        bool ok = LogonUser(username, effectiveDomain, password,
                            LOGON32_LOGON_NETWORK, LOGON32_PROVIDER_DEFAULT,
                            out var token);
        if (ok)
        {
            CloseHandle(token);
            return new(true, RequestCulture.T("credentials.valid", username, effectiveDomain));
        }

        int err = Marshal.GetLastWin32Error();
        string msg = err switch
        {
            1326 => RequestCulture.T("credentials.invalid"),
            1327 => RequestCulture.T("credentials.noNetworkLogon"),
            1328 => RequestCulture.T("credentials.timeRestriction"),
            1329 => RequestCulture.T("credentials.workstationNotAuthorized"),
            1330 => RequestCulture.T("credentials.passwordExpired"),
            1331 => RequestCulture.T("credentials.accountDisabled"),
            1332 => RequestCulture.T("credentials.accountNotFound"),
            1314 => RequestCulture.T("credentials.insufficientPrivileges"),
            _    => RequestCulture.T("credentials.windowsError", err)
        };

        return new(false, msg);
    }
}