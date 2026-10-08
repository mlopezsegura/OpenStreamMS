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
    /// Puerto base de Sunshine (<c>port</c> en <c>sunshine.conf</c>). Es el que usan los clientes
    /// Moonlight y del que Sunshine deriva el resto: panel HTTPS = <c>StreamPort + 1</c>,
    /// vídeo/audio/control = <c>StreamPort + 9..13</c>, RTSP = <c>StreamPort + 21</c>.
    /// Defecto: 47989.
    /// </summary>
    public int          SunshineStreamPort { get; set; } = 47989;

    /// <summary>Puerto HTTPS del panel de gestión. Derivado: <c>StreamPort + 1</c>.</summary>
    public int          SunshineWebPort  => SunshineStreamPort + 1;
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
}
