using System.Diagnostics;
using System.Globalization;
using System.Xml.Linq;

namespace OpenStreamMS.Services.Sunshine;

internal static class SunshineCrashDetector
{
    private const string EventNamespace = "http://schemas.microsoft.com/win/2004/08/events/event";

    internal static string? TryFindRecentFault(string sunshineExe, int pid, DateTime processStartLocal)
    {
        try
        {
            var xml = QueryApplicationErrorEvents();
            if (string.IsNullOrWhiteSpace(xml))
                return null;

            XNamespace ns = EventNamespace;
            var doc = XDocument.Parse("<Events>" + xml + "</Events>");
            var cutoff = processStartLocal == DateTime.MinValue
                ? DateTime.Now.AddMinutes(-10)
                : processStartLocal.AddMinutes(-2);

            foreach (var ev in doc.Root?.Elements(ns + "Event") ?? Enumerable.Empty<XElement>())
            {
                var eventTime = ReadEventTime(ev, ns);
                if (eventTime is not null && eventTime.Value < cutoff)
                    continue;

                var data = ev.Element(ns + "EventData")?
                    .Elements(ns + "Data")
                    .Select(x => x.Value)
                    .ToArray();

                if (data is null || data.Length < 11)
                    continue;

                var appName = data.ElementAtOrDefault(0) ?? "";
                var moduleName = data.ElementAtOrDefault(3) ?? "";
                var exceptionCode = data.ElementAtOrDefault(6) ?? "";
                var eventPid = data.ElementAtOrDefault(8) ?? "";
                var appPath = data.ElementAtOrDefault(10) ?? "";

                if (!LooksLikeSunshine(appName, appPath, sunshineExe))
                    continue;
                if (!PidMatches(eventPid, pid))
                    continue;

                var moduleText = string.IsNullOrWhiteSpace(moduleName)
                    ? "modulo desconocido"
                    : moduleName;
                var codeText = string.IsNullOrWhiteSpace(exceptionCode)
                    ? "excepcion desconocida"
                    : exceptionCode;

                return string.Equals(exceptionCode, "0x80000003", StringComparison.OrdinalIgnoreCase)
                    ? $"Sunshine fallo con STATUS_BREAKPOINT ({codeText}) en {moduleText} (PID={pid})."
                    : $"Sunshine fallo con {codeText} en {moduleText} (PID={pid}).";
            }
        }
        catch (Exception ex)
        {
            Logger.Warning($"[Sunshine] No se pudo leer Application Error: {ex.Message}");
        }

        return null;
    }

    private static string QueryApplicationErrorEvents()
    {
        var psi = new ProcessStartInfo("wevtutil.exe")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        psi.ArgumentList.Add("qe");
        psi.ArgumentList.Add("Application");
        psi.ArgumentList.Add("/q:*[System[(EventID=1000)]]");
        psi.ArgumentList.Add("/rd:true");
        psi.ArgumentList.Add("/c:40");
        psi.ArgumentList.Add("/f:xml");

        using var p = Process.Start(psi);
        if (p is null)
            return "";

        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();

        if (!p.WaitForExit(3000))
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            return "";
        }

        if (p.ExitCode != 0 && !string.IsNullOrWhiteSpace(stderr))
            Logger.Warning($"[Sunshine] wevtutil devolvio {p.ExitCode}: {stderr.Trim()}");

        return stdout;
    }

    private static DateTime? ReadEventTime(XElement ev, XNamespace ns)
    {
        var raw = ev.Element(ns + "System")?
            .Element(ns + "TimeCreated")?
            .Attribute("SystemTime")?
            .Value;

        if (string.IsNullOrWhiteSpace(raw))
            return null;

        return DateTime.TryParse(
            raw,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var utc)
            ? utc.ToLocalTime()
            : null;
    }

    private static bool LooksLikeSunshine(string appName, string appPath, string expectedPath)
    {
        if (string.Equals(appName, "sunshine.exe", StringComparison.OrdinalIgnoreCase))
            return true;

        return !string.IsNullOrWhiteSpace(appPath) &&
               string.Equals(
                   Path.GetFullPath(appPath),
                   Path.GetFullPath(expectedPath),
                   StringComparison.OrdinalIgnoreCase);
    }

    private static bool PidMatches(string rawPid, int expectedPid)
    {
        rawPid = rawPid.Trim();
        if (rawPid.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return int.TryParse(
                       rawPid[2..],
                       NumberStyles.HexNumber,
                       CultureInfo.InvariantCulture,
                       out var parsed) &&
                   parsed == expectedPid;
        }

        return int.TryParse(rawPid, NumberStyles.Integer, CultureInfo.InvariantCulture, out var decimalPid) &&
               decimalPid == expectedPid;
    }
}
