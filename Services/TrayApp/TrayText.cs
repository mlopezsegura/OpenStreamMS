using System.Globalization;

namespace OpenStreamMS.Services.TrayApp;

/// <summary>
/// Textos del icono de bandeja en el idioma de Windows (los mismos que el panel: inglés,
/// español, francés y portugués; el resto, en inglés).
/// </summary>
internal static class TrayText
{
    private static readonly Dictionary<string, Dictionary<string, string>> Texts = new()
    {
        ["en"] = new()
        {
            ["sessions"]       = "Sessions",
            ["sessions.none"]  = "No sessions configured",
            ["seats"]          = "Local seats",
            ["seat.noSession"] = "Assign a session to this seat in OpenStreamMS first.",
            ["action.start"]   = "Turn on",
            ["action.stop"]    = "Turn off",
            ["open"]           = "Open OpenStreamMS",
            ["exit"]           = "Exit",
            ["offline"]        = "OpenStreamMS — service not responding",
            ["state.Running"]  = "On",
            ["state.Starting"] = "Starting…",
            ["state.Stopping"] = "Stopping…",
            ["state.Stopped"]  = "Off",
            ["state.Created"]  = "Off",
            ["state.Error"]    = "Error",
            ["pair.title"]     = "Pairing request",
            ["pair.body"]      = "{0} wants to pair with '{1}'. Click to enter the PIN.",
            ["pair.tvNamed"]   = "The TV {0}",
            ["pair.tvAt"]      = "A TV at {0}",
            ["pair.deviceAt"]  = "A device at {0}",
            ["pair.tv"]        = "A TV with Moonlight WebRTC",
            ["pair.device"]    = "A Moonlight device",
        },
        ["es"] = new()
        {
            ["sessions"]       = "Sesiones",
            ["sessions.none"]  = "Sin sesiones configuradas",
            ["seats"]          = "Puestos locales",
            ["seat.noSession"] = "Asigna antes una sesión a este puesto en OpenStreamMS.",
            ["action.start"]   = "Encender",
            ["action.stop"]    = "Apagar",
            ["open"]           = "Abrir OpenStreamMS",
            ["exit"]           = "Salir",
            ["offline"]        = "OpenStreamMS — el servicio no responde",
            ["state.Running"]  = "Activa",
            ["state.Starting"] = "Iniciando…",
            ["state.Stopping"] = "Deteniendo…",
            ["state.Stopped"]  = "Apagada",
            ["state.Created"]  = "Apagada",
            ["state.Error"]    = "Error",
            ["pair.title"]     = "Solicitud de emparejamiento",
            ["pair.body"]      = "{0} quiere emparejarse con '{1}'. Haz clic para introducir el PIN.",
            ["pair.tvNamed"]   = "La TV {0}",
            ["pair.tvAt"]      = "Una TV en {0}",
            ["pair.deviceAt"]  = "Un dispositivo en {0}",
            ["pair.tv"]        = "Una TV con Moonlight WebRTC",
            ["pair.device"]    = "Un dispositivo Moonlight",
        },
        ["fr"] = new()
        {
            ["sessions"]       = "Sessions",
            ["sessions.none"]  = "Aucune session configurée",
            ["seats"]          = "Postes locaux",
            ["seat.noSession"] = "Attribuez d'abord une session à ce poste dans OpenStreamMS.",
            ["action.start"]   = "Allumer",
            ["action.stop"]    = "Éteindre",
            ["open"]           = "Ouvrir OpenStreamMS",
            ["exit"]           = "Quitter",
            ["offline"]        = "OpenStreamMS — le service ne répond pas",
            ["state.Running"]  = "Active",
            ["state.Starting"] = "Démarrage…",
            ["state.Stopping"] = "Arrêt…",
            ["state.Stopped"]  = "Éteinte",
            ["state.Created"]  = "Éteinte",
            ["state.Error"]    = "Erreur",
            ["pair.title"]     = "Demande d'appairage",
            ["pair.body"]      = "{0} veut s'appairer avec « {1} ». Cliquez pour saisir le PIN.",
            ["pair.tvNamed"]   = "La TV {0}",
            ["pair.tvAt"]      = "Une TV en {0}",
            ["pair.deviceAt"]  = "Un appareil en {0}",
            ["pair.tv"]        = "Une TV avec Moonlight WebRTC",
            ["pair.device"]    = "Un appareil Moonlight",
        },
        ["pt"] = new()
        {
            ["sessions"]       = "Sessões",
            ["sessions.none"]  = "Nenhuma sessão configurada",
            ["seats"]          = "Postos locais",
            ["seat.noSession"] = "Atribua primeiro uma sessão a este posto no OpenStreamMS.",
            ["action.start"]   = "Ligar",
            ["action.stop"]    = "Desligar",
            ["open"]           = "Abrir o OpenStreamMS",
            ["exit"]           = "Sair",
            ["offline"]        = "OpenStreamMS — o serviço não responde",
            ["state.Running"]  = "Ativa",
            ["state.Starting"] = "A iniciar…",
            ["state.Stopping"] = "A parar…",
            ["state.Stopped"]  = "Desligada",
            ["state.Created"]  = "Desligada",
            ["state.Error"]    = "Erro",
            ["pair.title"]     = "Pedido de emparelhamento",
            ["pair.body"]      = "{0} quer emparelhar com '{1}'. Clique para introduzir o PIN.",
            ["pair.tvNamed"]   = "A TV {0}",
            ["pair.tvAt"]      = "Uma TV em {0}",
            ["pair.deviceAt"]  = "Um dispositivo em {0}",
            ["pair.tv"]        = "Uma TV com Moonlight WebRTC",
            ["pair.device"]    = "Um dispositivo Moonlight",
        },
    };

    private static readonly Dictionary<string, string> Current =
        Texts.GetValueOrDefault(CultureInfo.CurrentUICulture.TwoLetterISOLanguageName) ?? Texts["en"];

    public static string Get(string key, params object[] args)
    {
        var text = Current.GetValueOrDefault(key) ?? Texts["en"].GetValueOrDefault(key) ?? key;
        return args.Length == 0 ? text : string.Format(text, args);
    }

    /// <summary>Estado de sesión o puesto tal como lo devuelve la API, traducido.</summary>
    public static string State(string state) => Get("state." + state);
}
