namespace OpenStreamMS.Core.Api;

/// <summary>Rango de puertos inclusivo.</summary>
public readonly record struct PortRange(int From, int To)
{
    public bool Overlaps(PortRange other) => From <= other.To && other.From <= To;

    public override string ToString() => From == To ? From.ToString() : $"{From}-{To}";
}

/// <summary>Puerto o rango que una sesión ocupa en exclusiva, con su transporte y su uso.</summary>
public sealed record PortClaim(string Transport, PortRange Range, string Purpose);

/// <summary>Puertos sugeridos para una sesión nueva, libres respecto a las existentes.</summary>
public record SuggestedPortsResponse(
    int SunshineStreamPort,
    int WebRtcPort,
    int WebRtcMediaPortMin,
    int WebRtcMediaPortMax);

/// <summary>
/// Puertos que usa cada instancia de Sunshine según sus protocolos activos: los calcula,
/// detecta choques entre sesiones y sugiere puertos libres.
/// <para>
/// Moonlight deriva todo del puerto base (<c>port</c>): HTTPS en <c>-5</c>, HTTP en el base, panel
/// web en <c>+1</c>, vídeo/control/audio UDP en <c>+9..+11</c> (micrófono <c>+13</c> en algunos forks)
/// y RTSP en <c>+21</c>. WebRTC usa un puerto TCP de señalización, un rango UDP de media y el
/// descubrimiento UDP 8000, que es compartido por todas las instancias y no cuenta como choque.
/// </para>
/// </summary>
public static class SunshinePorts
{
    public const int DefaultMoonlightPort      = 47989;
    public const int MoonlightPortStep         = 100;
    public const int DefaultWebRtcPort         = 8000;
    public const int WebRtcDiscoveryPort       = 8000;
    public const int DefaultWebRtcMediaPortMin = 40000;
    /// <summary>Puertos UDP de media por sesión. Cada stream WebRTC usa unos pocos; 20 deja margen.</summary>
    public const int WebRtcMediaPortCount      = 20;

    /// <summary>Rango Moonlight completo que se abre en el firewall: <c>base-5 .. base+21</c>.</summary>
    public static PortRange MoonlightFirewallRange(int basePort) => new(basePort - 5, basePort + 21);

    /// <summary>Puertos que la sesión ocupa con sus protocolos activos.</summary>
    public static IReadOnlyList<PortClaim> Claims(StreamSession s)
    {
        var b = s.SunshineStreamPort;
        var claims = new List<PortClaim>
        {
            // El panel web escucha siempre, sea cual sea el protocolo.
            new("TCP", new(b + 1, b + 1), "panel web de Sunshine"),
        };

        if (s.MoonlightEnabled)
        {
            claims.Add(new("TCP", new(b - 5, b - 5), "Moonlight HTTPS"));
            claims.Add(new("TCP", new(b, b),         "Moonlight HTTP"));
            claims.Add(new("TCP", new(b + 21, b + 21), "Moonlight RTSP"));
            claims.Add(new("UDP", new(b + 9, b + 13),  "Moonlight vídeo/audio/control"));
        }

        if (s.WebRtcEnabled)
        {
            claims.Add(new("TCP", new(s.WebRtcPort, s.WebRtcPort), "señalización WebRTC"));
            claims.Add(new("UDP", new(s.WebRtcMediaPortMin, s.WebRtcMediaPortMax), "media WebRTC"));
        }

        return claims;
    }

    /// <summary>
    /// Valida los puertos de la sesión por sí solos (rangos válidos, sin choques internos).
    /// Devuelve el error o null.
    /// </summary>
    public static string? Validate(StreamSession s)
    {
        if (s.SunshineStreamPort - 5 < 1024 || s.SunshineStreamPort + 21 > 65535)
            return $"El puerto Moonlight {s.SunshineStreamPort} deja su rango ({MoonlightFirewallRange(s.SunshineStreamPort)}) fuera de 1024-65535.";
        if (s.WebRtcPort is < 1024 or > 65535)
            return $"El puerto WebRTC {s.WebRtcPort} está fuera de 1024-65535.";
        if (s.WebRtcMediaPortMin is < 1024 or > 65535 || s.WebRtcMediaPortMax is < 1024 or > 65535)
            return $"El rango de media WebRTC {s.WebRtcMediaPortMin}-{s.WebRtcMediaPortMax} está fuera de 1024-65535.";
        if (s.WebRtcMediaPortMin > s.WebRtcMediaPortMax)
            return $"El rango de media WebRTC {s.WebRtcMediaPortMin}-{s.WebRtcMediaPortMax} está vacío.";

        var claims = Claims(s);
        for (int i = 0; i < claims.Count; i++)
            for (int j = i + 1; j < claims.Count; j++)
                if (Collide(claims[i], claims[j]))
                    return $"{Describe(claims[i])} se solapa con {Describe(claims[j])} en la misma sesión.";

        return null;
    }

    /// <summary>
    /// Primer choque de puertos entre <paramref name="session"/> y alguna de <paramref name="others"/>
    /// (se ignora a sí misma por Id). Devuelve el error o null.
    /// </summary>
    public static string? FindConflict(StreamSession session, IEnumerable<StreamSession> others)
    {
        var mine = Claims(session);
        foreach (var other in others)
        {
            if (other.Id == session.Id) continue;
            foreach (var theirs in Claims(other))
                foreach (var claim in mine)
                    if (Collide(claim, theirs))
                        return $"{Describe(claim)} choca con la sesión '{other.Name}' ({Describe(theirs)}).";
        }
        return null;
    }

    /// <summary>
    /// Puertos libres para una sesión nueva: puerto base Moonlight en pasos de 100 desde 47989,
    /// puerto WebRTC desde 8000 y rango de media en bloques de 20 desde 40000.
    /// </summary>
    public static SuggestedPortsResponse Suggest(IReadOnlyCollection<StreamSession> existing)
    {
        var probe = new StreamSession { Name = "(nueva)", StreamProtocol = StreamProtocol.Moonlight };
        for (int k = 0; k < 150; k++)
        {
            probe.SunshineStreamPort = DefaultMoonlightPort + k * MoonlightPortStep;
            if (probe.SunshineStreamPort + 21 > 65535) break;
            if (FindConflict(probe, existing) is null) break;
        }

        probe.StreamProtocol = StreamProtocol.WebRtc;
        probe.WebRtcMediaPortMin = DefaultWebRtcMediaPortMin;
        probe.WebRtcMediaPortMax = DefaultWebRtcMediaPortMin + WebRtcMediaPortCount - 1;
        for (int k = 0; k < 1000; k++)
        {
            probe.WebRtcPort = DefaultWebRtcPort + k;
            if (Validate(probe) is null && FindConflict(probe, existing) is null) break;
        }

        for (int k = 0; k < 1000; k++)
        {
            probe.WebRtcMediaPortMin = DefaultWebRtcMediaPortMin + k * WebRtcMediaPortCount;
            probe.WebRtcMediaPortMax = probe.WebRtcMediaPortMin + WebRtcMediaPortCount - 1;
            if (probe.WebRtcMediaPortMax > 65535) break;
            if (Validate(probe) is null && FindConflict(probe, existing) is null) break;
        }

        return new SuggestedPortsResponse(probe.SunshineStreamPort, probe.WebRtcPort,
                                          probe.WebRtcMediaPortMin, probe.WebRtcMediaPortMax);
    }

    private static bool Collide(PortClaim a, PortClaim b) =>
        a.Transport == b.Transport && a.Range.Overlaps(b.Range);

    private static string Describe(PortClaim c) => $"{c.Transport} {c.Range} ({c.Purpose})";
}
