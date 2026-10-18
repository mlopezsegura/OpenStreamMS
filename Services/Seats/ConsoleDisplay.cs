using OpenStreamMS.Core.Helpers;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;

namespace OpenStreamMS.Services.Seats;

/// <summary>
/// La configuración de pantallas (CCD) es por sesión y la sesión 0 del servicio no tiene
/// monitores: QueryDisplayConfig falla. Este puente lanza
/// <c>OpenStreamMS.exe --display &lt;cmd&gt;</c> en la sesión de consola, espera a que
/// termine y lee su resultado (JSON) de un archivo.
/// </summary>
public static class ConsoleDisplay
{
    private static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "OpenStreamMS", "display");

    public static List<DisplayInfo> List() =>
        Run<List<DisplayInfo>>("list") ?? [];

    public static bool Activate(string devicePath) =>
        Run<bool>("activate", devicePath);

    public static bool Deactivate(string devicePath) =>
        Run<bool>("deactivate", devicePath);

    private static T? Run<T>(string command, string? devicePath = null)
    {
        if (!ConsoleSession.TryGetUser(out var consoleId, out _, out _))
        {
            Logger.Warning("[Seats] No hay nadie con sesión en la consola: no se pueden gestionar los monitores.");
            return default;
        }
        EnsureDir();
        var outFile = Path.Combine(Dir, $"{Guid.NewGuid():N}.json");
        try
        {
            var args = $"--display {command} \"{outFile}\"" + (devicePath is null ? "" : $" \"{devicePath}\"");
            new ProcessInSession().LaunchInSession(consoleId, Environment.ProcessPath!, args,
                                                   waitForExit: true, waitTimeoutMs: 15000);
            if (!File.Exists(outFile))
            {
                Logger.Warning($"[Seats] --display {command} no devolvió resultado.");
                return default;
            }
            return JsonSerializer.Deserialize<T>(File.ReadAllText(outFile));
        }
        catch (Exception ex)
        {
            Logger.Warning($"[Seats] --display {command} falló: {ex.Message}");
            return default;
        }
        finally
        {
            try { File.Delete(outFile); } catch { /* temporal */ }
        }
    }

    private static void EnsureDir() => SharedDir("display");

    /// <summary>Carpeta compartida: el servicio la crea y el usuario de la consola escribe en ella.</summary>
    internal static string SharedDir(string name)
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "OpenStreamMS", name);
        if (Directory.Exists(dir)) return dir;
        var di = Directory.CreateDirectory(dir);
        var sec = di.GetAccessControl();
        sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
            FileSystemRights.Modify, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None, AccessControlType.Allow));
        di.SetAccessControl(sec);
        return dir;
    }

    /// <summary>
    /// Lado de la consola: <c>OpenStreamMS.exe --display list|activate|deactivate &lt;out&gt; [devicePath]</c>.
    /// </summary>
    public static int RunCommand(string[] args)
    {
        if (args.Length < 3) return 2;
        string command = args[1], outFile = args[2];
        string? devicePath = args.Length > 3 ? args[3] : null;
        object result = command switch
        {
            "list"       => DisplayTopology.Enumerate(),
            "activate"   => devicePath is not null && DisplayTopology.Activate(devicePath),
            "deactivate" => devicePath is not null && DisplayTopology.Deactivate(devicePath),
            _            => false,
        };
        File.WriteAllText(outFile, JsonSerializer.Serialize(result));
        return 0;
    }
}
