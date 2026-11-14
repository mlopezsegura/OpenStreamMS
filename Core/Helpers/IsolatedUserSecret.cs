using System.ComponentModel;
using System.Runtime.InteropServices;

namespace OpenStreamMS.Core.Helpers;

/// <summary>
/// Contraseña de cada usuario aislado (<c>osms-…</c>), guardada como secreto local de LSA
/// (<c>L$OpenStreamMS_&lt;usuario&gt;</c>): Windows lo cifra y solo lo puede leer SYSTEM.
/// <para>
/// Por qué se guarda: restablecer la contraseña de una cuenta local (lo que hacía cada arranque)
/// deja inservibles sus claves DPAPI, que dependen de la contraseña. Steam guarda su inicio de
/// sesión con DPAPI, así que recordaba la cuenta pero pedía QR o contraseña cada vez; lo mismo
/// pasaba con las contraseñas guardadas en los navegadores de la sesión.
/// </para>
/// </summary>
internal static class IsolatedUserSecret
{
    [StructLayout(LayoutKind.Sequential)]
    private struct LSA_UNICODE_STRING
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LSA_OBJECT_ATTRIBUTES
    {
        public int Length;
        public IntPtr RootDirectory, ObjectName;
        public int Attributes;
        public IntPtr SecurityDescriptor, SecurityQualityOfService;
    }

    [DllImport("advapi32.dll")] private static extern uint LsaOpenPolicy(IntPtr system, ref LSA_OBJECT_ATTRIBUTES attrs, int access, out IntPtr handle);
    [DllImport("advapi32.dll")] private static extern uint LsaStorePrivateData(IntPtr policy, ref LSA_UNICODE_STRING key, IntPtr data);
    [DllImport("advapi32.dll")] private static extern uint LsaRetrievePrivateData(IntPtr policy, ref LSA_UNICODE_STRING key, out IntPtr data);
    [DllImport("advapi32.dll")] private static extern uint LsaFreeMemory(IntPtr buffer);
    [DllImport("advapi32.dll")] private static extern uint LsaClose(IntPtr handle);
    [DllImport("advapi32.dll")] private static extern int LsaNtStatusToWinError(uint status);

    private const int  POLICY_CREATE_SECRET           = 0x00000020;
    private const int  POLICY_GET_PRIVATE_INFORMATION = 0x00000004;
    private const uint STATUS_OBJECT_NAME_NOT_FOUND   = 0xC0000034;

    /// <summary>Nombre del secreto: "L$" lo hace local (nunca sale del equipo ni se replica).</summary>
    internal static string KeyName(string username) => "L$OpenStreamMS_" + username.ToLowerInvariant();

    /// <summary>La contraseña guardada para <paramref name="username"/>, o null si no hay.</summary>
    internal static string? TryGet(string username)
    {
        return WithPolicy(policy => WithKey(username, (ref LSA_UNICODE_STRING key) =>
        {
            uint st = LsaRetrievePrivateData(policy, ref key, out var data);
            if (st == STATUS_OBJECT_NAME_NOT_FOUND) return null;
            Check(st, "LsaRetrievePrivateData");
            try
            {
                if (data == IntPtr.Zero) return null;
                var s = Marshal.PtrToStructure<LSA_UNICODE_STRING>(data);
                return s.Length == 0 ? null : Marshal.PtrToStringUni(s.Buffer, s.Length / 2);
            }
            finally
            {
                if (data != IntPtr.Zero) LsaFreeMemory(data);
            }
        }));
    }

    /// <summary>Guarda (o sustituye) la contraseña de <paramref name="username"/>.</summary>
    internal static void Store(string username, string password)
    {
        WithPolicy(policy => WithKey(username, (ref LSA_UNICODE_STRING key) =>
        {
            var buffer = Marshal.StringToHGlobalUni(password);
            var value = new LSA_UNICODE_STRING
            {
                Length = (ushort)(password.Length * 2),
                MaximumLength = (ushort)(password.Length * 2),
                Buffer = buffer,
            };
            var valuePtr = Marshal.AllocHGlobal(Marshal.SizeOf<LSA_UNICODE_STRING>());
            try
            {
                Marshal.StructureToPtr(value, valuePtr, false);
                Check(LsaStorePrivateData(policy, ref key, valuePtr), "LsaStorePrivateData");
                return (string?)null;
            }
            finally
            {
                Marshal.FreeHGlobal(valuePtr);
                Marshal.ZeroFreeGlobalAllocUnicode(buffer);
            }
        }));
    }

    /// <summary>Borra el secreto de <paramref name="username"/> (si no existe, no hace nada).</summary>
    internal static void Delete(string username)
    {
        WithPolicy(policy => WithKey(username, (ref LSA_UNICODE_STRING key) =>
        {
            uint st = LsaStorePrivateData(policy, ref key, IntPtr.Zero);
            if (st != STATUS_OBJECT_NAME_NOT_FOUND) Check(st, "LsaStorePrivateData");
            return (string?)null;
        }));
    }

    private delegate string? KeyAction(ref LSA_UNICODE_STRING key);

    private static string? WithKey(string username, KeyAction action)
    {
        var name = KeyName(username);
        var buffer = Marshal.StringToHGlobalUni(name);
        try
        {
            var key = new LSA_UNICODE_STRING
            {
                Length = (ushort)(name.Length * 2),
                MaximumLength = (ushort)((name.Length + 1) * 2),
                Buffer = buffer,
            };
            return action(ref key);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static string? WithPolicy(Func<IntPtr, string?> action)
    {
        var attrs = new LSA_OBJECT_ATTRIBUTES { Length = Marshal.SizeOf<LSA_OBJECT_ATTRIBUTES>() };
        Check(LsaOpenPolicy(IntPtr.Zero, ref attrs, POLICY_CREATE_SECRET | POLICY_GET_PRIVATE_INFORMATION, out var policy), "LsaOpenPolicy");
        try { return action(policy); }
        finally { LsaClose(policy); }
    }

    private static void Check(uint status, string what)
    {
        if (status != 0) throw new Win32Exception(LsaNtStatusToWinError(status), what);
    }
}
