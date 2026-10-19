using System.Security.AccessControl;
using System.Security.Principal;
using OpenStreamMS.Services;

namespace OpenStreamMS.Core.Helpers
{
    /// <summary>
    /// Deja un fichero o carpeta legible solo por SYSTEM y Administradores. Por defecto, lo que
    /// el servicio escribe junto al exe (Program Files) o en ProgramData lo puede leer cualquier
    /// usuario, y ahí van contraseñas de usuarios de Windows, la clave que firma las cookies del
    /// panel y las credenciales de Sunshine. En este equipo hay otros usuarios: los de los puestos
    /// y las sesiones de streaming.
    /// </summary>
    public static class SecureFile
    {
        public static void RestrictToAdmins(string path)
        {
            // Solo el servicio: ejecutado a mano por un usuario normal, se quedaría sin acceso a
            // sus propios ficheros.
            if (!WindowsIdentity.GetCurrent().IsSystem) return;
            try
            {
                var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
                var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);

                if (File.Exists(path))
                {
                    var fs = new FileSecurity();
                    fs.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
                    fs.AddAccessRule(new FileSystemAccessRule(system, FileSystemRights.FullControl, AccessControlType.Allow));
                    fs.AddAccessRule(new FileSystemAccessRule(admins, FileSystemRights.FullControl, AccessControlType.Allow));
                    new FileInfo(path).SetAccessControl(fs);
                }
                else if (Directory.Exists(path))
                {
                    const InheritanceFlags all = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
                    var ds = new DirectorySecurity();
                    ds.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
                    ds.AddAccessRule(new FileSystemAccessRule(system, FileSystemRights.FullControl, all, PropagationFlags.None, AccessControlType.Allow));
                    ds.AddAccessRule(new FileSystemAccessRule(admins, FileSystemRights.FullControl, all, PropagationFlags.None, AccessControlType.Allow));
                    new DirectoryInfo(path).SetAccessControl(ds);
                }
            }
            catch (Exception ex)
            {
                Logger.Warning($"[Security] No se pudieron restringir los permisos de '{path}': {ex.Message}");
            }
        }
    }
}
