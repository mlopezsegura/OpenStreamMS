using System.IO.Compression;
using System.Security.Cryptography;

namespace OpenStreamMS.Services.Session;

/// <summary>
/// TermWrap (llccd), el multi-RDP de OpenStreamMS (sustituye al RDP Wrapper de asmtron). Misma versión, URL y hashes
/// fijos que Native/RemoteIdd/Provider/install.ps1: la DLL corre dentro de TermService, así que
/// no se acepta otra. Además de la multisesión parchea IsAllowNonRDPStack (lo que necesita el
/// proveedor de RemoteIdd) y funciona en Windows Home.
/// </summary>
internal static class TermWrapInstaller
{
    private const string ZipUrl  = "https://github.com/llccd/TermWrap/releases/download/v0.7/TermWrap-0.7.zip";
    private const string ZipHash = "096CC075E4D2EBBF2587F19D5E9A3003181E1D3AE44C514E0532BA222F82AFC4";
    private static readonly Dictionary<string, string> FileHashes = new()
    {
        ["TermWrap.dll"] = "0D213D44A0FE0E6D174D81D7D986C29D306466F460E429B1B946D994F562A2D4",
        ["Zydis.dll"]    = "AFBDBAF38B8F2789731AB83700B1F4F0095056C3F073288CFE773A103DDBDF81",
    };

    public const string ServiceDllValue = @"%ProgramFiles%\RDP Wrapper\TermWrap.dll";
    private const string SvcParams = @"SYSTEM\CurrentControlSet\Services\TermService\Parameters";

    public static string InstallDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "RDP Wrapper");

    /// <summary>true si TermWrap.dll y Zydis.dll están en su carpeta con el hash esperado.</summary>
    public static bool FilesOk() =>
        FileHashes.All(f => File.Exists(Path.Combine(InstallDir, f.Key)) &&
                            Sha256(Path.Combine(InstallDir, f.Key)) == f.Value);

    /// <summary>Descarga y copia los ficheros si faltan o no coinciden. No toca ServiceDll.</summary>
    public static async Task EnsureFilesAsync(Action<string> log, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("OpenStreamMS");
        if (FilesOk()) { log("TermWrap 0.7 is already in its folder."); return; }
        if (!Environment.Is64BitOperatingSystem) throw new InvalidOperationException("TermWrap: solo x64.");

        var tmp = Path.Combine(Path.GetTempPath(), "osms-termwrap");
        if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true);
        Directory.CreateDirectory(tmp);
        try
        {
            var zip = Path.Combine(tmp, "TermWrap-0.7.zip");
            log($"Downloading {ZipUrl}…");
            await using (var src = await http.GetStreamAsync(ZipUrl, ct))
            await using (var dst = File.Create(zip))
                await src.CopyToAsync(dst, ct);
            if (Sha256(zip) != ZipHash) throw new InvalidOperationException("TermWrap-0.7.zip does not have the expected hash.");
            ZipFile.ExtractToDirectory(zip, tmp);

            Directory.CreateDirectory(InstallDir);
            foreach (var (name, hash) in FileHashes)
            {
                var from = Path.Combine(tmp, "x64", name);
                if (Sha256(from) != hash) throw new InvalidOperationException($"{name} does not have the expected hash.");
                var to = Path.Combine(InstallDir, name);
                if (File.Exists(to) && Sha256(to) == hash) continue;
                CopyInUse(from, to);
                log($"Copied {to}.");
            }
        }
        finally { try { Directory.Delete(tmp, recursive: true); } catch { } }
    }

    /// <summary>ServiceDll de TermService → TermWrap. Devuelve true si lo ha cambiado.</summary>
    public static bool PointServiceDll(Action<string> log)
    {
        using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(SvcParams, writable: true)
            ?? throw new InvalidOperationException($"no existe HKLM\\{SvcParams}");
        var current = key.GetValue("ServiceDll", null, Microsoft.Win32.RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
        if (string.Equals(Path.GetFileName(current), "TermWrap.dll", StringComparison.OrdinalIgnoreCase)) return false;
        key.SetValue("ServiceDll", ServiceDllValue, Microsoft.Win32.RegistryValueKind.ExpandString);
        log($"TermService: ServiceDll {current} -> {ServiceDllValue}.");
        return true;
    }

    /// <summary>ServiceDll de vuelta al termsrv.dll de Windows (desinstalar el multi-RDP).</summary>
    public static void RestoreWindowsServiceDll(Action<string> log)
    {
        using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(SvcParams, writable: true);
        key?.SetValue("ServiceDll", @"%SystemRoot%\System32\termsrv.dll", Microsoft.Win32.RegistryValueKind.ExpandString);
        log("TermService: ServiceDll -> %SystemRoot%\\System32\\termsrv.dll.");
    }

    /// <summary>
    /// Las tareas de autoupdate de asmtron reponen rdpwrap.dll como ServiceDll al arrancar y
    /// deshacen TermWrap. OpenStreamMS ya no usa asmtron: se desactivan.
    /// </summary>
    public static void DisableAsmtronAutoupdate(Action<string> log)
    {
        const string script =
            "Get-ScheduledTask | Where-Object { $_.State -ne 'Disabled' -and " +
            "($_.Actions | Where-Object { \"$($_.Execute) $($_.Arguments)\" -match 'autoupdate\\.bat|RDPWInst' }) } | " +
            "ForEach-Object { Disable-ScheduledTask -TaskPath $_.TaskPath -TaskName $_.TaskName | Out-Null; $_.TaskName }";
        var encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script));
        var psi = new System.Diagnostics.ProcessStartInfo("powershell.exe",
            $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encoded}")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        try
        {
            using var p = System.Diagnostics.Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit(30_000);
            if (output.Length > 0) log($"Disabled RDP Wrapper autoupdate tasks: {output.Replace(Environment.NewLine, ", ")}.");
        }
        catch (Exception ex) { log($"Could not check RDP Wrapper autoupdate tasks: {ex.Message}"); }
    }

    private static string Sha256(string path)
    {
        using var s = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(s));
    }

    /// <summary>Si el destino está cargado (TermService), se renombra el viejo y se copia al lado.</summary>
    private static void CopyInUse(string from, string to)
    {
        try { File.Copy(from, to, overwrite: true); }
        catch (IOException)
        {
            var old = to + ".old-" + DateTime.Now.ToString("yyyyMMddHHmmss");
            File.Move(to, old);
            File.Copy(from, to);
        }
    }
}
