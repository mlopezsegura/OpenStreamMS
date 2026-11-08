using Microsoft.Win32;
using OpenStreamMS.Services;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using OpenStreamMS.Services.Localization;

namespace OpenStreamMS.Core.Helpers;

/// <summary>
/// Aislamiento nativo de sesiones SIN software de terceros: cada sesión de stream
/// marcada como aislada corre bajo un usuario local dedicado (<c>osms-xxxxxxxxxxxx</c>)
/// que el servicio crea y gestiona automáticamente.
///
/// QUÉ APORTA:
///   - Perfil propio (C:\Users\osms-...) → filesystem aislado con ACLs NTFS reales,
///     sin virtualización.
///   - HKCU propio → registro aislado de verdad.
///   - Sesión WTS, audio y desktop propios → el mismo aislamiento que dos usuarios
///     físicos distintos. Steam/Chrome/etc. corren en paralelo sin lock files.
///   - Sin driver kernel, sin licencias, sin binarios externos.
///
/// SEGURIDAD:
///   - Contraseña aleatoria (24 chars, 4 clases) ROTADA en cada arranque y nunca
///     persistida en disco — solo vive en memoria durante el arranque de la sesión.
///   - Usuario oculto de la pantalla de login (SpecialAccounts\UserList).
///   - Miembro únicamente de Users + Remote Desktop Users. Donde no existe ese grupo
///     (Windows Home), recibe directamente el derecho de inicio de sesión remoto y
///     acceso a la escucha RDP-Tcp; se le quitan al borrar la cuenta.
///
/// CICLO DE VIDA:
///   - EnsureUser            → crea el usuario (o rota la contraseña si ya existe).
///   - TryDeleteProfile      → borra C:\Users\osms-... + hive de registro (modo efímero).
///   - RemoveUser            → perfil + cuenta + entrada de registro (al eliminar la sesión,
///                             salvo que se pida conservarlo para reutilizarlo en otra).
///   - ListOrphans/DeleteOrphan → usuarios y perfiles osms-* que ya no usa ninguna sesión.
///
/// El nombre sale del Guid de la sesión (<see cref="GetUsername"/>) salvo que la sesión
/// reutilice un usuario conservado (StreamSession.IsolatedUsername).
/// </summary>
internal static class IsolatedUserManager
{
    internal sealed record IsolatedCredentials(string Username, string Password);

    // ── P/Invoke ─────────────────────────────────────────────────────────────

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    static extern int NetUserAdd(string? servername, int level, ref USER_INFO_1 buf, out int parmErr);

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    static extern int NetUserSetInfo(string? servername, string username, int level,
                                     ref USER_INFO_1003 buf, out int parmErr);

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    static extern int NetUserDel(string? servername, string username);

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    static extern int NetLocalGroupAddMembers(string? servername, string groupname, int level,
                                              ref LOCALGROUP_MEMBERS_INFO_3 buf, int totalentries);

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    static extern int NetUserEnum(string? servername, int level, int filter, out IntPtr bufptr, int prefmaxlen,
                                  out int entriesread, out int totalentries, ref int resumeHandle);

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    static extern int NetUserGetLocalGroups(string? servername, string username, int level, int flags,
                                            out IntPtr bufptr, int prefmaxlen, out int entriesread, out int totalentries);

    [DllImport("netapi32.dll")]
    static extern int NetApiBufferFree(IntPtr buffer);

    [DllImport("userenv.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool DeleteProfile(string lpSidString, string? lpProfilePath, string? lpComputerName);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct USER_INFO_1
    {
        public string  usri1_name;
        public string  usri1_password;
        public uint    usri1_password_age;
        public uint    usri1_priv;
        public string? usri1_home_dir;
        public string? usri1_comment;
        public uint    usri1_flags;
        public string? usri1_script_path;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct USER_INFO_1003
    {
        public string usri1003_password;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct LOCALGROUP_MEMBERS_INFO_3
    {
        public string lgrmi3_domainandname;
    }

    const int  NERR_Success          = 0;
    const int  NERR_UserNotFound     = 2221;
    const int  NERR_UserExists       = 2224;
    const int  ERROR_MEMBER_IN_ALIAS = 1378;   // ya es miembro del grupo
    const int  ERROR_FILE_NOT_FOUND  = 2;
    const int  ERROR_ACCESS_DENIED   = 5;
    const int  ERROR_MORE_DATA       = 234;
    const int  FILTER_NORMAL_ACCOUNT = 2;
    const int  MAX_PREFERRED_LENGTH  = -1;
    const uint USER_PRIV_USER        = 1;
    const uint UF_SCRIPT             = 0x0001;  // obligatorio en NetUserAdd
    const uint UF_DONT_EXPIRE_PASSWD = 0x10000;

    const string UserListKey =
        @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon\SpecialAccounts\UserList";

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Nombre SAM determinista del usuario aislado de una sesión:
    /// "osms-" + 12 hex del SHA-256 del Guid (17 chars, bajo el límite SAM de 20).
    /// </summary>
    internal static string GetUsername(Guid sessionId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(sessionId.ToString("N")));
        return "osms-" + Convert.ToHexString(hash, 0, 6).ToLowerInvariant();
    }

    /// <summary>
    /// Crea el usuario local dedicado de la sesión (o rota su contraseña si ya existe),
    /// lo añade a Users + Remote Desktop Users y lo oculta de la pantalla de login.
    /// Devuelve credenciales listas para FreeRDP. La contraseña no se persiste nunca.
    /// </summary>
    internal static IsolatedCredentials EnsureUser(string username, string sessionName)
    {
        if (!IsManagedName(username))
            throw new InvalidOperationException(BackendText.Get("isolatedUser.notManaged", username));
        var password = GeneratePassword();

        var info = new USER_INFO_1
        {
            usri1_name     = username,
            usri1_password = password,
            usri1_priv     = USER_PRIV_USER,
            usri1_flags    = UF_SCRIPT | UF_DONT_EXPIRE_PASSWD,
            usri1_comment  = $"OpenStreamMS: usuario aislado de la sesión de stream '{sessionName}'. " +
                             "Gestionado automáticamente; no modificar a mano.",
        };

        int rc = NetUserAdd(null, 1, ref info, out _);
        switch (rc)
        {
            case NERR_Success:
                Logger.Log(BackendText.Get("isolatedUser.userCreated", username, sessionName));
                break;

            case NERR_UserExists:
                var reset = new USER_INFO_1003 { usri1003_password = password };
                int rcSet = NetUserSetInfo(null, username, 1003, ref reset, out _);
                if (rcSet != NERR_Success)
                    throw new InvalidOperationException(
                        BackendText.Get("isolatedUser.passwordRotationFailed", username, rcSet));
                Logger.Log(BackendText.Get("isolatedUser.passwordRotated", username));
                break;

            default:
                throw new InvalidOperationException(
                    BackendText.Get("isolatedUser.netUserAddFailed", username, rc));
        }

        // NetUserAdd NO añade a ningún grupo: sin Users no hay logon interactivo,
        // sin Remote Desktop Users termsrv rechaza la conexión de FreeRDP.
        AddToLocalGroup(username, WellKnownSidType.BuiltinUsersSid, required: true);
        if (!AddToLocalGroup(username, WellKnownSidType.BuiltinRemoteDesktopUsersSid, required: false))
            GrantRemoteLogonRight(username);
        HideFromLoginScreen(username);
        RemoveLegacySteamDeny(username);

        return new IsolatedCredentials(username, password);
    }

    /// <summary>
    /// Borra el perfil del usuario aislado (directorio + hive NTUSER.DAT) vía
    /// DeleteProfileW. Devuelve true si el perfil quedó eliminado o no existía.
    /// Falla (false + error) mientras el perfil siga cargado tras el logoff.
    /// </summary>
    internal static bool TryDeleteProfile(string username, out string? error)
    {
        error = null;
        var sid = TryGetSid(username);
        if (sid is null)
            return true; // el usuario no existe → no hay perfil que borrar
        return TryDeleteProfileBySid(sid, out error);
    }

    /// <summary>
    /// Borra un perfil por su SID. Sirve también cuando la cuenta ya no existe: el perfil
    /// sigue registrado en ProfileList con el SID de la cuenta borrada.
    /// </summary>
    static bool TryDeleteProfileBySid(string sid, out string? error)
    {
        error = null;
        if (DeleteProfile(sid, null, null))
            return true;

        int err = Marshal.GetLastWin32Error();
        if (err == ERROR_FILE_NOT_FOUND)
            return true; // sin perfil en disco: nada que borrar

        error = err == ERROR_ACCESS_DENIED
            ? $"error {err} (perfil aún cargado, el logoff no ha terminado)"
            : $"error {err}";
        return false;
    }

    /// <summary>
    /// Borrado de perfil con reintentos en segundo plano. profsvc tarda unos segundos
    /// en descargar el hive tras el logoff, así que el primer intento suele fallar
    /// con ACCESS_DENIED. Backoff incremental: 2+4+6+8+10+12 s (~42 s máx).
    /// </summary>
    internal static async Task DeleteProfileWithRetryAsync(string username, string sessionName)
    {
        for (int attempt = 1; attempt <= 6; attempt++)
        {
            await Task.Delay(TimeSpan.FromSeconds(attempt * 2));

            if (TryDeleteProfile(username, out var error))
            {
                Logger.Log(BackendText.Get("isolatedUser.profileDeleted", username, sessionName));
                return;
            }

            Logger.Log(BackendText.Get("isolatedUser.profileNotReleased", username, error, attempt));
        }

        Logger.Warning(BackendText.Get("isolatedUser.profileDeleteFailed", username));
    }

    /// <summary>
    /// Elimina por completo el usuario aislado: perfil, cuenta local y entrada
    /// de ocultación del login. Para usar al borrar la sesión de stream.
    /// </summary>
    internal static void RemoveUser(string username)
    {
        if (!IsManagedName(username))
            throw new InvalidOperationException(BackendText.Get("isolatedUser.notManaged", username));

        // Nunca borrar la cuenta con su sesión de Windows viva: la sesión queda huérfana y a
        // medio cerrar, y Terminal Services (y el apagado del equipo) se bloquean esperándola.
        foreach (var live in RdpSessionCreator.FindUserSessions(username))
        {
            if (RdpSessionCreator.LogoffAndWait(live, TimeSpan.FromSeconds(30))) continue;
            Logger.Warning(BackendText.Get("isolatedUser.sessionOpen", username, live));
            return;
        }

        var sid = TryGetSid(username);
        bool profileGone = TryDeleteProfile(username, out var error);

        RemoveLegacySteamDeny(username);   // antes de borrar la cuenta: hace falta su SID

        // Derechos dados a la cuenta (inicio de sesión remoto en Home, apagar): sin esto
        // quedarían en la directiva local con un SID que ya no existe.
        if (sid is not null)
        {
            try
            {
                var sidObj = new SecurityIdentifier(sid);
                using (var policy = LsaPolicy.Open()) policy.RemoveAll(sidObj);
                SetWinStationAccess(sidObj, grant: false);
            }
            catch (Exception ex) { Logger.Warning(BackendText.Get("isolatedUser.errorRemovingRights", username, ex.Message)); }
        }

        int rc = NetUserDel(null, username);
        if (rc == NERR_Success)
            Logger.Log(BackendText.Get("isolatedUser.userDeleted", username));
        else if (rc != NERR_UserNotFound)
            Logger.Warning(BackendText.Get("isolatedUser.netUserDelFailed", username, rc));

        UnhideFromLoginScreen(username);

        // Justo tras el logoff profsvc aún tiene el perfil cargado y DeleteProfile falla:
        // antes eso dejaba la carpeta C:\Users\osms-... para siempre. Se reintenta por SID,
        // que sigue valiendo aunque la cuenta ya no exista.
        if (!profileGone && sid is not null)
        {
            Logger.Log(BackendText.Get("isolatedUser.profileStillLoaded", username, error));
            _ = DeleteProfileBySidWithRetryAsync(sid, username);
        }
    }

    static async Task DeleteProfileBySidWithRetryAsync(string sid, string username)
    {
        string? error = null;
        for (int attempt = 1; attempt <= 6; attempt++)
        {
            await Task.Delay(TimeSpan.FromSeconds(attempt * 2));
            if (TryDeleteProfileBySid(sid, out error))
            {
                Logger.Log(BackendText.Get("isolatedUser.profileDeleted", username));
                return;
            }
        }
        Logger.Warning(BackendText.Get("isolatedUser.profileDeleteRetryFailed", username, error ?? "unknown"));
    }

    // ── Usuarios y perfiles sin sesión ────────────────────────────────────────

    /// <summary>
    /// Usuario dedicado o perfil que ya no usa ninguna sesión. <see cref="Key"/> lo identifica
    /// para borrarlo: <c>user:osms-…</c> (cuenta, con o sin perfil), <c>sid:S-1-…</c> (perfil
    /// de una cuenta ya borrada) o <c>dir:osms-…</c> (carpeta suelta en C:\Users).
    /// </summary>
    internal sealed record OrphanEntry(string Key, string Name, bool AccountExists,
                                       string? ProfilePath, long? SizeBytes, bool SignedIn);

    /// <summary>
    /// Nombres de usuario que OpenStreamMS crea y puede gestionar: "osms-" y hasta 20 caracteres
    /// SAM sin separadores. Nada fuera de este patrón se crea, reutiliza ni borra.
    /// </summary>
    internal static bool IsManagedName(string? username) =>
        !string.IsNullOrEmpty(username) && username.Length <= 20 &&
        username.StartsWith("osms-", StringComparison.OrdinalIgnoreCase) &&
        username.IndexOfAny(['\\', '/', ':', '*', '?', '"', '<', '>', '|', '@', '.', ' ']) < 0;

    /// <summary>Cuentas locales osms-* que existen ahora mismo.</summary>
    internal static List<string> ListManagedAccounts()
    {
        var names = new List<string>();
        int resume = 0, rc;
        do
        {
            rc = NetUserEnum(null, 0, FILTER_NORMAL_ACCOUNT, out var buf, MAX_PREFERRED_LENGTH,
                             out int read, out _, ref resume);
            if (rc is not (NERR_Success or ERROR_MORE_DATA)) break;
            try
            {
                for (int i = 0; i < read; i++)
                {
                    var name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(buf, i * IntPtr.Size));
                    if (IsManagedName(name)) names.Add(name!);
                }
            }
            finally { NetApiBufferFree(buf); }
        } while (rc == ERROR_MORE_DATA);
        return names;
    }

    /// <summary>
    /// Usuarios dedicados sin sesión, perfiles de cuentas ya borradas y carpetas sueltas
    /// osms-* en el directorio de perfiles. <paramref name="inUse"/>: usuarios de las sesiones.
    /// </summary>
    internal static List<OrphanEntry> ListOrphans(IEnumerable<string> inUse, bool withSizes)
    {
        var used = new HashSet<string>(inUse, StringComparer.OrdinalIgnoreCase);
        var profiles = ReadManagedProfiles();                 // SID → ruta
        var result = new List<OrphanEntry>();
        var claimedSids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var name in ListManagedAccounts())
        {
            var sid = TryGetSid(name);
            if (sid is not null) claimedSids.Add(sid);
            if (used.Contains(name)) continue;
            var path = sid is not null && profiles.TryGetValue(sid, out var p) ? p : null;
            result.Add(new OrphanEntry($"user:{name}", name, true, path,
                                       withSizes ? DirectorySize(path) : null,
                                       RdpSessionCreator.FindUserSessions(name).Count > 0));
        }

        foreach (var (sid, path) in profiles)
        {
            if (claimedSids.Contains(sid)) continue;          // de una cuenta que existe
            result.Add(new OrphanEntry($"sid:{sid}", Path.GetFileName(path), false, path,
                                       withSizes ? DirectorySize(path) : null, false));
        }

        var registered = new HashSet<string>(profiles.Values, StringComparer.OrdinalIgnoreCase);
        var profilesDir = ProfilesDirectory();
        if (profilesDir is not null && Directory.Exists(profilesDir))
        {
            foreach (var dir in Directory.EnumerateDirectories(profilesDir, "osms-*"))
            {
                if (registered.Contains(dir)) continue;
                var leaf = Path.GetFileName(dir);
                result.Add(new OrphanEntry($"dir:{leaf}", leaf, false, dir,
                                           withSizes ? DirectorySize(dir) : null, false));
            }
        }
        return result;
    }

    /// <summary>
    /// Borra un elemento de <see cref="ListOrphans"/>. La clave se valida contra la lista
    /// recién calculada: nunca se borra nada que no sea un usuario o perfil osms-* sin sesión.
    /// </summary>
    internal static void DeleteOrphan(string key, IEnumerable<string> inUse)
    {
        var entry = ListOrphans(inUse, withSizes: false).FirstOrDefault(e => e.Key == key)
            ?? throw new InvalidOperationException(BackendText.Get("isolatedUser.cannotDelete"));
        if (entry.SignedIn)
            throw new InvalidOperationException(BackendText.Get("isolatedUser.signedIn", entry.Name));

        if (key.StartsWith("user:", StringComparison.Ordinal))
        {
            RemoveUser(entry.Name);
        }
        else if (key.StartsWith("sid:", StringComparison.Ordinal))
        {
            if (!TryDeleteProfileBySid(key[4..], out var error))
                throw new InvalidOperationException(BackendText.Get("isolatedUser.profileDeleteError", entry.Name, error));
            Logger.Log(BackendText.Get("isolatedUser.profileDeleted", entry.ProfilePath));
        }
        else
        {
            // Carpeta sin perfil registrado: ruta reconstruida desde el directorio de perfiles.
            var dir = Path.Combine(ProfilesDirectory()!, entry.Name);
            Directory.Delete(dir, recursive: true);
            Logger.Log(BackendText.Get("isolatedUser.orphanFolderDeleted", dir));
        }
    }

    /// <summary>Ruta del perfil de un usuario (null si nunca ha iniciado sesión o no existe).</summary>
    internal static string? GetProfilePath(string accountName)
    {
        var sid = TryGetSid(accountName);
        if (sid is null) return null;
        using var key = Registry.LocalMachine.OpenSubKey($@"{ProfileListKey}\{sid}");
        return key?.GetValue("ProfileImagePath") is string p ? Environment.ExpandEnvironmentVariables(p) : null;
    }

    static string? TryGetSid(string accountName)
    {
        try { return ((SecurityIdentifier)new NTAccount(accountName).Translate(typeof(SecurityIdentifier))).Value; }
        catch (IdentityNotMappedException) { return null; }
        catch (SystemException) { return null; }
    }

    const string ProfileListKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList";

    static string? ProfilesDirectory()
    {
        using var key = Registry.LocalMachine.OpenSubKey(ProfileListKey);
        return key?.GetValue("ProfilesDirectory") is string p ? Environment.ExpandEnvironmentVariables(p) : null;
    }

    /// <summary>Perfiles registrados cuya carpeta empieza por osms- (SID → ruta).</summary>
    static Dictionary<string, string> ReadManagedProfiles()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var list = Registry.LocalMachine.OpenSubKey(ProfileListKey);
        if (list is null) return map;
        foreach (var sid in list.GetSubKeyNames())
        {
            using var k = list.OpenSubKey(sid);
            if (k?.GetValue("ProfileImagePath") is not string raw) continue;
            if (sid.EndsWith(".bak", StringComparison.OrdinalIgnoreCase)) continue;   // copia de un perfil dañado
            var path = Environment.ExpandEnvironmentVariables(raw);
            if (Path.GetFileName(path).StartsWith("osms-", StringComparison.OrdinalIgnoreCase))
                map[sid] = path;
        }
        return map;
    }

    /// <summary>Tamaño en bytes sin seguir uniones (Application Data, etc.). Null si falla.</summary>
    static long? DirectorySize(string? path)
    {
        if (path is null || !Directory.Exists(path)) return null;
        try
        {
            var opts = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible    = true,
                AttributesToSkip      = FileAttributes.ReparsePoint,
            };
            long total = 0;
            foreach (var f in new DirectoryInfo(path).EnumerateFiles("*", opts)) total += f.Length;
            return total;
        }
        catch { return null; }
    }

    /// <summary>
    /// Sesión con la cuenta propia del usuario (no aislada): Windows solo deja iniciar sesión
    /// por Escritorio remoto a Administradores y a Remote Desktop Users. Una cuenta estándar
    /// fuera de ese grupo era rechazada (FreeRDP ERRCONNECT_ACCESS_DENIED, 0x20016) y la sesión
    /// no arrancaba. Si la cuenta es local y no tiene acceso, se añade a Remote Desktop Users
    /// (o, sin ese grupo, se le da el derecho). Las cuentas de dominio o de Microsoft no se tocan.
    /// </summary>
    internal static void EnsureRemoteLogonForOwnAccount(string username, string? domain)
    {
        if (string.IsNullOrWhiteSpace(username) || username.Contains('@') || username.Contains('\\')) return;
        if (!string.IsNullOrEmpty(domain) && domain != "." &&
            !domain.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase)) return;
        try
        {
            var groups = LocalGroupsOf(username);
            if (groups is null) return;   // no es una cuenta local de este equipo
            if (groups.Contains(GroupName(WellKnownSidType.BuiltinAdministratorsSid) ?? "") ||
                groups.Contains(GroupName(WellKnownSidType.BuiltinRemoteDesktopUsersSid) ?? "")) return;

            Logger.Log($"[RDP] '{username}' no es administrador ni está en Remote Desktop Users: se le da acceso por Escritorio remoto para que la sesión pueda iniciarse.");
            if (!AddToLocalGroup(username, WellKnownSidType.BuiltinRemoteDesktopUsersSid, required: false))
                GrantRemoteLogonRight(username);
        }
        catch (Exception ex)
        {
            Logger.Warning($"[RDP] No se pudo comprobar el acceso por Escritorio remoto de '{username}': {ex.Message}");
        }
    }

    /// <summary>Grupos locales (también indirectos) de una cuenta local, o null si no existe.</summary>
    static HashSet<string>? LocalGroupsOf(string username)
    {
        const int LG_INCLUDE_INDIRECT = 1;
        int rc = NetUserGetLocalGroups(null, username, 0, LG_INCLUDE_INDIRECT, out var buf,
                                       MAX_PREFERRED_LENGTH, out int read, out _);
        try
        {
            if (rc == NERR_UserNotFound) return null;
            if (rc != NERR_Success) throw new InvalidOperationException($"NetUserGetLocalGroups={rc}");
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < read; i++)
                if (Marshal.PtrToStringUni(Marshal.ReadIntPtr(buf, i * IntPtr.Size)) is { } name) set.Add(name);
            return set;
        }
        finally { if (buf != IntPtr.Zero) NetApiBufferFree(buf); }
    }

    /// <summary>Nombre localizado de un grupo integrado, o null si este Windows no lo tiene.</summary>
    static string? GroupName(WellKnownSidType groupSid)
    {
        try
        {
            var account = ((NTAccount)new SecurityIdentifier(groupSid, null).Translate(typeof(NTAccount))).Value;
            return account.Contains('\\') ? account[(account.IndexOf('\\') + 1)..] : account;
        }
        catch (IdentityNotMappedException) { return null; }
    }

    // ── Privados ──────────────────────────────────────────────────────────────

    /// <summary>Devuelve false si este Windows no tiene el grupo (Home, versiones "aligeradas").</summary>
    static bool AddToLocalGroup(string username, WellKnownSidType groupSid, bool required)
    {
        // Resolver el nombre LOCALIZADO del grupo desde su SID well-known
        // ("Remote Desktop Users" es "Usuarios de escritorio remoto" en es-ES).
        var sid = new SecurityIdentifier(groupSid, null);
        string group;
        try
        {
            var account = ((NTAccount)sid.Translate(typeof(NTAccount))).Value;
            group = account.Contains('\\') ? account[(account.IndexOf('\\') + 1)..] : account;
        }
        catch (IdentityNotMappedException)
        {
            // Hay Windows "aligerados" (Tiny11, Atlas, ReviOS…) que borran grupos integrados.
            // Antes esto subía tal cual ("Some or all identity references could not be
            // translated") y la sesión no arrancaba sin decir qué faltaba.
            var msg = BackendText.Get("isolatedUser.missingGroup", GroupLabel(groupSid), sid.Value, username);
            if (required) throw new InvalidOperationException(msg);
            Logger.Log(msg + " " + BackendText.Get("isolatedUser.grantingRemoteLogon"));
            return false;
        }

        var member = new LOCALGROUP_MEMBERS_INFO_3
        {
            lgrmi3_domainandname = $"{Environment.MachineName}\\{username}"
        };

        int rc = NetLocalGroupAddMembers(null, group, 3, ref member, 1);
        if (rc is not (NERR_Success or ERROR_MEMBER_IN_ALIAS))
            Logger.Warning(BackendText.Get("isolatedUser.errorAddingToGroup", username, group, rc));
        return true;
    }

    /// <summary>
    /// Windows Home (y algunos Windows "aligerados") no tiene el grupo Remote Desktop Users
    /// (S-1-5-32-555), que es quien tiene "Permitir inicio de sesión a través de Servicios de
    /// Escritorio remoto" (SeRemoteInteractiveLogonRight). Sin él, termsrv rechaza el inicio de
    /// sesión del usuario aislado y la sesión nunca aparece. El derecho se da a la cuenta.
    /// Se quita al borrarla (<see cref="RemoveUser"/>).
    /// </summary>
    static void GrantRemoteLogonRight(string username)
    {
        const string right = "SeRemoteInteractiveLogonRight";
        try
        {
            var sid = (SecurityIdentifier)new NTAccount(username).Translate(typeof(SecurityIdentifier));
            using var policy = LsaPolicy.Open();
            if (!policy.Has(sid, right)) policy.Add(sid, right);
            if (policy.Has(sid, "SeDenyRemoteInteractiveLogonRight"))
                Logger.Warning(BackendText.Get("isolatedUser.cannotGrantRemoteLogon", username, "policy"));
            SetWinStationAccess(sid, grant: true);
            Logger.Log(BackendText.Get("isolatedUser.remoteLogonGranted", username, right));
        }
        catch (Exception ex)
        {
            Logger.Error(BackendText.Get("isolatedUser.cannotGrantRemoteLogon", username, ex.Message));
        }
    }

    const string WinStationsKey = @"SYSTEM\CurrentControlSet\Control\Terminal Server\WinStations";
    const string RdpTcpKey = WinStationsKey + @"\RDP-Tcp";
    // Lo mismo que tiene Remote Desktop Users por defecto: WINSTATION_QUERY | LOGON | CONNECT ("Acceso de usuario").
    const int WinStationUserAccess = 0x121;

    /// <summary>
    /// Además del derecho, termsrv comprueba los permisos de la escucha RDP-Tcp (los de
    /// WinStations\DefaultSecurity si RDP-Tcp no tiene "Security" propio). Por defecto solo
    /// Administradores y Remote Desktop Users pueden iniciar sesión; sin ese grupo, se da
    /// "Acceso de usuario" a la cuenta. Al quitarlo, si RDP-Tcp vuelve a ser igual que el
    /// predeterminado se borra su "Security" para dejarlo como estaba.
    /// </summary>
    static void SetWinStationAccess(SecurityIdentifier sid, bool grant)
    {
        using var root = Registry.LocalMachine.OpenSubKey(WinStationsKey)
            ?? throw new InvalidOperationException($"no existe HKLM\\{WinStationsKey}");
        using var rdpTcp = Registry.LocalMachine.OpenSubKey(RdpTcpKey, writable: true)
            ?? throw new InvalidOperationException($"no existe HKLM\\{RdpTcpKey}");

        var defaultBytes = root.GetValue("DefaultSecurity") as byte[];
        var currentBytes = rdpTcp.GetValue("Security") as byte[] ?? defaultBytes;
        if (currentBytes is null)
        {
            Logger.Warning("[IsolatedUser] RDP-Tcp no tiene permisos legibles (ni Security ni DefaultSecurity); no se tocan.");
            return;
        }

        var sd = new RawSecurityDescriptor(currentBytes, 0);
        var acl = sd.DiscretionaryAcl ?? new RawAcl(GenericAcl.AclRevision, 0);
        bool has = false;
        for (int i = acl.Count - 1; i >= 0; i--)
        {
            if (acl[i] is not CommonAce ace || ace.SecurityIdentifier != sid) continue;
            if (grant && ace.AceQualifier == AceQualifier.AccessAllowed && (ace.AccessMask & WinStationUserAccess) == WinStationUserAccess)
                has = true;
            else if (!grant)
                acl.RemoveAce(i);
        }
        if (grant && has) return;
        if (!grant && acl.Count == (sd.DiscretionaryAcl?.Count ?? 0)) return;   // no había nada suyo

        if (grant)
            acl.InsertAce(acl.Count, new CommonAce(AceFlags.None, AceQualifier.AccessAllowed,
                                                   WinStationUserAccess, sid, false, null));
        sd.DiscretionaryAcl = acl;
        sd.SetFlags(sd.ControlFlags | ControlFlags.DiscretionaryAclPresent);

        var bytes = new byte[sd.BinaryLength];
        sd.GetBinaryForm(bytes, 0);
        if (!grant && defaultBytes is not null && bytes.AsSpan().SequenceEqual(defaultBytes))
            rdpTcp.DeleteValue("Security", throwOnMissingValue: false);
        else
            rdpTcp.SetValue("Security", bytes, RegistryValueKind.Binary);
    }

    static string GroupLabel(WellKnownSidType groupSid) => groupSid switch
    {
        WellKnownSidType.BuiltinUsersSid              => "'Users'",
        WellKnownSidType.BuiltinRemoteDesktopUsersSid => "'Remote Desktop Users'",
        _                                             => groupSid.ToString(),
    };

    static void HideFromLoginScreen(string username)
    {
        try
        {
            using var key = Registry.LocalMachine.CreateSubKey(UserListKey, writable: true);
            key?.SetValue(username, 0, RegistryValueKind.DWord);
        }
        catch (Exception ex)
        {
            Logger.Warning($"[IsolatedUser] No se pudo ocultar '{username}' de la pantalla de login: {ex.Message}");
        }
    }

    static void UnhideFromLoginScreen(string username)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(UserListKey, writable: true);
            key?.DeleteValue(username, throwOnMissingValue: false);
        }
        catch { /* no crítico */ }
    }

    const string SteamMachineKey = @"SOFTWARE\WOW6432Node\Valve\Steam";

    /// <summary>
    /// Quita la regla Deny que una versión anterior ponía al usuario aislado sobre
    /// <see cref="SteamMachineKey"/>: sin poder leer esa clave su Steam no arranca.
    /// El aislamiento entre Steams lo hace ahora osms-steamhook.dll. Idempotente.
    /// </summary>
    static void RemoveLegacySteamDeny(string username) =>
        EditSteamKeyAcl(username, (security, rule) => security.RemoveAccessRuleSpecific(rule));

    static void EditSteamKeyAcl(string username, Action<RegistrySecurity, RegistryAccessRule> edit)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(SteamMachineKey,
                RegistryKeyPermissionCheck.ReadWriteSubTree,
                RegistryRights.ReadPermissions | RegistryRights.ChangePermissions);
            if (key is null) return;   // Steam no instalado

            var sid  = (SecurityIdentifier)new NTAccount(username).Translate(typeof(SecurityIdentifier));
            var rule = new RegistryAccessRule(sid, RegistryRights.QueryValues | RegistryRights.SetValue,
                InheritanceFlags.None, PropagationFlags.None, AccessControlType.Deny);

            var security = key.GetAccessControl();
            edit(security, rule);
            key.SetAccessControl(security);
        }
        catch (Exception ex)
        {
            Logger.Warning($"[IsolatedUser] No se pudo ajustar el acceso de '{username}' al registro de Steam: {ex.Message}");
        }
    }

    /// <summary>
    /// Contraseña aleatoria de 24 chars con las 4 clases garantizadas (cumple la
    /// política de complejidad por defecto de Windows). Sin caracteres ambiguos.
    /// </summary>
    static string GeneratePassword()
    {
        const string upper   = "ABCDEFGHJKLMNPQRSTUVWXYZ";
        const string lower   = "abcdefghijkmnpqrstuvwxyz";
        const string digits  = "23456789";
        const string symbols = "!@#$%^*-_=+";
        const string all     = upper + lower + digits + symbols;

        var chars = new char[24];
        chars[0] = Pick(upper);
        chars[1] = Pick(lower);
        chars[2] = Pick(digits);
        chars[3] = Pick(symbols);
        for (int i = 4; i < chars.Length; i++)
            chars[i] = Pick(all);

        // Fisher-Yates con RNG criptográfico para no dejar las clases fijas al inicio
        for (int i = chars.Length - 1; i > 0; i--)
        {
            int j = RandomNumberGenerator.GetInt32(i + 1);
            (chars[i], chars[j]) = (chars[j], chars[i]);
        }

        return new string(chars);

        static char Pick(string set) => set[RandomNumberGenerator.GetInt32(set.Length)];
    }
}
