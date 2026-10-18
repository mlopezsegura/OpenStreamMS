using OpenStreamMS.Services;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace OpenStreamMS.Core.Helpers;

/// <summary>
/// Un puesto exclusivo no puede apagar ni reiniciar el equipo (solo la consola); una sesión
/// remota o un puesto no exclusivo sí. Windows da "Apagar el sistema" (SeShutdownPrivilege) al
/// grupo Usuarios, y los usuarios aislados (osms-*) son usuarios estándar: aunque NoClose les
/// quite los botones, podrían apagar con <c>shutdown /s</c>. El derecho es por cuenta, no por
/// sesión, así que mientras haya un puesto exclusivo se le quita al grupo Usuarios y se da
/// explícitamente a cada usuario aislado que sí puede (<see cref="SetForUser"/>).
/// Administradores (la consola) lo conserva siempre. Vale desde el siguiente inicio de sesión
/// de cada usuario (los privilegios van en el token). Se anota en disco si se quitó al grupo,
/// para devolverlo cuando ya no haya puestos exclusivos o al desinstalar.
/// </summary>
internal static class ShutdownRight
{
    private const string Privilege = "SeShutdownPrivilege";
    private static readonly SecurityIdentifier Users = new(WellKnownSidType.BuiltinUsersSid, null);
    private static readonly SecurityIdentifier Admins = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly string JournalPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "OpenStreamMS", "shutdown-right-removed");

    /// <summary>
    /// Antes de que el usuario aislado inicie sesión: si puede apagar, se le da el derecho a él
    /// (no depende del grupo Usuarios); si no, se le quita y se retira también al grupo.
    /// </summary>
    public static void SetForUser(string username, bool allow)
    {
        try
        {
            var sid = (SecurityIdentifier)new NTAccount(username).Translate(typeof(SecurityIdentifier));
            using (var policy = Lsa.Open())
            {
                if (allow) policy.Add(sid, Privilege);
                else if (policy.Has(sid, Privilege)) policy.Remove(sid, Privilege);
            }
            if (!allow) RestrictToAdministrators();
            Logger.Log($"[Seguridad] '{username}' {(allow ? "puede" : "no puede")} apagar ni reiniciar el equipo.");
        }
        catch (Exception ex)
        {
            Logger.Warning($"[Seguridad] No se pudo ajustar 'Apagar el sistema' para '{username}': {ex.Message}");
        }
    }

    /// <summary>Quita "Apagar el sistema" al grupo Usuarios (idempotente).</summary>
    private static void RestrictToAdministrators()
    {
        try
        {
            using var policy = Lsa.Open();
            policy.Add(Admins, Privilege);              // que la consola nunca se quede sin poder apagar
            if (!policy.Has(Users, Privilege)) return;
            Directory.CreateDirectory(Path.GetDirectoryName(JournalPath)!);
            File.WriteAllText(JournalPath, DateTime.Now.ToString("O"));
            policy.Remove(Users, Privilege);
            Logger.Log("[Seguridad] 'Apagar el sistema' retirado del grupo Usuarios (hay puestos exclusivos): lo conservan los administradores y los usuarios aislados que pueden.");
        }
        catch (Exception ex)
        {
            Logger.Warning($"[Seguridad] No se pudo retirar 'Apagar el sistema' del grupo Usuarios: {ex.Message}");
        }
    }

    /// <summary>Devuelve el derecho al grupo Usuarios si OpenStreamMS se lo quitó (sin puestos exclusivos o al desinstalar).</summary>
    public static void Restore()
    {
        if (!File.Exists(JournalPath)) return;
        try
        {
            using var policy = Lsa.Open();
            policy.Add(Users, Privilege);
            File.Delete(JournalPath);
            Logger.Log("[Seguridad] 'Apagar el sistema' devuelto al grupo Usuarios.");
        }
        catch (Exception ex)
        {
            Logger.Warning($"[Seguridad] No se pudo devolver 'Apagar el sistema' al grupo Usuarios: {ex.Message}");
        }
    }

    // ── LSA ──────────────────────────────────────────────────────────────────

    private sealed class Lsa : IDisposable
    {
        [StructLayout(LayoutKind.Sequential)] struct LSA_OBJECT_ATTRIBUTES { public int Length; public IntPtr RootDirectory, ObjectName; public int Attributes; public IntPtr SecurityDescriptor, SecurityQualityOfService; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct LSA_UNICODE_STRING { public ushort Length, MaximumLength; public string Buffer; }
        // Al leer: el búfer de LSA no tiene por qué acabar en nulo, se lee por su longitud.
        [StructLayout(LayoutKind.Sequential)] struct LSA_UNICODE_STRING_OUT { public ushort Length, MaximumLength; public IntPtr Buffer; }

        [DllImport("advapi32.dll")] static extern uint LsaOpenPolicy(IntPtr system, ref LSA_OBJECT_ATTRIBUTES attrs, int access, out IntPtr handle);
        [DllImport("advapi32.dll")] static extern uint LsaAddAccountRights(IntPtr policy, byte[] sid, LSA_UNICODE_STRING[] rights, int count);
        [DllImport("advapi32.dll")] static extern uint LsaRemoveAccountRights(IntPtr policy, byte[] sid, bool all, LSA_UNICODE_STRING[] rights, int count);
        [DllImport("advapi32.dll")] static extern uint LsaEnumerateAccountRights(IntPtr policy, byte[] sid, out IntPtr rights, out int count);
        [DllImport("advapi32.dll")] static extern uint LsaFreeMemory(IntPtr buffer);
        [DllImport("advapi32.dll")] static extern uint LsaClose(IntPtr handle);
        [DllImport("advapi32.dll")] static extern int LsaNtStatusToWinError(uint status);

        private const int POLICY_ALL_ACCESS = 0x000F0FFF;
        private const uint STATUS_OBJECT_NAME_NOT_FOUND = 0xC0000034;   // la cuenta no tiene ningún derecho
        private readonly IntPtr _handle;

        private Lsa(IntPtr handle) => _handle = handle;

        public static Lsa Open()
        {
            var attrs = new LSA_OBJECT_ATTRIBUTES { Length = Marshal.SizeOf<LSA_OBJECT_ATTRIBUTES>() };
            Check(LsaOpenPolicy(IntPtr.Zero, ref attrs, POLICY_ALL_ACCESS, out var h), "LsaOpenPolicy");
            return new Lsa(h);
        }

        private static byte[] Bytes(SecurityIdentifier sid) { var b = new byte[sid.BinaryLength]; sid.GetBinaryForm(b, 0); return b; }

        private static LSA_UNICODE_STRING[] Right(string name) =>
            [new LSA_UNICODE_STRING { Buffer = name, Length = (ushort)(name.Length * 2), MaximumLength = (ushort)(name.Length * 2 + 2) }];

        public bool Has(SecurityIdentifier sid, string right)
        {
            uint st = LsaEnumerateAccountRights(_handle, Bytes(sid), out var buf, out int count);
            if (st == STATUS_OBJECT_NAME_NOT_FOUND) return false;
            Check(st, "LsaEnumerateAccountRights");
            try
            {
                int size = Marshal.SizeOf<LSA_UNICODE_STRING_OUT>();
                for (int i = 0; i < count; i++)
                {
                    var s = Marshal.PtrToStructure<LSA_UNICODE_STRING_OUT>(buf + i * size);
                    if (string.Equals(Marshal.PtrToStringUni(s.Buffer, s.Length / 2), right, StringComparison.OrdinalIgnoreCase)) return true;
                }
                return false;
            }
            finally { LsaFreeMemory(buf); }
        }

        public void Add(SecurityIdentifier sid, string right) =>
            Check(LsaAddAccountRights(_handle, Bytes(sid), Right(right), 1), "LsaAddAccountRights");

        public void Remove(SecurityIdentifier sid, string right) =>
            Check(LsaRemoveAccountRights(_handle, Bytes(sid), false, Right(right), 1), "LsaRemoveAccountRights");

        private static void Check(uint status, string what)
        {
            if (status != 0) throw new InvalidOperationException($"{what}: error {LsaNtStatusToWinError(status)}");
        }

        public void Dispose() => LsaClose(_handle);
    }
}
