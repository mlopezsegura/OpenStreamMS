'use strict';

// Compatibility adapter for the existing API's human-readable Spanish messages.
// No session/driver behavior changes. New APIs should return stable message codes.
(() => {
  const rules = [
    [/Detén la sesión antes de/i, 'error.stopSession'],
    [/Ya existe una sesión local/i, 'error.localExists'],
    [/Ya hay una operación|ya está en estado/i, 'error.busy'],
    [/puerto|\bport\b/i, 'error.port'],
    [/Un usuario reutilizado no puede tener perfil efímero/i, 'error.ephemeralReuse'],
    [/Solo una sesión aislada puede reutilizar/i, 'error.onlyIsolated'],
    [/El usuario .* no existe o ya lo usa/i, 'error.reuseUser'],
    [/El puesto necesita un nombre/i, 'error.seatName'],
    [/Detén el puesto antes/i, 'error.stopSeat'],
    [/El monitor principal es de la consola/i, 'error.primaryMonitor'],
    [/ya está asignad[oa] al puesto|El monitor lo está usando el puesto/i, 'error.assigned'],
    [/El puesto no tiene sesión asignada/i, 'seat.needSession'],
    [/La sesión asignada no existe/i, 'seat.sessionMissing'],
    [/No hay (ningún usuario|nadie) con sesión iniciada/i, 'error.noConsoleUser'],
    [/tiene una sesión de Windows abierta/i, 'error.userSignedIn'],
    [/no llegó a estar activa a tiempo|timeout|no termino en 10 s/i, 'error.timeout'],
  ];
  I18n.message = function(raw) {
    if (!raw) return this.t('error.request');
    let text = String(raw);
    try {
      const body = JSON.parse(text);
      if (body && typeof body === 'object' && body.code && this.has('sunmgr.err.' + body.code)) {
        return this.t('sunmgr.err.' + body.code, { detail: /^HTTP \d+$/.test(body.message || '') ? body.message : this.t('error.request') });
      }
      text = typeof body === 'string' ? body : body?.detail || body?.message || body?.title || text;
    } catch { /* Plain text responses are also supported. */ }
    const required = text.match(/El campo '(Name|Username|Password)' es obligatorio/);
    if (required) return this.t('error.required', { field: this.t({ Name: 'new.nameLabel', Username: 'login.user', Password: 'login.password' }[required[1]]).replace(/\s*\*$/, '') });
    for (const [pattern, key] of rules) if (pattern.test(text)) return this.t(key);
    // Keep diagnostics available without exposing untranslated server prose in UI.
    console.warn('OpenStreamMS operation diagnostic:', text);
    return this.t('error.request');
  };
  I18n.responseError = async function(response) { return this.message(await response.text()); };
  I18n.status = function(text) {
    const exact = {
      'Arrancando la sesión…': 'seat.status.start',
      'Encendiendo el monitor…': 'seat.status.monitor',
      'Iniciando agente de captura e imagen...': 'seat.status.agent',
      'Iniciando presentador en la consola...': 'seat.status.presenter',
      'Separando el ratón y el teclado del puesto…': 'seat.status.input',
      'Bloqueando la sesión: el puesto pedirá la contraseña…': 'seat.status.lock',
      'Activo: imagen, audio y periféricos conectados.': 'seat.status.running',
    };
    if (Object.hasOwn(exact, text)) return this.t(exact[text]);
    const resize = text.match(/^(Ajustando|Conectando) la sesión a (\d+)x(\d+) @ (\d+) Hz/);
    if (resize) return this.t(resize[1] === 'Conectando' ? 'seat.status.resizeMic' : 'seat.status.resize', { width: resize[2], height: resize[3], hz: resize[4] });
    if (/^(Desconectado|Apagado):?/.test(text)) return this.t('seat.status.stopped');
    return this.t('seat.status.pending');
  };
})();
