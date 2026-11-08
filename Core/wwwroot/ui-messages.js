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
    // No rule matched: show the server's own message. A generic "operation failed" hid the
    // real cause (FreeRDP exit code, monitor error…) and left users with nothing to report.
    text = text.trim();
    if (!text || /^<!doctype|^<html/i.test(text)) return this.t('error.request');
    return text.length > 600 ? text.slice(0, 600) + '…' : text;
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
  I18n.driverServiceState = function(state) {
    const key = { Running: 'driver.log.running', Stopped: 'driver.log.stopped' }[state];
    return key ? this.t(key) : String(state || '—');
  };
  // Compatibility with the existing Spanish-text driver API. Keep timestamps,
  // paths, exit codes and unrecognized vendor diagnostics; render as textContent.
  I18n.driverLog = function(raw) {
    const line = String(raw ?? '');
    const stamp = line.match(/^(\[\d{2}:\d{2}:\d{2}\] )(.*)$/s);
    const prefix = stamp?.[1] || '';
    const text = stamp?.[2] ?? line;
    const translate = (key, params) => prefix + this.t('driver.log.' + key, params);
    let m;
    if ((m = text.match(/^--- (Inicio|Fin): (install|uninstall) \((\d{2}:\d{2}:\d{2})\) ---$/))) {
      return translate(m[1] === 'Inicio' ? 'start' : 'end', {
        action: this.t(m[2] === 'install' ? 'vigem.btn.install' : 'vigem.btn.uninstall'), time: m[3],
      });
    }
    if ((m = text.match(/^Usando el instalador incluido: (.+)$/s))) return translate('bundled', { path: m[1] });
    if ((m = text.match(/^Descargando última release de (.+)…$/s))) return translate('download', { repo: m[1] });
    if ((m = text.match(/^Asset: (.+)$/s))) return translate('asset', { name: m[1] });
    if ((m = text.match(/^Descargado en: (.+)$/s))) return translate('downloaded', { path: m[1] });
    if ((m = text.match(/^Ejecutando instalador silencioso \((.*)\)…$/s))) return translate('executing', { args: m[1] });
    if ((m = text.match(/^✗ No se encontró el comando de desinstalación de (.+) en el registro\.$/s))) return translate('noUninstall', { name: m[1] });
    if ((m = text.match(/^Usando comando de desinstalación registrado: (.+)$/s))) return translate('uninstallCommand', { command: m[1] });
    if ((m = text.match(/^Instalador salió con código (-?\d+)\.$/))) return translate('exitCode', { code: m[1] });
    if ((m = text.match(/^✓ (.+) (instalado|desinstalado) correctamente\.$/s))) return translate(m[2] === 'instalado' ? 'installed' : 'uninstalled', { name: m[1] });
    if (text === '⚠ Se requiere reiniciar Windows para completar la operación.') return prefix + this.t('vigem.rebootRequired');
    if (text === '✗ Cancelado por el usuario.') return translate('cancelled');
    if (text === '✗ Fallo fatal durante la instalación (revisa el log de Windows Installer).') return translate('fatal');
    if (text === '✗ Código de salida desconocido.') return translate('unknownExit');
    if ((m = text.match(/^Estado actual: Installed=(True|False), Version=(.*), ServiceState=(.*)$/s))) {
      return translate('status', { installed: this.t('driver.log.' + (m[1] === 'True' ? 'yes' : 'no')), version: m[2], service: this.driverServiceState(m[3]) });
    }
    if ((m = text.match(/^(?:ERROR: )?No se encontró el instalador \.exe en la última release de (.+)\.$/s))) return translate('missingInstaller', { repo: m[1] });
    if ((m = text.match(/^ERROR: (.*)$/s))) return translate('error', { detail: m[1] });
    return line;
  };
})();
