namespace OpenStreamMS.Core.Api;

public enum SessionState
{
    Created,   // Creada vía API, no iniciada todavía
    Starting,  // Creando sesión RDP en segundo plano
    Running,   // Sesión RDP activa y Sunshine corriendo
    Stopping,  // Detención en curso
    Stopped,   // Detenida correctamente
    Error      // Error durante inicio o ejecución
}

/// <summary>
/// Protocolos que sirve el Sunshine de la sesión (<c>stream_protocol</c> en <c>sunshine.conf</c>,
/// solo en Sunshine con WebRTC). Los puertos de un protocolo apagado no se abren.
/// </summary>
public enum StreamProtocol
{
    Moonlight, // Solo clientes Moonlight (GameStream)
    WebRtc,    // Solo TVs con Moonlight WebRTC
    Both       // Ambos
}

/// <summary>
/// Ritmo al que Windows compone la pantalla de la sesión RDP (DWMFRAMEINTERVAL, ms enteros).
/// </summary>
public enum RdpCompositionMode
{
    /// <summary>
    /// Compone bastante más rápido que los fps (60 fps → 250 Hz) y Sunshine manda cada fotograma
    /// al llegar: sin fotogramas repetidos, a cambio de más CPU y GPU por sesión.
    /// </summary>
    Smooth,
    /// <summary>
    /// Compone a los fps (60 fps → 17 ms, 58,8 Hz): bastante menos CPU y GPU por sesión, con algún
    /// fotograma repetido de vez en cuando. Pensado para escritorio y para muchas sesiones.
    /// </summary>
    Efficient,
}

/// <summary>Ajustes de la pantalla de una sesión RDP que se aplican al conectar.</summary>
/// <param name="Scale">Escala en % (ver <see cref="RdpDisplayScales"/>).</param>
public sealed record RdpDisplayOptions(int Scale = 100, RdpCompositionMode Composition = RdpCompositionMode.Smooth)
{
    public static readonly RdpDisplayOptions Default = new();
}

/// <summary>Escalas de pantalla que ofrece Windows (las de Configuración → Pantalla).</summary>
public static class RdpDisplayScales
{
    public static readonly int[] Values = [100, 125, 150, 175, 200, 225, 250, 300];

    /// <summary>La escala admitida más cercana; 0 o negativa = 100 %.</summary>
    public static int Normalize(int scale) =>
        scale <= 0 ? 100 : Values.MinBy(v => Math.Abs(v - scale));
}

public static class StreamProtocolExtensions
{
    /// <summary>Valor de <c>stream_protocol</c> en <c>sunshine.conf</c>.</summary>
    public static string ToConfigValue(this StreamProtocol protocol) => protocol switch
    {
        StreamProtocol.Moonlight => "moonlight",
        StreamProtocol.WebRtc    => "webrtc",
        _                        => "both",
    };

    /// <summary>Interpreta un valor de <c>stream_protocol</c>; false si no es uno conocido.</summary>
    public static bool TryParseConfigValue(string? value, out StreamProtocol protocol)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "moonlight": protocol = StreamProtocol.Moonlight; return true;
            case "webrtc":    protocol = StreamProtocol.WebRtc;    return true;
            case "both":      protocol = StreamProtocol.Both;      return true;
            default:          protocol = StreamProtocol.Both;      return false;
        }
    }
}

public class StreamSession
{
    public Guid         Id              { get; set; } = Guid.NewGuid();
    public string       Name            { get; set; } = "";
    public string       Username        { get; set; } = "";
    public string       Domain          { get; set; } = ".";
    internal string     Password        { get; set; } = "";   // no expuesto en la API
    /// <summary>
    /// Usuario del panel web de Sunshine. Si lo defines tú se respeta; si lo dejas vacío,
    /// OpenStreamMS lo genera una vez y lo persiste. Lo expone la API porque la UI muestra
    /// las credenciales para que puedas iniciar sesión en el panel.
    /// </summary>
    public string       SunshineAuthUser { get; set; } = "";
    /// <summary>
    /// Contraseña en claro del panel web de Sunshine. Reglas idénticas a <see cref="SunshineAuthUser"/>.
    /// Se reescribe en <c>sunshine_state.json</c> en cada arranque, así si la cambias aquí
    /// la próxima vez que arranque Sunshine ya estará aplicada.
    /// </summary>
    public string       SunshineAuthPass { get; set; } = "";
    /// <summary>
    /// Si true, OpenStreamMS genera credenciales nuevas para el panel Sunshine al
    /// crear cada proceso Sunshine y tambiÃ©n repara automÃ¡ticamente un 401 del proxy.
    /// Los dispositivos emparejados se conservan en <c>sunshine_state.json</c>.
    /// </summary>
    public bool         RotateSunshineCredentials { get; set; } = false;
    public string       SunshineExePath { get; set; } = "";
    public bool         RdpBackground   { get; set; } = true;
    public bool         VddEnabled      { get; set; } = false;
    public int          RdpWidth        { get; set; } = 1920;
    public int          RdpHeight       { get; set; } = 1080;
    /// <summary>Framerate objetivo para la sesiÃ³n RDP. Defecto: 60 fps.</summary>
    public int          RdpFrameRate    { get; set; } = 60;
    /// <summary>Profundidad de color RDP en bits por pixel. Valores FreeRDP habituales: 16, 24 o 32.</summary>
    public int          RdpColorDepth   { get; set; } = 32;
    /// <summary>Escala de la pantalla de la sesión RDP en % (100 = sin escalar). Ver RdpDisplayScales.</summary>
    public int          RdpDisplayScale { get; set; } = 100;
    /// <summary>Ritmo de composición de la sesión RDP.</summary>
    public RdpCompositionMode RdpComposition { get; set; } = RdpCompositionMode.Smooth;
    /// <summary>
    /// Si true, la sesión se lanza automáticamente cuando el servicio arranca o reinicia.
    /// </summary>
    public bool         Enabled          { get; set; } = false;
    /// <summary>
    /// Si true, lanza la sesión con un perfil aislado para que la misma app pueda
    /// abrirse simultáneamente en la sesión local y en la de stream.
    /// </summary>
    public bool         UseStreamProfile { get; set; } = false;
    /// <summary>
    /// Aislamiento nativo SIN software de terceros: la sesión corre bajo un usuario
    /// local dedicado (osms-xxxxxxxxxxxx) que OpenStreamMS crea y gestiona. Perfil,
    /// HKCU y ACLs propios — aislamiento real de SO.
    /// Cuando está activo, Username/Domain/Password de esta sesión se ignoran; la
    /// contraseña del usuario aislado es aleatoria, se rota en cada arranque y no
    /// se persiste nunca.
    /// </summary>
    public bool         Isolated         { get; set; } = false;
    /// <summary>
    /// Solo con <see cref="Isolated"/>: si true, el perfil del usuario aislado
    /// (C:\Users\osms-...) se borra al detener la sesión — cada arranque parte de
    /// cero. false = perfil persistente (juegos instalados se conservan).
    /// </summary>
    public bool         IsolatedEphemeral { get; set; } = false;
    /// <summary>
    /// Solo con <see cref="Isolated"/>: usuario dedicado que se reutilizó de una sesión
    /// borrada (con su perfil: login de Steam, partidas, ajustes). Null = el que sale del Id.
    /// </summary>
    public string?      IsolatedUsername { get; set; }

    /// <summary>Usuario de Windows dedicado de una sesión aislada.</summary>
    public string GetIsolatedUser() =>
        IsolatedUsername ?? OpenStreamMS.Core.Helpers.IsolatedUserManager.GetUsername(Id);
    /// <summary>
    /// Sesión local: Sunshine corre en el escritorio físico de este PC (la sesión de consola),
    /// con el usuario que lo tenga abierto. No se crea sesión RDP y al detenerla no se cierra
    /// la sesión de Windows. Las opciones de RDP, usuario, perfil y aislamiento no se aplican.
    /// </summary>
    public bool         Local            { get; set; } = false;
    /// <summary>
    /// Puerto base de Sunshine (<c>port</c> en <c>sunshine.conf</c>). Es el que usan los clientes
    /// Moonlight y del que Sunshine deriva el resto: panel HTTPS = <c>StreamPort + 1</c>,
    /// vídeo/audio/control = <c>StreamPort + 9..13</c>, RTSP = <c>StreamPort + 21</c>.
    /// Defecto: 47989.
    /// </summary>
    public int          SunshineStreamPort { get; set; } = 47989;

    /// <summary>Puerto HTTPS del panel de gestión. Derivado: <c>StreamPort + 1</c>.</summary>
    public int          SunshineWebPort  => SunshineStreamPort + 1;
    /// <summary>
    /// Protocolos que sirve Sunshine. Se puede cambiar en caliente desde OpenStreamMS o desde el
    /// interruptor de la pestaña Red del panel de Sunshine: el último cambio gana.
    /// </summary>
    public StreamProtocol StreamProtocol    { get; set; } = StreamProtocol.Both;
    /// <summary>Puerto TCP al que conectan las TVs Moonlight WebRTC (<c>webrtc_port</c>). Defecto: 8000.</summary>
    public int          WebRtcPort          { get; set; } = SunshinePorts.DefaultWebRtcPort;
    /// <summary>Primer puerto UDP del vídeo y audio WebRTC (<c>webrtc_media_port_min</c>).</summary>
    public int          WebRtcMediaPortMin  { get; set; } = SunshinePorts.DefaultWebRtcMediaPortMin;
    /// <summary>Último puerto UDP del vídeo y audio WebRTC (<c>webrtc_media_port_max</c>).</summary>
    public int          WebRtcMediaPortMax  { get; set; } = SunshinePorts.DefaultWebRtcMediaPortMin + SunshinePorts.WebRtcMediaPortCount - 1;
    /// <summary>Si Sunshine atiende a clientes Moonlight.</summary>
    public bool         MoonlightEnabled    => StreamProtocol != StreamProtocol.WebRtc;
    /// <summary>Si Sunshine atiende a TVs Moonlight WebRTC.</summary>
    public bool         WebRtcEnabled       => StreamProtocol != StreamProtocol.Moonlight;
    /// <summary>
    /// Nombre que publica Sunshine por mDNS/Moonlight. Si es null o vacío se usa <see cref="Name"/>.
    /// </summary>
    public string?      SunshineName        { get; set; }
    /// <summary>
    /// Método de captura de Sunshine (ddx, wgc, nvfbc). wgc suele ser obligatorio dentro de sesiones RDP.
    /// null = auto-detección de Sunshine.
    /// </summary>
    public string?      Capture             { get; set; }
    /// <summary>
    /// Encoder de Sunshine (nvenc, quicksync, amdvce, software). null = auto-detección.
    /// </summary>
    public string?      Encoder             { get; set; }
    /// <summary>
    /// Nombre del adaptador/monitor a capturar (<c>output_name</c>). Útil cuando hay varios monitores
    /// (real + VDD). null = Sunshine elige el principal.
    /// </summary>
    public string?      OutputName          { get; set; }
    /// <summary>
    /// Origen permitido para el panel web de Sunshine: "pc", "lan" o "wan". null = defecto Sunshine
    /// (lan). Ojo con "pc": en algunos escenarios bloquea el acceso desde otra sesión Windows
    /// del mismo equipo aunque la IP sea 127.0.0.1.
    /// </summary>
    public string?      OriginWebUiAllowed  { get; set; }
    public SessionState State           { get; set; } = SessionState.Created;
    public uint         RdpSessionId    { get; set; }
    public int          SunshinePid     { get; set; }
    internal int        FreeRdpPid      { get; set; }   // no expuesto en la API ni persistido
    public DateTime     CreatedAt       { get; set; } = DateTime.UtcNow;
    public DateTime?    StartedAt       { get; set; }
    public DateTime?    StoppedAt       { get; set; }
    public string?      ErrorMessage    { get; set; }
    /// <summary>
    /// Paso del arranque en curso, para el panel: "user", "rdp", "profile", "unlock", "sunshine".
    /// Null fuera del arranque. No se persiste.
    /// </summary>
    public string?      StartPhase      { get; set; }
    /// <summary>Paso del arranque en el que falló el último intento (mismos valores que StartPhase). No se persiste.</summary>
    public string?      FailedPhase     { get; set; }
    /// <summary>
    /// Clientes transmitiendo ahora por Moonlight y por WebRTC (TVs), según el Sunshine de la
    /// sesión (<see cref="OpenStreamMS.Services.OpenStream.ActiveStreamsMonitor"/>). Null si no se sabe:
    /// sesión parada o Sunshine sin el endpoint. No se persiste.
    /// </summary>
    public int?         ClientsMoonlight { get; set; }
    public int?         ClientsWebRtc    { get; set; }
    /// <summary>
    /// Sesión anfitriona del mosaico en el que está esta sesión (la propia si es la anfitriona);
    /// null fuera de un mosaico. Lo mantiene <see cref="OpenStreamMS.Services.Mosaic.MosaicService"/>. No se persiste.
    /// </summary>
    public Guid?        MosaicHost       { get; set; }
    /// <summary>Posición en el mosaico por orden de llegada (0 = anfitriona; el mando N va a la N). No se persiste.</summary>
    public int?         MosaicIndex      { get; set; }
    /// <summary>Sesiones en el mosaico, solo en la anfitriona. No se persiste.</summary>
    public int?         MosaicCount      { get; set; }
    /// <summary>Solo en la anfitriona: con dos sesiones, una encima de otra. No se persiste.</summary>
    public bool         MosaicStacked    { get; set; }
}
