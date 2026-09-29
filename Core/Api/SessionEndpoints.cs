using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace OpenStreamMS.Core.Api;

// ── DTOs ─────────────────────────────────────────────────────────────────────

/// <summary>Datos para crear una nueva sesión de streaming.</summary>
public record CreateSessionRequest(
    /// <summary>Nombre descriptivo de la sesión.</summary>
    string Name,
    /// <summary>Nombre de usuario Windows para la sesión RDP.</summary>
    string Username,
    /// <summary>Contraseña del usuario.</summary>
    string Password,
    /// <summary>Dominio del usuario (usa '.' para equipo local).</summary>
    string Domain = ".",
    /// <summary>Ruta a sunshine.exe. Si se omite usa la configuración global del servicio.</summary>
    string? SunshineExePath = null,
    /// <summary>Lanzar mstsc minimizado/oculto (recomendado en producción).</summary>
    bool RdpBackground = true,
    /// <summary>Activar parche de pantalla virtual en apps.json de Sunshine.</summary>
    bool VddEnabled = false,
    /// <summary>Resolución horizontal de la sesión RDP (píxeles). Por defecto 1920.</summary>
    int RdpWidth = 1920,
    /// <summary>Resolución vertical de la sesión RDP (píxeles). Por defecto 1080.</summary>
    int RdpHeight = 1080,
    /// <summary>Framerate objetivo de la sesiÃ³n RDP. Por defecto 60 fps.</summary>
    int RdpFrameRate = 60,
    /// <summary>Profundidad de color RDP en bits por pixel. Por defecto 32 bpp.</summary>
    int RdpColorDepth = 32,
    /// <summary>
    /// Si true, la sesión se lanza automáticamente al arrancar o reiniciar el servicio.
    /// Puede cambiarse después con PATCH /api/sessions/{id}/enabled.
    /// </summary>
    bool Enabled = false,
    /// <summary>
    /// Si true, lanza la sesión con un perfil aislado de Windows.
    /// Permite abrir la misma app (Steam, Chrome…) en la sesión local y en la de stream con menos conflictos de perfil.
    /// </summary>
    bool UseStreamProfile = false,
    /// <summary>
    /// Puerto base de Sunshine/Moonlight (defecto: 47989). El panel HTTPS se sirve en StreamPort+1
    /// pero no se expone externamente — OpenStreamMS hace reverse proxy.
    /// </summary>
    int SunshineStreamPort = 47989,
    /// <summary>Nombre publicado por Sunshine (mDNS/Moonlight). Vacío = nombre de la sesión.</summary>
    string? SunshineName = null,
    /// <summary>Método de captura: "ddx", "wgc" o "nvfbc". Vacío = auto-detección.</summary>
    string? Capture = null,
    /// <summary>Encoder: "nvenc", "quicksync", "amdvce" o "software". Vacío = auto-detección.</summary>
    string? Encoder = null,
    /// <summary>Nombre del adaptador/monitor a capturar (output_name). Vacío = Sunshine elige el principal.</summary>
    string? OutputName = null,
    /// <summary>Origen permitido para el panel web: "pc", "lan" o "wan". Vacío = defecto Sunshine (lan).</summary>
    string? OriginWebUiAllowed = null,
    /// <summary>
    /// Usuario para el panel web de Sunshine. Vacío = OpenStreamMS lo genera automáticamente
    /// la primera vez. Si lo defines, se respeta y se reescribe en sunshine_state.json en cada arranque.
    /// </summary>
    string? SunshineAuthUser = null,
    /// <summary>Contraseña en claro para el panel web de Sunshine. Reglas idénticas a SunshineAuthUser.</summary>
    string? SunshineAuthPass = null,
    /// <summary>Si true, rota las credenciales del panel Sunshine al arrancar la sesiÃ³n y ante desincronizaciones.</summary>
    bool RotateSunshineCredentials = false,
    /// <summary>
    /// Aislamiento nativo (sin Sandboxie): la sesión corre bajo un usuario local dedicado
    /// (osms-...) creado y gestionado por OpenStreamMS, con perfil y registro propios.
    /// Con esto activo, Username/Password/Domain se ignoran (pueden ir vacíos).
    /// </summary>
    bool Isolated = false,
    /// <summary>Solo con Isolated: borrar el perfil del usuario dedicado al detener la sesión (cada arranque parte de cero).</summary>
    bool IsolatedEphemeral = false
);

/// <summary>Datos editables de una sesión ya creada (requiere sesión detenida).</summary>
public record UpdateSessionConfigRequest(
    /// <summary>Nombre descriptivo de la sesión.</summary>
    string Name,
    /// <summary>Ruta a sunshine.exe. Null para usar la configuración global.</summary>
    string? SunshineExePath,
    /// <summary>Lanzar mstsc minimizado/oculto.</summary>
    bool RdpBackground,
    /// <summary>Activar parche de pantalla virtual en apps.json de Sunshine.</summary>
    bool VddEnabled,
    /// <summary>Resolución horizontal (px).</summary>
    int RdpWidth,
    /// <summary>Resolución vertical (px).</summary>
    int RdpHeight,
    /// <summary>Framerate objetivo de la sesiÃ³n RDP.</summary>
    int RdpFrameRate,
    /// <summary>Profundidad de color RDP en bits por pixel.</summary>
    int RdpColorDepth,
    /// <summary>Puerto base de Sunshine/Moonlight.</summary>
    int SunshineStreamPort,
    /// <summary>Si true, usa un perfil aislado para la sesión de stream.</summary>
    bool UseStreamProfile,
    /// <summary>Nombre publicado por Sunshine. Vacío = nombre de la sesión.</summary>
    string? SunshineName = null,
    /// <summary>Método de captura: "ddx", "wgc" o "nvfbc". Vacío = auto-detección.</summary>
    string? Capture = null,
    /// <summary>Encoder: "nvenc", "quicksync", "amdvce" o "software". Vacío = auto-detección.</summary>
    string? Encoder = null,
    /// <summary>Nombre del adaptador/monitor (output_name). Vacío = Sunshine elige el principal.</summary>
    string? OutputName = null,
    /// <summary>Origen permitido para el panel web: "pc", "lan" o "wan". Vacío = defecto Sunshine.</summary>
    string? OriginWebUiAllowed = null,
    /// <summary>
    /// Usuario del panel web de Sunshine. Vacío/null = mantener el actual. Para forzar cambio,
    /// envía un valor concreto; se reescribirá en sunshine_state.json en el próximo arranque.
    /// </summary>
    string? SunshineAuthUser = null,
    /// <summary>Contraseña del panel. Vacío/null = mantener la actual.</summary>
    string? SunshineAuthPass = null,
    /// <summary>Si true, rota las credenciales del panel Sunshine al arrancar la sesiÃ³n y ante desincronizaciones.</summary>
    bool RotateSunshineCredentials = false,
    /// <summary>Aislamiento nativo: usuario local dedicado gestionado por el servicio.</summary>
    bool Isolated = false,
    /// <summary>Solo con Isolated: borrar el perfil del usuario dedicado al detener.</summary>
    bool IsolatedEphemeral = false
);

/// <summary>Cuerpo para activar o desactivar el auto-arranque de una sesión.</summary>
public record SetEnabledRequest(
    /// <summary>true para habilitar el auto-arranque al reiniciar el servicio; false para deshabilitarlo.</summary>
    bool Enabled
);

/// <summary>Detalles de una sesión de streaming.</summary>
public record SessionResponse(
    Guid         Id,
    string       Name,
    string       Username,
    string       Domain,
    string       SunshineExePath,
    bool         RdpBackground,
    bool         VddEnabled,
    int          RdpWidth,
    int          RdpHeight,
    int          RdpFrameRate,
    int          RdpColorDepth,
    /// <summary>Si true, la sesión se relanza automáticamente al reiniciar el servicio.</summary>
    bool         Enabled,
    /// <summary>Si true, la sesión usa un perfil aislado para procesos de stream.</summary>
    bool         UseStreamProfile,
    /// <summary>Puerto base Moonlight (escritura). Panel HTTPS derivado = StreamPort+1.</summary>
    int          SunshineStreamPort,
    /// <summary>Puerto HTTPS del panel (lectura, derivado). No se expone externamente.</summary>
    int          SunshineWebPort,
    /// <summary>Nombre publicado por Sunshine. Null si usa el nombre de la sesión.</summary>
    string?      SunshineName,
    /// <summary>Método de captura configurado (ddx/wgc/nvfbc). Null = auto-detección.</summary>
    string?      Capture,
    /// <summary>Encoder configurado. Null = auto-detección.</summary>
    string?      Encoder,
    /// <summary>Adaptador/monitor a capturar (output_name). Null = principal.</summary>
    string?      OutputName,
    /// <summary>Origen permitido para el panel web (pc/lan/wan). Null = defecto Sunshine.</summary>
    string?      OriginWebUiAllowed,
    /// <summary>Usuario del panel web de Sunshine (para login en https://127.0.0.1:&lt;web&gt;).</summary>
    string       SunshineAuthUser,
    /// <summary>Contraseña en claro del panel web de Sunshine.</summary>
    string       SunshineAuthPass,
    /// <summary>Si true, las credenciales del panel se rotan automÃ¡ticamente.</summary>
    bool         RotateSunshineCredentials,
    SessionState State,
    uint         RdpSessionId,
    int          SunshinePid,
    DateTime     CreatedAt,
    DateTime?    StartedAt,
    DateTime?    StoppedAt,
    string?      ErrorMessage
);

/// <summary>Respuesta con las últimas líneas del log de una sesión.</summary>
public record SessionLogsResponse(
    Guid     SessionId,
    string   SessionName,
    int      LinesReturned,
    string[] Lines
);

/// <summary>Credenciales a verificar antes de crear una sesión.</summary>
public record TestCredentialsRequest(
    /// <summary>Nombre de usuario Windows.</summary>
    string Username,
    /// <summary>Contraseña del usuario.</summary>
    string Password,
    /// <summary>Dominio (usa '.' para equipo local).</summary>
    string Domain = "."
);

/// <summary>Resultado de la verificación de credenciales.</summary>
public record TestCredentialsResponse(
    /// <summary>true si las credenciales son válidas en Windows.</summary>
    bool   Success,
    /// <summary>Mensaje descriptivo del resultado.</summary>
    string Message
);

/// <summary>Solicitud de emparejamiento con un cliente Moonlight.</summary>
public record PairRequest(
    /// <summary>PIN de 4 dígitos mostrado por el cliente Moonlight al intentar conectar.</summary>
    string Pin,
    /// <summary>Nombre descriptivo para el cliente (Moonlight PC, iPad, etc.). Opcional.</summary>
    string Name = "Moonlight",
    /// <summary>
    /// Solicitud pendiente a la que va el PIN (Sunshine 2026.9+). Opcional: si solo hay
    /// una se usa esa; si hay varias la API responde <c>multiple_pending</c> con la lista.
    /// </summary>
    string? PairingId = null
);

/// <summary>Solicitud de emparejamiento pendiente en Sunshine (Moonlight esperando PIN).</summary>
public record PendingPairing(string Id, string? Name, string? Address);

/// <summary>Cliente Moonlight pareado con una sesión.</summary>
public record SunshineClient(string Uuid, string Name);

/// <summary>Error estructurado para operaciones del proxy a Sunshine.</summary>
/// <param name="Code">
/// Código identificador. Valores posibles:
/// <list type="bullet">
/// <item><c>invalid_pin</c> — el PIN introducido no coincide o ya expiró.</item>
/// <item><c>credentials_rotated</c> — el panel devolvió 401; hemos borrado las credenciales internas, hay que reiniciar la sesión.</item>
/// <item><c>session_not_running</c> — la sesión no está activa.</item>
/// <item><c>no_pending_pairing</c> — ningún Moonlight está esperando PIN (Sunshine 2026.9+).</item>
/// <item><c>multiple_pending</c> — varios Moonlight esperan PIN; elegir uno de <c>Pairings</c>.</item>
/// <item><c>proxy_error</c> — error genérico hablando con Sunshine.</item>
/// </list>
/// </param>
/// <param name="Message">Descripción legible opcional.</param>
/// <param name="Pairings">Solicitudes pendientes, solo con <c>multiple_pending</c>.</param>
public record SunshineError(string Code, string? Message = null,
                            IReadOnlyList<PendingPairing>? Pairings = null);

// ── ENDPOINTS ─────────────────────────────────────────────────────────────────

public static class SessionEndpoints
{
    public static IEndpointRouteBuilder MapSessionApi(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/sessions")
                       .WithTags("Sessions");

        // POST /api/sessions/test-credentials  (antes que /{id:guid} para no colisionar)
        group.MapPost("/test-credentials", TestCredentials)
             .WithName("TestCredentials")
             .WithSummary("Probar credenciales de usuario Windows")
             .WithDescription("""
                Verifica que el usuario y contraseña son válidos en Windows mediante LogonUser (LOGON_NETWORK).
                No crea ninguna sesión ni carga el perfil del usuario.
                Debe llamarse y obtener Success=true antes de crear una sesión.
                """);

        // GET /api/sessions
        group.MapGet("/", GetAll)
             .WithName("GetAllSessions")
             .WithSummary("Listar todas las sesiones")
             .WithDescription("Devuelve la lista completa de sesiones registradas con su estado actual.");

        // POST /api/sessions
        group.MapPost("/", Create)
             .WithName("CreateSession")
             .WithSummary("Crear una sesión")
             .WithDescription("""
                Registra una nueva sesión de streaming.
                La sesión se crea en estado 'Created' y no se inicia hasta llamar a /start.
                Usa 'Enabled=true' para que el servicio la lance automáticamente en cada reinicio.
                """);

        // GET /api/sessions/{id}
        group.MapGet("/{id:guid}", GetById)
             .WithName("GetSession")
             .WithSummary("Obtener una sesión")
             .WithDescription("Devuelve los detalles y el estado actual de una sesión específica.");

        // DELETE /api/sessions/{id}
        group.MapDelete("/{id:guid}", Delete)
             .WithName("DeleteSession")
             .WithSummary("Eliminar una sesión")
             .WithDescription("Elimina una sesión. La sesión debe estar detenida antes de poder eliminarse.");

        // POST /api/sessions/{id}/start
        group.MapPost("/{id:guid}/start", Start)
             .WithName("StartSession")
             .WithSummary("Iniciar una sesión")
             .WithDescription("""
                Inicia la sesión: crea la sesión RDP para el usuario configurado y lanza Sunshine en ella.
                La operación es asíncrona; la sesión pasa a estado 'Starting' de inmediato.
                Consulta el estado con GET /api/sessions/{id} y el log con GET /api/sessions/{id}/logs.
                """);

        // POST /api/sessions/{id}/stop
        group.MapPost("/{id:guid}/stop", Stop)
             .WithName("StopSession")
             .WithSummary("Detener una sesión")
             .WithDescription("Detiene Sunshine y marca la sesión como detenida. La sesión RDP se mantiene abierta.");

        // POST /api/sessions/{id}/restart
        group.MapPost("/{id:guid}/restart", Restart)
             .WithName("RestartSession")
             .WithSummary("Reiniciar una sesión")
             .WithDescription("""
                Cierra la sesión por completo (mata FreeRDP, Sunshine y hace logoff de la sesión Windows)
                y la vuelve a iniciar. Funciona incluso si la sesión está colgada en Starting/Stopping/Error.
                Operación asíncrona: responde 202 y la sesión pasa primero a 'Stopping' y luego a 'Starting'.
                """);

        // PATCH /api/sessions/{id}/enabled
        group.MapPatch("/{id:guid}/enabled", SetEnabled)
             .WithName("SetSessionEnabled")
             .WithSummary("Activar o desactivar el auto-arranque")
             .WithDescription("""
                Cambia el flag 'Enabled' de la sesión sin necesidad de recrearla.
                Cuando Enabled=true, el servicio lanzará esta sesión automáticamente
                al arrancar o reiniciarse. El cambio se persiste en sessions.json.
                """);

        // PATCH /api/sessions/{id}/config
        group.MapPatch("/{id:guid}/config", UpdateConfig)
             .WithName("UpdateSessionConfig")
             .WithSummary("Editar la configuración de una sesión")
             .WithDescription("""
                Actualiza la configuración de la sesión (nombre, resolución, puerto de Sunshine, etc.).
                La sesión debe estar detenida. Los cambios se aplican en el próximo inicio.
                """);

        // GET /api/sessions/{id}/logs?lines=200
        group.MapGet("/{id:guid}/logs", GetLogs)
             .WithName("GetSessionLogs")
             .WithSummary("Ver el log de una sesión")
             .WithDescription("Devuelve las últimas N líneas del log de actividad de la sesión.");

        // ── Gestión Sunshine (reverse proxy con Basic Auth interna) ──────────

        // POST /api/sessions/{id}/pair
        group.MapPost("/{id:guid}/pair", PairClient)
             .WithName("PairSessionClient")
             .WithSummary("Emparejar un cliente Moonlight")
             .WithDescription("""
                Envía el PIN mostrado por el cliente Moonlight al Sunshine de la sesión.
                La sesión debe estar activa. OpenStreamMS actúa como proxy con sus
                credenciales internas; el puerto del panel de Sunshine no se expone.
                """);

        // GET /api/sessions/{id}/clients
        group.MapGet("/{id:guid}/clients", ListClients)
             .WithName("ListSessionClients")
             .WithSummary("Listar clientes pareados")
             .WithDescription("Devuelve la lista de clientes Moonlight pareados con la sesión.");

        // DELETE /api/sessions/{id}/clients/{uuid}
        group.MapDelete("/{id:guid}/clients/{uuid}", UnpairClient)
             .WithName("UnpairSessionClient")
             .WithSummary("Eliminar un cliente pareado");

        // DELETE /api/sessions/{id}/clients
        group.MapDelete("/{id:guid}/clients", UnpairAllClients)
             .WithName("UnpairAllSessionClients")
             .WithSummary("Eliminar todos los clientes pareados");

        return app;
    }

    // ── handlers ─────────────────────────────────────────────────────────────

    static Ok<IEnumerable<SessionResponse>> GetAll(StreamSessionService svc) =>
        TypedResults.Ok(svc.GetAll().Select(ToResponse));

    static Results<Created<SessionResponse>, BadRequest<string>> Create(
        CreateSessionRequest req, StreamSessionService svc)
    {
        if (string.IsNullOrWhiteSpace(req.Name))
            return TypedResults.BadRequest("El campo 'Name' es obligatorio.");
        if (string.IsNullOrWhiteSpace(req.Username))
            return TypedResults.BadRequest("El campo 'Username' es obligatorio.");
        if (string.IsNullOrWhiteSpace(req.Password))
            return TypedResults.BadRequest("El campo 'Password' es obligatorio.");

        var session = svc.Create(req.Name, req.Username, req.Domain, req.Password,
                                 req.SunshineExePath, req.RdpBackground, req.VddEnabled,
                                 req.RdpWidth, req.RdpHeight, req.RdpFrameRate, req.RdpColorDepth,
                                 req.Enabled,
                                 req.UseStreamProfile, req.SunshineStreamPort,
                                 req.SunshineName, req.Capture, req.Encoder,
                                 req.OutputName, req.OriginWebUiAllowed,
                                 req.SunshineAuthUser, req.SunshineAuthPass,
                                 req.RotateSunshineCredentials);
        return TypedResults.Created($"/api/sessions/{session.Id}", ToResponse(session));
    }

    static Results<Ok<SessionResponse>, NotFound> GetById(Guid id, StreamSessionService svc)
    {
        var s = svc.Get(id);
        return s is null ? TypedResults.NotFound() : TypedResults.Ok(ToResponse(s));
    }

    static Results<NoContent, NotFound, BadRequest<string>> Delete(Guid id, StreamSessionService svc)
    {
        try
        {
            return svc.Delete(id) ? TypedResults.NoContent() : TypedResults.NotFound();
        }
        catch (InvalidOperationException ex)
        {
            return TypedResults.BadRequest(ex.Message);
        }
    }

    static Results<Accepted<SessionResponse>, NotFound, BadRequest<string>> Start(
        Guid id, StreamSessionService svc)
    {
        var session = svc.Get(id);
        if (session is null) return TypedResults.NotFound();
        try
        {
            svc.Start(id);
            return TypedResults.Accepted($"/api/sessions/{id}", ToResponse(session));
        }
        catch (InvalidOperationException ex)
        {
            return TypedResults.BadRequest(ex.Message);
        }
    }

    static Results<Ok<SessionResponse>, NotFound, BadRequest<string>> Stop(
        Guid id, StreamSessionService svc)
    {
        var session = svc.Get(id);
        if (session is null) return TypedResults.NotFound();
        try
        {
            svc.Stop(id);
            return TypedResults.Ok(ToResponse(session));
        }
        catch (InvalidOperationException ex)
        {
            return TypedResults.BadRequest(ex.Message);
        }
    }

    static Results<Accepted<SessionResponse>, NotFound> Restart(
        Guid id, StreamSessionService svc)
    {
        var session = svc.Get(id);
        if (session is null) return TypedResults.NotFound();
        svc.Restart(id);
        return TypedResults.Accepted($"/api/sessions/{id}", ToResponse(session));
    }

    static Results<Ok<SessionResponse>, NotFound> SetEnabled(
        Guid id, SetEnabledRequest req, StreamSessionService svc)
    {
        try
        {
            var session = svc.SetEnabled(id, req.Enabled);
            return TypedResults.Ok(ToResponse(session));
        }
        catch (KeyNotFoundException)
        {
            return TypedResults.NotFound();
        }
    }

    static Results<Ok<SessionResponse>, NotFound, BadRequest<string>> UpdateConfig(
        Guid id, UpdateSessionConfigRequest req, StreamSessionService svc)
    {
        try
        {
            var session = svc.UpdateConfig(id, req.Name, req.SunshineExePath,
                                           req.RdpBackground, req.VddEnabled,
                                           req.RdpWidth, req.RdpHeight,
                                           req.RdpFrameRate, req.RdpColorDepth,
                                           req.SunshineStreamPort, req.UseStreamProfile,
                                           req.SunshineName, req.Capture, req.Encoder,
                                           req.OutputName, req.OriginWebUiAllowed,
                                           req.SunshineAuthUser, req.SunshineAuthPass,
                                           req.RotateSunshineCredentials);
            return TypedResults.Ok(ToResponse(session));
        }
        catch (KeyNotFoundException)
        {
            return TypedResults.NotFound();
        }
        catch (InvalidOperationException ex)
        {
            return TypedResults.BadRequest(ex.Message);
        }
    }

    static Results<Ok<SessionLogsResponse>, NotFound> GetLogs(
        Guid id, StreamSessionService svc,
        [FromQuery] int lines = 200)
    {
        var session = svc.Get(id);
        if (session is null) return TypedResults.NotFound();

        var logLines = svc.GetLogs(id, Math.Clamp(lines, 1, 5000));
        return TypedResults.Ok(new SessionLogsResponse(id, session.Name, logLines.Length, logLines));
    }

    static Ok<TestCredentialsResponse> TestCredentials(TestCredentialsRequest req)
    {
        var result = CredentialTester.Test(req.Username, req.Domain, req.Password);
        return TypedResults.Ok(new TestCredentialsResponse(result.Success, result.Message));
    }

    // ── Sunshine proxy handlers ──────────────────────────────────────────────

    /// <summary>
    /// Si el panel respondió 401, borra las credenciales internas (para que se regeneren
    /// al próximo arranque) y devuelve un error con <c>code=credentials_rotated</c>.
    /// </summary>
    static SunshineError? HandleAuthFailure(int status, StreamSession session, StreamSessionService svc)
    {
        if (status != 401) return null;
        if (!session.RotateSunshineCredentials)
            return new SunshineError("credentials_mismatch");

        svc.ResetSunshineCredentials(session.Id);
        return new SunshineError("credentials_rotated");
    }

    static async Task<IResult> PairClient(
        Guid id, PairRequest req, StreamSessionService svc, SunshineProxy proxy)
    {
        var session = svc.Get(id);
        if (session is null) return Results.NotFound();
        if (session.State != SessionState.Running)
            return Results.Json(new SunshineError("session_not_running"), statusCode: 400);
        if (string.IsNullOrWhiteSpace(req.Pin) || req.Pin.Length < 4)
            return Results.Json(new SunshineError("invalid_pin"), statusCode: 400);

        var name = string.IsNullOrWhiteSpace(req.Name) ? "Moonlight" : req.Name;

        // Sunshine 2026.9+: el PIN va dirigido a una solicitud pendiente concreta
        // (GET /api/pin → {pairings:[{id,name,address}]}) y POST exige pairing_id; sin él
        // responde 400. Las versiones anteriores no tienen GET /api/pin → flujo antiguo.
        var (listStatus, listBody) = await proxy.SendJsonAsync(session, HttpMethod.Get, "/api/pin");
        if (HandleAuthFailure(listStatus, session, svc) is { } rotatedList)
            return Results.Json(rotatedList, statusCode: 409);

        object payload;
        if (listStatus is >= 200 and < 300 &&
            listBody?.TryGetProperty("pairings", out var arr) == true &&
            arr.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            // El id se reenvía con su tipo JSON original (número o texto según versión).
            var rawIds  = new Dictionary<string, System.Text.Json.JsonElement>();
            var pending = arr.EnumerateArray()
                .Where(p => p.TryGetProperty("id", out var i) && rawIds.TryAdd(i.ToString(), i.Clone()))
                .Select(p => new PendingPairing(
                    p.GetProperty("id").ToString(),
                    p.TryGetProperty("name", out var n) && n.ValueKind == System.Text.Json.JsonValueKind.String ? n.GetString() : null,
                    p.TryGetProperty("address", out var a) && a.ValueKind == System.Text.Json.JsonValueKind.String ? a.GetString() : null))
                .ToList();

            string? pairingId = req.PairingId;
            if (pairingId is not null && pending.All(p => p.Id != pairingId))
                pairingId = null; // caducó o ya se completó: se vuelve a elegir

            if (pairingId is null)
            {
                if (pending.Count == 0)
                    return Results.Json(new SunshineError("no_pending_pairing"), statusCode: 400);
                if (pending.Count > 1)
                    return Results.Json(new SunshineError("multiple_pending", Pairings: pending), statusCode: 409);
                pairingId = pending[0].Id;
            }

            payload = new { pairing_id = rawIds[pairingId], pin = req.Pin, name };
        }
        else
        {
            payload = new { pin = req.Pin, name };
        }

        var (status, body) = await proxy.SendJsonAsync(session, HttpMethod.Post, "/api/pin", payload);

        if (HandleAuthFailure(status, session, svc) is { } rotated)
            return Results.Json(rotated, statusCode: 409);

        if (status is >= 200 and < 300 &&
            body?.TryGetProperty("status", out var ok) == true && ok.GetBoolean())
        {
            // /api/pin sólo desbloquea el handshake con Moonlight. El state se escribe
            // cuando Moonlight completa la fase final, así que no reiniciamos Sunshine aquí.
            svc.ScheduleSunshineStateMirror(id);
            return Results.Ok(new { success = true });
        }

        // 2xx sin status:true → PIN malo o expirado.
        if (status is >= 200 and < 300)
            return Results.Json(new SunshineError("invalid_pin"), statusCode: 400);

        // Sunshine suele explicar el 4xx en {"error": "..."}: se muestra tal cual.
        var detail = body?.TryGetProperty("error", out var err) == true &&
                     err.ValueKind == System.Text.Json.JsonValueKind.String
            ? $"HTTP {status}: {err.GetString()}"
            : $"HTTP {status}";
        return Results.Json(new SunshineError("proxy_error", detail), statusCode: 502);
    }

    static async Task<IResult> ListClients(
        Guid id, StreamSessionService svc, SunshineProxy proxy)
    {
        var session = svc.Get(id);
        if (session is null) return Results.NotFound();
        if (session.State != SessionState.Running)
            return Results.Json(new SunshineError("session_not_running"), statusCode: 400);

        var (status, body) = await proxy.SendJsonAsync(session, HttpMethod.Get, "/api/clients/list");

        if (HandleAuthFailure(status, session, svc) is { } rotated)
            return Results.Json(rotated, statusCode: 409);

        if (status is < 200 or >= 300 || body is null)
            return Results.Json(new SunshineError("proxy_error", $"HTTP {status}"), statusCode: 502);

        var clients = new List<SunshineClient>();
        if (body.Value.TryGetProperty("named_certs", out var list) && list.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            foreach (var item in list.EnumerateArray())
            {
                var uuid = item.TryGetProperty("uuid", out var u) ? u.GetString() ?? "" : "";
                var name = item.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                if (!string.IsNullOrEmpty(uuid))
                    clients.Add(new SunshineClient(uuid, string.IsNullOrEmpty(name) ? uuid : name));
            }
        }
        return Results.Ok(clients.ToArray());
    }

    static async Task<IResult> UnpairClient(
        Guid id, string uuid, StreamSessionService svc, SunshineProxy proxy)
    {
        var session = svc.Get(id);
        if (session is null) return Results.NotFound();
        if (session.State != SessionState.Running)
            return Results.Json(new SunshineError("session_not_running"), statusCode: 400);

        var (status, _) = await proxy.SendJsonAsync(session, HttpMethod.Post,
                                                    "/api/clients/unpair", new { uuid });

        if (HandleAuthFailure(status, session, svc) is { } rotated)
            return Results.Json(rotated, statusCode: 409);

        if (status is >= 200 and < 300)
        {
            svc.ScheduleSunshineStateMirror(id);
            return Results.NoContent();
        }
        return Results.Json(new SunshineError("proxy_error", $"HTTP {status}"), statusCode: 502);
    }

    static async Task<IResult> UnpairAllClients(
        Guid id, StreamSessionService svc, SunshineProxy proxy)
    {
        var session = svc.Get(id);
        if (session is null) return Results.NotFound();
        if (session.State != SessionState.Running)
            return Results.Json(new SunshineError("session_not_running"), statusCode: 400);

        var (status, _) = await proxy.SendJsonAsync(session, HttpMethod.Post,
                                                    "/api/clients/unpair-all");

        if (HandleAuthFailure(status, session, svc) is { } rotated)
            return Results.Json(rotated, statusCode: 409);

        if (status is >= 200 and < 300)
        {
            svc.ScheduleSunshineStateMirror(id);
            return Results.NoContent();
        }
        return Results.Json(new SunshineError("proxy_error", $"HTTP {status}"), statusCode: 502);
    }

    // ── mapping ──────────────────────────────────────────────────────────────

    static SessionResponse ToResponse(StreamSession s) => new(
        s.Id, s.Name, s.Username, s.Domain, s.SunshineExePath,
        s.RdpBackground, s.VddEnabled, s.RdpWidth, s.RdpHeight,
        s.RdpFrameRate, s.RdpColorDepth,
        s.Enabled, s.UseStreamProfile, s.SunshineStreamPort, s.SunshineWebPort,
        s.SunshineName, s.Capture, s.Encoder, s.OutputName, s.OriginWebUiAllowed,
        s.SunshineAuthUser, s.SunshineAuthPass, s.RotateSunshineCredentials,
        s.State, s.RdpSessionId, s.SunshinePid,
        s.CreatedAt, s.StartedAt, s.StoppedAt, s.ErrorMessage);
}
