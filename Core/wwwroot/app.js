'use strict';

// ── Estado global ──────────────────────────────────────────────────────────
let sessions         = [];
let sessionsLoaded   = false;
let sessionTimer     = null;
let logTimer         = null;
let logSessionId     = null;
let autoScroll       = true;
let toastTimer       = null;
let credentialsTested = false;   // true solo si el último test de credenciales fue exitoso

// ── Inicialización ─────────────────────────────────────────────────────────
document.addEventListener('DOMContentLoaded', async () => {
  if (!await I18n.ready) return;
  startRefresh();
  rdpRefresh();
  DRIVER_KEYS.forEach(driverRefresh);
  loadOrphans();
  setInterval(whenVisible(() => {
    rdpRefreshPillOnly();
    DRIVER_KEYS.forEach(driverRefreshPillOnly);
  }), 15000);
});

// Re-renderizar al cambiar de idioma para que los botones/badges generados
// dinámicamente por JS se traduzcan inmediatamente.
window.addEventListener('langchange', () => {
  renderSessions();
  if (!byId('settings-modal').classList.contains('hidden')) { renderAppearance(); settingsPane(settingsActivePane); }
  if (!byId('orphans-modal').classList.contains('hidden')) renderOrphans();
  fillReuseOptions();
  if (rdpLastStatus) {
    updateRdpPill(rdpLastStatus);
    renderRdpStatus(rdpLastStatus);
  }
  for (const k of DRIVER_KEYS) {
    const st = drivers[k].lastStatus;
    if (st) { updateDriverPill(k, st); renderDriverStatus(k, st); }
    renderDriverLog(k);
  }
  // Ventana de sesión abierta: puerto derivado, título y botón
  updateDerivedWebPort();
  setSessTexts();
  // Título del modal de log si hay una sesión abierta
  if (logSessionId) {
    const s = sessions.find(x => x.id === logSessionId);
    if (s) document.getElementById('log-title').textContent = t('log.titleFor', { name: s.name });
  }
});

function startRefresh() {
  loadSessions();
  sessionTimer = setInterval(whenVisible(loadSessions), 3000);
}

function stopRefresh() {
  clearInterval(sessionTimer);
  sessionTimer = null;
}

// Con la pestaña oculta no se sondea: una pestaña olvidada no mantiene al
// servicio respondiendo peticiones cada 2-3 s. Al volver se refresca al momento.
function whenVisible(fn) {
  return () => { if (!document.hidden) fn(); };
}

document.addEventListener('visibilitychange', () => {
  if (document.hidden) return;
  if (sessionTimer) loadSessions();
  if (logTimer) fetchLogs();
});

// ── API helper ─────────────────────────────────────────────────────────────
async function api(method, path, body) {
  try {
    const opts = { method, credentials: 'same-origin', headers: {} };
    if (body !== undefined) {
      opts.headers['Content-Type'] = 'application/json';
      opts.body = JSON.stringify(body);
    }
    // Send current UI language so backend can localize API responses
    if (window.I18n && I18n.lang) {
      opts.headers['Accept-Language'] = I18n.lang.replace('_', '-');
    }
    const res = await fetch(path, opts);
    if (res.status === 401) { window.location.href = '/login.html'; return null; }
    return res;
  } catch {
    toast(t('toast.connectionError'), true);
    return null;
  }
}

// ── Sesiones ───────────────────────────────────────────────────────────────
async function loadSessions() {
  // Los puestos también: la tarjeta de cada sesión dice a qué puesto está asociada.
  const [res, seatRes] = await Promise.all([api('GET', '/api/sessions'), api('GET', '/api/seats')]);
  if (!res) return;
  if (!res.ok) return;
  sessions = await res.json();
  sessionsLoaded = true;
  if (seatRes && seatRes.ok) seats = await seatRes.json();
  // Con una sesión o un puesto en marcha este PC está codificando vídeo: en automático,
  // efectos reducidos (sin desenfoque ni animaciones) para no quitarle GPU.
  if (window.Theme) {
    // Sin el dato de clientes (Sunshine sin /api/streams) se supone que una sesión activa emite.
    const streaming = s => s.state === 'Running' &&
      (s.clientsMoonlight == null || s.clientsWebRtc == null || s.clientsMoonlight + s.clientsWebRtc > 0);
    Theme.setBusy(sessions.some(streaming) ||
                  seats.some(s => s.state === 'Running' || s.state === 'Starting'));
    updateEffectsHint();
  }
  renderSessions();
  openPairFromHash();
}

// El aviso de emparejamiento del icono de bandeja abre el panel con #pair=<sesión>: se
// abre el emparejamiento de esa sesión y se quita el hash para no reabrirlo al refrescar.
function openPairFromHash() {
  const m = location.hash.match(/^#pair=([0-9a-f-]{36})$/i);
  if (!m || !sessions.some(s => s.id === m[1])) return;
  history.replaceState(null, '', location.pathname + location.search);
  openSunMgrModal(m[1]);
}
window.addEventListener('hashchange', openPairFromHash);

function renderSessions() {
  // Las píldoras de componentes también repintan: hasta tener la lista, sigue el "Cargando…".
  if (!sessionsLoaded) return;
  const grid  = document.getElementById('sessions-grid');
  const count = document.getElementById('session-count');
  count.textContent = sessions.length;

  const html = sessions.length === 0 ? onboardingHtml() : sessions.map(renderCard).join('');
  renderInto(grid, html);
}

// Una sesión: miniatura de su pantalla, nombre, estado, botón de encender y el resto en "⋯".
function renderCard(s) {
  const isRunning = s.state === 'Running';
  const isBusy    = s.state === 'Starting' || s.state === 'Stopping';
  const screen = s.local
    ? `<div class="screen-thumb screen-local">${uiIcon('pc')}<span class="screen-res">${esc(t('card.localShort'))}</span></div>`
    : `<div class="screen-thumb"><span class="screen-res">${s.rdpWidth}×${s.rdpHeight}</span>
         <span class="screen-fps">${s.rdpFrameRate || 60} ${esc(t('unit.fps'))}</span></div>`;
  const power = isBusy
    ? `<button type="button" class="power-btn busy" disabled title="${esc(t(s.state === 'Starting' ? 'btn.starting' : 'btn.stopping'))}" aria-label="${esc(t(s.state === 'Starting' ? 'btn.starting' : 'btn.stopping'))}">${uiIcon('power')}</button>`
    : `<button type="button" class="power-btn ${isRunning ? 'on' : 'off'}" onclick="${isRunning ? 'stopSession' : 'startSession'}('${s.id}')"
               title="${esc(t(isRunning ? 'btn.stop' : 'btn.start'))}" aria-label="${esc(t(isRunning ? 'btn.stop' : 'btn.start'))}: ${esc(s.name)}" aria-pressed="${isRunning}">${uiIcon('power')}</button>`;

  const protos = [];
  if (s.streamProtocol !== 'WebRtc')
    protos.push(`<span class="chip chip-moonlight" title="Moonlight">${uiIcon('moon')}Moonlight <b>${s.sunshineStreamPort}</b></span>`);
  if (s.streamProtocol !== 'Moonlight')
    protos.push(`<span class="chip chip-webrtc" title="WebRTC · UDP ${s.webRtcMediaPortMin}-${s.webRtcMediaPortMax}">${uiIcon('web')}WebRTC <b>${s.webRtcPort}</b></span>`);

  return `
    <div class="session-card state-${s.state.toLowerCase()}" id="card-${s.id}">
      <div class="card-top">
        ${screen}
        <div class="card-title">
          <div class="card-name">${esc(s.name)}</div>
          <div class="card-user">${s.local ? esc(t('card.localUser'))
            : s.isolated ? (s.isolatedUser
                ? `${esc(t('card.isolatedUserNamed'))} <code class="card-username" title="${esc(t('card.isolatedUser_title'))}">${esc(s.isolatedUser)}</code>`
                : esc(t('card.isolatedUser')))
            : `${esc(s.username)}@${esc(s.domain)}`}</div>
          ${badge(s.state)}
        </div>
        ${power}
      </div>
      <div class="card-chips">${protos.join('')}${sessionSeatLine(s)}${clientsChip(s)}${mosaicChip(s)}</div>
      ${metricsSlot(s)}
      ${startProgress(s)}
      ${rdpWarning(s)}
      ${steamWarning(s)}
      ${s.streamProtocol !== 'Moonlight' && !s.sunshineSupportsWebRtc
        ? `<div class="card-warning">${esc(t('proto.unsupported'))}</div>` : ''}
      ${errorBox(s)}
      <div class="card-footer">
        <label class="toggle" title="${esc(t('card.autostart_title'))}">
          <input type="checkbox" ${s.enabled ? 'checked' : ''} onchange="setEnabled('${s.id}', this.checked)">
          <span class="toggle-track"></span>
          ${esc(t('card.autostart'))}
        </label>
        <div class="card-footer-actions">
          ${isRunning ? `<button type="button" class="btn btn-ghost btn-sm" onclick="openSunMgrModal('${s.id}')">${uiIcon('link')}${esc(t('ux.connect.btn'))}</button>` : ''}
          <button type="button" class="icon-btn more-btn" onclick="sessionMenu(event, '${s.id}')" title="${esc(t('nav.more'))}" aria-label="${esc(t('nav.more'))}: ${esc(s.name)}" aria-haspopup="menu">${uiIcon('more')}</button>
        </div>
      </div>
    </div>`;
}

// Acciones secundarias de una sesión (menú "⋯").
function sessionMenu(e, id) {
  const s = sessions.find(x => x.id === id);
  if (!s) return;
  const running = s.state === 'Running', busy = s.state === 'Starting' || s.state === 'Stopping';
  const btn = e.currentTarget;
  const mosaic = s.mosaicHost
    ? { icon: 'grid', label: t(s.mosaicHost === s.id ? 'mosaic.dissolve' : 'mosaic.leave'), run: () => leaveMosaic(s) }
    : { icon: 'grid', label: t('mosaic.join'), run: () => mosaicHostMenu(btn, s), disabled: s.local || busy, hint: t('mosaic.join_disabled') };
  openMenu(e, [
    { icon: 'restart',  label: t('btn.restart'),  run: () => restartSession(s.id, s.name) },
    mosaic,
    { icon: 'link',     label: t('ux.connect.btn'), run: () => openSunMgrModal(s.id), disabled: !running, hint: t('btn.sunshine_disabled_title') },
    { icon: 'external', label: t('btn.panel'),    run: () => openSunshinePanel(s.id), disabled: !running, hint: t('btn.panel_disabled_title') },
    { icon: 'log',      label: t('btn.log'),      run: () => openLogModal(s.id, s.name) },
    { icon: 'gear',     label: t('btn.settings'), run: () => openEditModal(s.id) },
    { sep: true },
    { icon: 'trash',    label: t('btn.delete'),   run: () => deleteSession(s.id, s.name), danger: true, disabled: running || busy },
  ]);
}
// Quién está conectado ahora (lo que cuenta Sunshine). Sin dato (Sunshine antiguo o
// arrancando), no se muestra nada.
function clientsChip(s) {
  if (s.state !== 'Running' || s.clientsMoonlight == null || s.clientsWebRtc == null) return '';
  const n = s.clientsMoonlight + s.clientsWebRtc;
  const title = t('ux.clients.detail', { moonlight: s.clientsMoonlight, webrtc: s.clientsWebRtc });
  return `<span class="chip chip-clients${n ? ' active' : ''}" title="${esc(title)}">${uiIcon('users')}${esc(
    n === 0 ? t('ux.clients.none') : n === 1 ? t('ux.clients.one') : t('ux.clients.many', { n }))}</span>`;
}

// ── Mosaico: hasta cuatro sesiones en el stream de una (ver MosaicService) ─────
// Partes por número de sesiones, como MosaicLayout.Areas: celdas 0 arriba-izq., 1 arriba-der.,
// 2 abajo-izq., 3 abajo-der.
const MOSAIC_CELLS = { 1: [[0, 1, 2, 3]], 2: [[0, 2], [1, 3]], 3: [[0], [1], [2]], 4: [[0], [1], [2], [3]] };

function mosaicMembers(hostId) {
  return sessions.filter(x => x.mosaicHost === hostId).sort((a, b) => (a.mosaicIndex ?? 0) - (b.mosaicIndex ?? 0));
}

// Etiqueta con un mapa 2×2: la parte de esta sesión resaltada; el título dice qué mando va a cada una.
function mosaicChip(s) {
  if (!s.mosaicHost) return '';
  const members = mosaicMembers(s.mosaicHost);
  const host = sessions.find(x => x.id === s.mosaicHost);
  if (!host || members.length < 2) return '';
  const parts = MOSAIC_CELLS[Math.min(members.length, 4)];
  const own = parts[s.mosaicIndex ?? 0] || [];
  const used = new Set(parts.flat());
  const cells = [0, 1, 2, 3].map(c =>
    `<i class="${own.includes(c) ? 'own' : used.has(c) ? 'used' : ''}"></i>`).join('');
  const list = members.map((m, i) => t('mosaic.member', { n: i + 1, name: m.name })).join(' · ');
  const label = s.id === host.id
    ? t('mosaic.hostChip', { n: members.length })
    : t('mosaic.guestChip', { name: host.name });
  return `<span class="chip chip-mosaic active" title="${esc(t('mosaic.chip_title', { list }))}"><span class="mosaic-map" aria-hidden="true">${cells}</span>${esc(label)}</span>`;
}

// Sesiones a cuyo stream se puede unir s: en marcha, retransmitiendo (o sin dato), sin mosaico
// o anfitrionas con hueco. s no puede ser ya anfitriona de otras.
function mosaicHostMenu(btn, s) {
  const hosts = sessions.filter(h => h.id !== s.id && h.state === 'Running' && !h.local &&
    (!h.mosaicHost || h.mosaicHost === h.id) && (h.mosaicCount || 1) < 4 &&
    (h.clientsMoonlight == null || h.clientsWebRtc == null || h.clientsMoonlight + h.clientsWebRtc > 0));
  if (!hosts.length) { toast(t('mosaic.noHosts'), true); return; }
  // Después del clic actual: si se abre dentro de él, el clic llega al document, que cierra
  // cualquier menú cuando el clic no fue dentro (el botón pulsado ya no está en la página).
  // Si la tarjeta se repintó entretanto, el botón "⋯" es otro.
  const anchor = () => btn.isConnected ? btn : document.querySelector(`#card-${s.id} .more-btn`) || btn;
  setTimeout(() => openMenu({ stopPropagation() {}, currentTarget: anchor() },
    hosts.map(h => ({ icon: 'grid', label: t('mosaic.joinTo', { name: h.name, n: (h.mosaicCount || 1) + 1 }), run: () => joinMosaic(h, s) }))));
}

async function joinMosaic(host, guest) {
  toast(t('mosaic.joining', { name: guest.name, host: host.name }));
  const res = await api('POST', `/api/sessions/${host.id}/mosaic/${guest.id}`);
  if (!res) return;
  if (!res.ok) { toast(await I18n.responseError(res), true); loadSessions(); return; }
  toast(t('mosaic.joined', { name: guest.name, host: host.name }));
  loadSessions();
}

async function leaveMosaic(s) {
  const res = await api('DELETE', `/api/sessions/${s.mosaicHost}/mosaic/${s.id}`);
  if (!res) return;
  if (!res.ok) { toast(await I18n.responseError(res), true); return; }
  toast(t(s.mosaicHost === s.id ? 'mosaic.dissolved' : 'mosaic.left', { name: s.name }));
  loadSessions();
}

// Puesto local asociado a la sesión: su nombre y si ahora mismo la está usando.
function sessionSeatLine(s) {
  const seat = seats.find(x => x.sessionId === s.id);
  if (!seat) return '';
  const inUse = seat.state === 'Running' || seat.state === 'Starting';
  const detail = inUse
    ? t(seat.exclusiveSession ? 'card.seatInUseExclusive' : 'card.seatInUse')
    : t('card.seatIdle');
  return `<span class="chip chip-seat${inUse ? ' active' : ''}" title="${esc(t('card.seat_title'))}: ${esc(detail)}">${uiIcon('seat')}${esc(seat.name)}</span>`;
}

// ── Protocolo de streaming (Moonlight / WebRTC / ambos) ───────────────────
function portSummary(s) {
  const parts = [];
  if (s.streamProtocol !== 'WebRtc')    parts.push(`Moonlight ${s.sunshineStreamPort}`);
  if (s.streamProtocol !== 'Moonlight') parts.push(`WebRTC ${s.webRtcPort} · UDP ${s.webRtcMediaPortMin}-${s.webRtcMediaPortMax}`);
  return `${t('card.port')} ${parts.join(' · ')}`;
}

function getProtocol() {
  const checked = document.querySelector('#sess-modal input[name="SessProto"]:checked');
  return checked ? checked.value : 'Both';
}

function setProtocolFields(protocol, webRtcPort, mediaMin, mediaMax) {
  const id = { Moonlight: 'moonlight', WebRtc: 'webrtc', Both: 'both' }[protocol] || 'both';
  byId(`sess-proto-${id}`).checked = true;
  byId('sess-webrtc-port').value      = webRtcPort || '';
  byId('sess-webrtc-media-min').value = mediaMin   || '';
  byId('sess-webrtc-media-max').value = mediaMax   || '';
  updateProtocolFields();
}

// Los campos WebRTC solo tienen sentido si el protocolo incluye WebRTC.
function updateProtocolFields() {
  const webrtc = getProtocol() !== 'Moonlight';
  byId('sess-webrtc-ports').querySelectorAll('input').forEach(el => { el.disabled = !webrtc; });
}

function readProtocolFields() {
  const num = id => parseInt(byId(id).value, 10) || 0;
  return {
    streamProtocol:     getProtocol(),
    webRtcPort:         num('sess-webrtc-port'),
    webRtcMediaPortMin: num('sess-webrtc-media-min'),
    webRtcMediaPortMax: num('sess-webrtc-media-max'),
  };
}

function badge(state) {
  const cls = {
    Created:  'badge-created',
    Starting: 'badge-starting',
    Running:  'badge-running',
    Stopping: 'badge-stopping',
    Stopped:  'badge-stopped',
    Error:    'badge-error',
  }[state] ?? 'badge-created';
  const label = t(`state.${state}`);
  return `<span class="badge ${cls}"><span class="badge-dot"></span>${esc(label)}</span>`;
}

// ── Acciones de sesión ─────────────────────────────────────────────────────
async function startSession(id) {
  const res = await api('POST', `/api/sessions/${id}/start`);
  if (!res) return;
  if (!res.ok) { toast(await I18n.responseError(res), true); return; }
  toast(t('toast.starting'));
  loadSessions();
}

async function stopSession(id) {
  const res = await api('POST', `/api/sessions/${id}/stop`);
  if (!res) return;
  if (!res.ok) { toast(await I18n.responseError(res), true); return; }
  toast(t('toast.stopped'));
  loadSessions();
}

async function restartSession(id, name) {
  if (!await ask(t('confirm.restart', { name }), { ok: t('btn.restart') })) return;
  const res = await api('POST', `/api/sessions/${id}/restart`);
  if (!res) return;
  if (!res.ok) { toast(await I18n.responseError(res), true); return; }
  toast(t('toast.restarting'));
  loadSessions();
}

// Una sesión aislada se lleva su usuario de Windows y con él el perfil (login de Steam,
// partidas, ajustes): se dice y, por defecto, se conserva para reutilizarlo.
async function deleteSession(id, name) {
  const s = sessions.find(x => x.id === id);
  let keepUser = false;
  if (s && s.isolated && s.isolatedUser) {
    const user = s.isolatedUser;
    const r = await askCheck(t('confirm.deleteIsolated', { name, user }),
                             { label: t('confirm.keepUser', { user }), checked: true },
                             { danger: true, ok: t('btn.delete') });
    if (!r) return;
    keepUser = r.checked;
  } else if (!await ask(t('confirm.delete', { name }), { danger: true, ok: t('btn.delete') })) {
    return;
  }
  const res = await api('DELETE', `/api/sessions/${id}${keepUser ? '?keepUser=true' : ''}`);
  if (!res) return;
  if (res.status === 400) { toast(await I18n.responseError(res), true); return; }
  if (!res.ok && res.status !== 204) { toast(t('toast.deleteError'), true); return; }
  toast(keepUser ? t('toast.deletedKeptUser', { name, user: s.isolatedUser }) : t('toast.deleted', { name }));
  loadSessions();
  loadOrphans();
}

async function setEnabled(id, enabled) {
  const res = await api('PATCH', `/api/sessions/${id}/enabled`, { enabled });
  if (!res || !res.ok) { toast(t('toast.autostartError'), true); loadSessions(); }
  else toast(enabled ? t('toast.autostartOn') : t('toast.autostartOff'));
}

// ═══════════════════════════════════════════════════════════════════════════
//  VENTANA DE SESIÓN: la misma para crear y para editar
// ═══════════════════════════════════════════════════════════════════════════
let sessEditingId = null;   // null = sesión nueva
let sessEditLocal = false;  // al editar, el tipo no cambia: es el de la sesión

function byId(id) { return document.getElementById(id); }

// ── Prueba de credenciales ─────────────────────────────────────────────────
function resetCredTest() {
  credentialsTested = false;
  byId('cred-test-result').style.display = 'none';
}

/** Prueba usuario y contraseña de Windows. Devuelve true si son válidos. */
async function testCredentials() {
  const username = byId('sess-user').value.trim();
  const domain   = byId('sess-domain').value.trim() || '.';
  const password = byId('sess-pass').value;

  if (!username || !password) {
    showCredTestResult(false, t('credTest.needCreds'));
    return false;
  }

  const btn     = byId('btn-test-creds');
  const spinner = byId('btn-test-spinner');
  btn.disabled          = true;
  spinner.style.display = '';

  try {
    const res = await api('POST', '/api/sessions/test-credentials', { username, domain, password });
    if (!res) return false;
    const data = await res.json();
    showCredTestResult(data.success, t(data.success ? 'credTest.success' : 'new.errorTestCreds'));
    credentialsTested = data.success;
    return data.success;
  } finally {
    btn.disabled          = false;
    spinner.style.display = 'none';
  }
}

function showCredTestResult(success, message) {
  const box  = byId('cred-test-result');
  box.className = `cred-test ${success ? 'cred-test-ok' : 'cred-test-err'}`;
  box.style.display = '';
  byId('cred-test-icon').textContent = success ? '✓' : '✗';
  byId('cred-test-msg').textContent  = message;
}

// ── Tipo de sesión y usuario ───────────────────────────────────────────────
function isLocalKind() {
  return sessEditingId ? sessEditLocal : byId('sess-kind-local').checked;
}

// Solo una sesión RDP nueva y no aislada pide usuario y contraseña: la aislada usa un
// usuario que crea el servicio y la local, el del escritorio.
function needsCreds() {
  return !sessEditingId && !isLocalKind() && !byId('sess-isolated').checked;
}

function updateIsolatedUi() {
  const isolated = byId('sess-isolated').checked;
  byId('sess-ephemeral-row').style.display = isolated ? '' : 'none';
  byId('sess-creds-block').style.display   = needsCreds() ? '' : 'none';
  // Reutilizar un usuario conservado: solo al crear una aislada y si hay alguno libre.
  byId('sess-reuse-row').style.display =
    isolated && !sessEditingId && !isLocalKind() && reusableUsers().length ? '' : 'none';
  // Un perfil efímero borraría justo lo que se quiere conservar al reutilizar el usuario.
  const reusing = byId('sess-reuse-row').style.display !== 'none' && !!byId('sess-reuse').value;
  byId('sess-ephemeral').disabled = reusing;
  if (reusing) byId('sess-ephemeral').checked = false;
}

// Usuarios dedicados conservados que se pueden asignar a una sesión nueva.
function reusableUsers() {
  return orphanUsers.filter(o => o.accountExists && !o.signedIn);
}

function fillReuseOptions() {
  const sel = byId('sess-reuse');
  if (!sel) return;
  const current = sel.value;
  const opts = [`<option value="">${esc(t('new.reuseNew'))}</option>`];
  for (const o of reusableUsers()) {
    const steam = o.steamLogin === 'saved' ? t('new.reuseSteamSaved')
                : o.steamLogin === 'missing' ? t('new.reuseSteamMissing') : '';
    opts.push(`<option value="${esc(o.name)}">${esc(o.name)}${steam ? ` — ${esc(steam)}` : ''}</option>`);
  }
  sel.innerHTML = opts.join('');
  sel.value = reusableUsers().some(o => o.name === current) ? current : '';
}

function updateKindUi() {
  byId('sess-modal').classList.toggle('kind-local', isLocalKind());
  updateIsolatedUi();
  updateSessionPreview();
}

// Título y botón según se cree o se edite (también al cambiar de idioma con la ventana abierta).
function setSessTexts() {
  const s = sessEditingId && sessions.find(x => x.id === sessEditingId);
  byId('sess-title').textContent        = s ? t('edit.titleFor', { name: s.name }) : t('new.title');
  byId('sess-submit-label').textContent = s ? t('edit.save') : t('new.create');
}

// ── Abrir, cerrar y guardar ────────────────────────────────────────────────
function openNewModal() {
  sessEditingId = null;
  byId('sess-form').reset();
  byId('sess-error').style.display = 'none';
  byId('sess-modal').classList.remove('mode-edit');
  byId('sess-kind-rdp').checked = true;
  byId('sess-domain').value     = '.';
  byId('sess-bg').checked       = true;
  byId('sess-fps').value        = 60;
  byId('sess-bpp').value        = '32';
  byId('sess-scale').value      = '100';
  byId('sess-composition').value = 'Smooth';
  byId('sess-port').value       = 47989;
  byId('sess-advanced').open    = false;
  setResPreset(1920, 1080);
  setProtocolFields('Both', 8000, 40000, 40019);
  updateDerivedWebPort();
  resetCredTest();
  fillReuseOptions();
  setSessTexts();
  updateKindUi();
  show('sess-modal');
  stopRefresh();
  suggestPorts();
  loadOrphans().then(() => { fillReuseOptions(); updateIsolatedUi(); });
  byId('sess-name').focus();
}

// Rellena el formulario con puertos que no chocan con ninguna sesión existente.
async function suggestPorts() {
  const res = await api('GET', '/api/sessions/ports/suggest');
  if (!res || !res.ok || sessEditingId) return;
  const p = await res.json();
  byId('sess-port').value = p.sunshineStreamPort;
  setProtocolFields(getProtocol(), p.webRtcPort, p.webRtcMediaPortMin, p.webRtcMediaPortMax);
  updateDerivedWebPort();
  updateSessionPreview();
}

function openEditModal(id) {
  const s = sessions.find(x => x.id === id);
  if (!s) return;
  sessEditingId = s.id;
  sessEditLocal = !!s.local;

  byId('sess-form').reset();
  byId('sess-error').style.display = 'none';
  byId('sess-modal').classList.add('mode-edit');
  byId('sess-id').value           = s.id;
  byId('sess-name').value         = s.name;
  byId('sess-isolated').checked   = !!s.isolated;
  byId('sess-ephemeral').checked  = !!s.isolatedEphemeral;
  byId('sess-enabled').checked    = !!s.enabled;
  byId('sess-profile').checked    = !!s.useStreamProfile;
  byId('sess-vdd').checked        = !!s.vddEnabled;
  byId('sess-bg').checked         = !!s.rdpBackground;
  byId('sess-fps').value          = s.rdpFrameRate || 60;
  byId('sess-bpp').value          = String(s.rdpColorDepth || 32);
  byId('sess-scale').value        = String(s.rdpDisplayScale || 100);
  byId('sess-composition').value  = s.rdpComposition || 'Smooth';
  byId('sess-port').value         = s.sunshineStreamPort;
  byId('sess-exe').value          = s.sunshineExePath || '';
  byId('sess-sunshine-name').value = s.sunshineName || '';
  byId('sess-capture').value      = s.capture || '';
  byId('sess-encoder').value      = s.encoder || '';
  byId('sess-output').value       = s.outputName || '';
  byId('sess-origin').value       = s.originWebUiAllowed || '';
  byId('sess-panel-user').value   = s.sunshineAuthUser || '';
  byId('sess-panel-pass').value   = s.sunshineAuthPass || '';
  byId('sess-rotate').checked     = !!s.rotateSunshineCredentials;
  byId('sess-advanced').open      = false;
  setResPreset(s.rdpWidth, s.rdpHeight);
  setProtocolFields(s.streamProtocol, s.webRtcPort, s.webRtcMediaPortMin, s.webRtcMediaPortMax);
  updateDerivedWebPort();
  setSessTexts();
  updateKindUi();
  show('sess-modal');
  stopRefresh();
  byId('sess-name').focus();
}

function closeSessionModal() {
  hide('sess-modal');
  sessEditingId = null;
  startRefresh();
}

function setSessBusy(busy) {
  byId('sess-submit').disabled = busy;
  byId('sess-submit-spinner').style.display = busy ? '' : 'none';
}

async function submitSession(e) {
  e.preventDefault();
  const errEl = byId('sess-error');
  errEl.style.display = 'none';
  const fail = msg => {
    errEl.textContent = msg;
    errEl.style.display = '';
    errEl.scrollIntoView({ block: 'nearest', behavior: 'smooth' });
  };

  const name = byId('sess-name').value.trim();
  if (!name) { fail(t('ux.err.name')); byId('sess-name').focus(); return; }

  const editing  = sessEditingId;
  const local    = isLocalKind();
  const isolated = !local && byId('sess-isolated').checked;
  const port     = parseInt(byId('sess-port').value, 10);
  if (!(port >= 1024 && port <= 65514)) {
    byId('sess-advanced').open = true;
    fail(t('ux.err.port'));
    byId('sess-port').focus();
    return;
  }

  // Con la sesión iniciada, los cambios se aplican deteniéndola y volviéndola a iniciar: se
  // pregunta antes. El estado se consulta al servidor: la lista no se refresca con el modal abierto.
  let restartIfActive = false;
  if (editing) {
    let state = (sessions.find(x => x.id === editing) || {}).state;
    const r = await api('GET', `/api/sessions/${editing}`);
    if (r && r.ok) state = (await r.json()).state;
    if (state === 'Running' || state === 'Starting') {
      if (!await ask(t('confirm.stopToApply'), { danger: true, ok: t('btn.stopAndApply') })) return;
      restartIfActive = true;
    }
  }

  setSessBusy(true);
  try {
    // "Crear" prueba las credenciales si no se ha hecho: no hace falta pulsar antes "Probar".
    if (needsCreds() && !credentialsTested) {
      if (!byId('sess-user').value.trim() || !byId('sess-pass').value) {
        fail(t('credTest.needCreds'));
        byId(byId('sess-user').value.trim() ? 'sess-pass' : 'sess-user').focus();
        return;
      }
      if (!await testCredentials()) { fail(t('new.errorTestCreds')); return; }
    }

    const resolution = getResolution();
    const current = editing && sessions.find(x => x.id === editing);
    const body = {
      name,
      sunshineExePath:    byId('sess-exe').value.trim() || null,
      rdpBackground:      byId('sess-bg').checked,
      // En RDP la pantalla virtual no hace nada y no se ofrece: se conserva lo que hubiera.
      vddEnabled:         local ? byId('sess-vdd').checked : !!(current && current.vddEnabled),
      rdpWidth:           resolution.w,
      rdpHeight:          resolution.h,
      rdpFrameRate:       parseInt(byId('sess-fps').value, 10) || 60,
      rdpColorDepth:      parseInt(byId('sess-bpp').value, 10) || 32,
      rdpDisplayScale:    parseInt(byId('sess-scale').value, 10) || 100,
      rdpComposition:     byId('sess-composition').value || 'Smooth',
      sunshineStreamPort: port,
      useStreamProfile:   !local && byId('sess-profile').checked,
      sunshineName:       byId('sess-sunshine-name').value.trim() || null,
      capture:            byId('sess-capture').value || null,
      encoder:            byId('sess-encoder').value || null,
      outputName:         byId('sess-output').value.trim() || null,
      originWebUiAllowed: byId('sess-origin').value || null,
      sunshineAuthUser:   byId('sess-panel-user').value.trim() || null,
      sunshineAuthPass:   byId('sess-panel-pass').value || null,
      rotateSunshineCredentials: byId('sess-rotate').checked,
      isolated,
      isolatedEphemeral:  isolated && byId('sess-ephemeral').checked,
      ...readProtocolFields(),
    };
    if (editing) body.restartIfActive = restartIfActive;

    let res;
    if (editing) {
      res = await api('PATCH', `/api/sessions/${editing}/config`, body);
    } else {
      Object.assign(body, {
        enabled:  byId('sess-enabled').checked,
        local,
        // La aislada y la local no usan las credenciales del formulario.
        username: needsCreds() ? byId('sess-user').value.trim() : '',
        domain:   needsCreds() ? (byId('sess-domain').value.trim() || '.') : '.',
        password: needsCreds() ? byId('sess-pass').value : '',
        reuseIsolatedUser: isolated ? (byId('sess-reuse').value || null) : null,
      });
      res = await api('POST', '/api/sessions', body);
    }
    if (!res) return;
    if (!res.ok) {
      const msg = await I18n.responseError(res);
      if (/port|puerto|порт/i.test(msg)) byId('sess-advanced').open = true;
      fail(msg || t('toast.connectionError'));
      return;
    }

    // El autoarranque va por su propio endpoint (también lo cambia el interruptor de la tarjeta).
    if (editing && current && byId('sess-enabled').checked !== !!current.enabled)
      await api('PATCH', `/api/sessions/${editing}/enabled`, { enabled: byId('sess-enabled').checked });

    closeSessionModal();
    toast(t(restartIfActive ? 'toast.restarting' : editing ? 'toast.saved' : 'toast.created'));
    loadSessions();
    if (body.reuseIsolatedUser) loadOrphans();
  } finally {
    setSessBusy(false);
  }
}

// ── Modal: log ─────────────────────────────────────────────────────────────
function openLogModal(id, name) {
  logSessionId = id;
  autoScroll   = true;
  document.getElementById('scroll-toggle').checked = true;
  document.getElementById('log-title').textContent = t('log.titleFor', { name });
  document.getElementById('log-box').innerHTML = `<span class="log-empty">${esc(t('log.loading'))}</span>`;
  document.getElementById('log-status').textContent = '';
  logLines = []; logUpdated = null; logPaused = false;
  byId('log-search').value = '';
  byId('log-pause').textContent = t('log.pause');
  byId('log-pause').setAttribute('aria-pressed', 'false');
  byId('log-pause').classList.remove('active');
  show('log-modal');
  stopRefresh();
  fetchLogs();
  showLogLevelNote();
  logTimer = setInterval(whenVisible(fetchLogs), 2500);
}

// Con un nivel distinto de Info el log de la sesión solo tiene parte: se dice, con el atajo a Ajustes.
async function showLogLevelNote() {
  const note = document.getElementById('log-level-note');
  note.classList.add('hidden');
  const res = await api('GET', '/api/settings');
  if (!res || !res.ok) return;
  const { logLevel } = await res.json();
  if (logLevel === 'Info') return;
  note.innerHTML = `${esc(t('log.levelNote', { level: t('settings.log.' + logLevel) }))}
    <button type="button" class="btn btn-ghost btn-xs" onclick="closeLogModal(); openSettingsModal('log')">${esc(t('settings.title'))}</button>`;
  note.classList.remove('hidden');
}

function closeLogModal() {
  clearInterval(logTimer);
  logTimer = null;
  logSessionId = null;
  logLines = [];   // con el modal cerrado no se guarda nada
  hide('log-modal');
  startRefresh();
}

async function fetchLogs() {
  if (!logSessionId || logPaused) return;
  const lines = document.getElementById('log-lines').value;
  const res   = await api('GET', `/api/sessions/${logSessionId}/logs?lines=${lines}`);
  if (!res || !res.ok) return;

  const data = await res.json();
  logLines = data.lines;
  logUpdated = new Date();
  renderLogs();
}

// Líneas "[hh:mm:ss.fff] [NIVEL] mensaje": color por nivel, filtro y búsqueda (en el navegador).
let logLines = [], logUpdated = null, logPaused = false;
const LOG_LEVEL_RX = /^\[[^\]]*\] \[(ERROR|WARNING|INFO|DEBUG)\]/;

function renderLogs() {
  const box = document.getElementById('log-box');
  const status = document.getElementById('log-status');
  const level = byId('log-level').value;
  const q = byId('log-search').value.trim().toLowerCase();
  const shown = logLines.filter(line => {
    const lvl = (LOG_LEVEL_RX.exec(line) || [])[1];
    if (level === 'error' && lvl !== 'ERROR') return false;
    if (level === 'warn' && lvl !== 'ERROR' && lvl !== 'WARNING') return false;
    return !q || line.toLowerCase().includes(q);
  });

  const time = logUpdated ? logUpdated.toLocaleTimeString(I18n.lang.replace('_', '-')) : '';
  status.textContent = (level !== 'all' || q)
    ? t('log.matches', { shown: shown.length, count: logLines.length, time })
    : t('log.status', { count: logLines.length, time });
  if (logPaused) status.textContent += ` · ${t('log.paused')}`;

  if (shown.length === 0) {
    box.innerHTML = `<span class="log-empty">${esc(t(logLines.length ? 'log.noMatches' : 'log.empty'))}</span>`;
    return;
  }

  const mark = text => {
    if (!q) return esc(text);
    let out = '', i = 0, k;
    const lower = text.toLowerCase();
    while ((k = lower.indexOf(q, i)) >= 0) {
      out += esc(text.slice(i, k)) + `<mark>${esc(text.slice(k, k + q.length))}</mark>`;
      i = k + q.length;
    }
    return out + esc(text.slice(i));
  };
  const wasAtBottom = box.scrollHeight - box.scrollTop <= box.clientHeight + 10;
  box.innerHTML = shown.map(line => {
    const lvl = ((LOG_LEVEL_RX.exec(line) || [])[1] || 'none').toLowerCase();
    return `<span class="log-line lvl-${lvl}">${mark(line)}</span>`;
  }).join('\n');
  if (autoScroll && wasAtBottom) box.scrollTop = box.scrollHeight;
}

// Pausa: deja de pedir líneas nuevas para leer con calma (el modal sigue abierto).
function toggleLogPause() {
  logPaused = !logPaused;
  const btn = byId('log-pause');
  btn.textContent = t(logPaused ? 'log.resume' : 'log.pause');
  btn.setAttribute('aria-pressed', String(logPaused));
  btn.classList.toggle('active', logPaused);
  if (logPaused) renderLogs(); else fetchLogs();
}

// ── Puerto derivado (stream + 1 = panel HTTPS interno) ────────────────────
function updateDerivedWebPort() {
  const v = parseInt(byId('sess-port').value, 10);
  byId('sess-port-derived').textContent = Number.isFinite(v) && v > 0
    ? t('new.portDerived', { web: v + 1 })
    : '';
}

// ── Resolución — combobox helpers ─────────────────────────────────────────
const RES_PRESETS = [
  '1280x720', '1366x768', '1600x900', '1920x1080',
  '2560x1080', '2560x1440', '3440x1440', '3840x2160',
];

function onResPreset() {
  byId('sess-res-custom').style.display = byId('sess-res-preset').value === 'custom' ? '' : 'none';
}

function getResolution() {
  const sel = byId('sess-res-preset');
  if (sel.value === 'custom') {
    return {
      w: parseInt(byId('sess-res-w').value, 10) || 1920,
      h: parseInt(byId('sess-res-h').value, 10) || 1080,
    };
  }
  const [w, h] = sel.value.split('x').map(Number);
  return { w, h };
}

function setResPreset(w, h) {
  const key = `${w}x${h}`;
  const sel = byId('sess-res-preset');
  if (RES_PRESETS.includes(key)) {
    sel.value = key;
    byId('sess-res-custom').style.display = 'none';
  } else {
    sel.value = 'custom';
    byId('sess-res-w').value = w;
    byId('sess-res-h').value = h;
    byId('sess-res-custom').style.display = '';
  }
}

// ── Utilidades ─────────────────────────────────────────────────────────────
function esc(str) {
  return String(str ?? '')
    .replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;')
    .replace(/"/g,'&quot;').replace(/'/g,'&#39;');
}

function show(id) {
  document.getElementById(id).classList.remove('hidden');
  // Las ventanas de configuración enseñan arriba una vista previa de lo que se está configurando.
  if (id === 'sess-modal') updateSessionPreview();
  if (id === 'seat-modal' && typeof updateSeatPreview === 'function') updateSeatPreview();
}
function hide(id) { document.getElementById(id).classList.add('hidden'); }

function handleBackdropClick(e, id) {
  if (e.target.id === id) closeModal(id);
}

// Cierra una ventana con su propia función de cierre (que también para sondeos y timers).
function closeModal(id) {
  const close = {
    'sess-modal':     closeSessionModal,
    'log-modal':      closeLogModal,
    'rdp-modal':      closeRdpModal,
    'vigem-modal':    () => closeDriverModal('vigem'),
    'hidhide-modal':  () => closeDriverModal('hidhide'),
    'sunmgr-modal':   closeSunMgrModal,
    'power-modal':    closePowerModal,
    'confirm-modal':  () => resolveConfirm(false),
    'seat-modal':     () => typeof closeSeatModal === 'function' && closeSeatModal(),
    'guide-modal':    () => typeof closeGuide === 'function' && closeGuide(),
  }[id];
  if (close) close(); else hide(id);
}

// ── RDP Wrapper ────────────────────────────────────────────────────────────
let rdpLogTimer = null;
let rdpLastStatus = null;

async function rdpRefreshPillOnly() {
  const res = await api('GET', '/api/rdpwrap/status');
  if (!res || !res.ok) return;
  const st = await res.json();
  rdpLastStatus = st;
  updateRdpPill(st);
}

async function rdpRefresh() {
  await rdpRefreshPillOnly();
  if (!document.getElementById('rdp-modal').classList.contains('hidden')) {
    renderRdpStatus(rdpLastStatus);
    await rdpFetchLog();
  }
}

function updateRdpPill(st) {
  const pill  = document.getElementById('rdp-pill');
  const state = document.getElementById('rdp-pill-state');
  if (!pill || !state) return;

  pill.classList.remove('status-pill-ok','status-pill-missing','status-pill-broken','status-pill-working','status-pill-warn','status-pill-pending');

  const key = rdpStateKey(st);
  const cls = { working: 'status-pill-working', ok: 'status-pill-ok', pending: 'status-pill-pending',
                broken: 'status-pill-broken', missing: 'status-pill-missing' }[key];

  pill.classList.add(cls);
  state.textContent = t(`rdp.state.${key}`);
  pill.title = key === 'pending' ? rdpPendingText(st) : '';
  updateSystemPill();
  renderSessions();
}

function renderRdpStatus(st) {
  if (!st) return;
  const badge = document.getElementById('rdp-status-badge');
  const key   = rdpStateKey(st);
  const cls   = { working: 'badge-starting', ok: 'badge-running', pending: 'badge-pending' }[key] || 'badge-error';
  badge.className = `badge ${cls}`;
  badge.innerHTML = `<span class="badge-dot"></span>${esc(t(`rdp.state.${key}`))}`;
  badge.title = '';
  const note = document.getElementById('rdp-pending-note');
  note.textContent = key === 'pending' ? rdpPendingText(st) : '';
  note.classList.toggle('hidden', key !== 'pending');

  document.getElementById('rdp-install-dir').textContent = st.installDir     || '—';
  document.getElementById('rdp-bundle').textContent      =
    st.wrapper ? `${st.wrapper} ${st.bundleVersion || ''}`.trim() : (st.bundleVersion || '—');
  document.getElementById('rdp-termsrv').textContent     = st.termsrvVersion || '—';
  document.getElementById('rdp-service-dll').textContent = st.serviceDllPath || '—';

  document.getElementById('rdp-btn-install').disabled = !!st.busy;
}

async function rdpFetchLog() {
  const res = await api('GET', '/api/rdpwrap/log');
  if (!res || !res.ok) return;
  const data = await res.json();
  const box  = document.getElementById('rdp-log-box');
  if (!data.lines || data.lines.length === 0) {
    box.innerHTML = `<span class="log-empty">${esc(t('rdp.log.empty'))}</span>`;
    return;
  }
  const wasAtBottom = box.scrollHeight - box.scrollTop <= box.clientHeight + 10;
  box.textContent = data.lines.join('\n');
  if (wasAtBottom) box.scrollTop = box.scrollHeight;
}

function openRdpModal() {
  show('rdp-modal');
  rdpRefresh();
  // Polling mientras el modal esté abierto
  clearInterval(rdpLogTimer);
  rdpLogTimer = setInterval(whenVisible(rdpRefresh), 2000);
}

function closeRdpModal() {
  hide('rdp-modal');
  clearInterval(rdpLogTimer);
  rdpLogTimer = null;
}

async function rdpInstall() {
  if (!await ask(t('rdp.confirm.install'), { ok: t('rdp.btn.install') })) return;
  const res = await api('POST', '/api/rdpwrap/install');
  if (!res) return;
  if (!res.ok) { toast(await I18n.responseError(res), true); return; }
  rdpRefresh();
}

// Estado del multi-RDP. "pending": instalado pero TermService aún no lo ha cargado. Es lo
// normal recién instalado (TermService parado hasta la primera conexión, o con la DLL anterior
// hasta reiniciar Windows): no es un fallo. "broken": ServiceDll apunta a un wrapper cuyos
// ficheros faltan.
function rdpStateKey(st) {
  if (st.busy)      return 'working';
  if (st.installed) return 'ok';
  if (st.pending)   return 'pending';
  if (st.wrapper)   return 'broken';
  return 'missing';
}

function rdpPendingText(st) {
  return t(st.loaded === false ? 'rdp.pendingReboot' : 'rdp.pendingFirstUse');
}

// ── Sunshine management (proxied through OpenStreamMS) ────────────────────
let sunmgrSessionId = null;

/**
 * Intenta parsear el body JSON de una respuesta fallida y devolver el mensaje
 * traducido. Los endpoints del proxy devuelven `{code, message?}` con claves
 * como `invalid_pin`, `credentials_rotated`, `session_not_running`, `proxy_error`.
 */
async function sunmgrTranslateError(res) {
  try {
    const body = await res.json();
    if (body && body.code) {
      const key = `sunmgr.err.${body.code}`;
      const fallback = `sunmgr.pair.${body.code === 'invalid_pin' ? 'failed' : ''}`;
      return t(I18n.has(key) ? key : 'sunmgr.pair.failed', { detail: /^HTTP \d+$/.test(body.message || '') ? body.message : t('error.request') });
    }
  } catch { /* body no-JSON */ }
  return t('toast.connectionError');
}

// Abre el panel web de Sunshine en una pestaña nueva. El alcance lo controla
// origin_web_ui_allowed en la sesión: por defecto "lan" (cualquiera en LAN puede entrar).
// El cert es auto-firmado: el navegador avisará la primera vez.
function openSunshinePanel(id) {
  const s = sessions.find(x => x.id === id);
  if (!s) return;
  if (s.state !== 'Running') { toast(t('btn.panel_disabled_title'), true); return; }
  const url = `https://127.0.0.1:${s.sunshineWebPort}/`;
  window.open(url, '_blank', 'noopener');
}

function openSunMgrModal(id) {
  const s = sessions.find(x => x.id === id);
  if (!s) return;
  sunmgrSessionId = id;
  document.getElementById('sunmgr-title').textContent = t('ux.connect.title', { name: s.name });
  document.getElementById('sunmgr-pair-form').reset();
  document.getElementById('sunmgr-pair-result').style.display = 'none';
  sunmgrSetPendingPairings(null);
  document.getElementById('sunmgr-clients-list').innerHTML = `<span class="sunmgr-empty">${esc(t('sunmgr.loading'))}</span>`;
  show('sunmgr-modal');
  renderConnectInfo(s);
  sunmgrLoadClients();
  document.getElementById('sunmgr-pin').focus();
}

function closeSunMgrModal() {
  hide('sunmgr-modal');
  sunmgrSessionId = null;
}

// Sunshine 2026.9+ dirige el PIN a una solicitud pendiente concreta. Con una sola el
// backend la elige solo; con varias devuelve la lista y aquí se muestra el selector.
function sunmgrSetPendingPairings(pairings) {
  const row = document.getElementById('sunmgr-pairing-row');
  const sel = document.getElementById('sunmgr-pairing');
  if (!pairings || pairings.length < 2) {
    row.style.display = 'none';
    sel.innerHTML = '';
    return;
  }
  sel.innerHTML = pairings.map(p => {
    const label = [p.name || t('sunmgr.pair.unknownDevice'), p.address].filter(Boolean).join(' — ');
    return `<option value="${esc(p.id)}">${esc(label)}</option>`;
  }).join('');
  row.style.display = '';
}

async function sunmgrPair(e) {
  e.preventDefault();
  if (!sunmgrSessionId) return;

  const pin    = document.getElementById('sunmgr-pin').value.trim();
  const name   = document.getElementById('sunmgr-name').value.trim() || 'Moonlight';
  const btn    = document.getElementById('sunmgr-pair-submit');
  const result = document.getElementById('sunmgr-pair-result');
  const pairingRow = document.getElementById('sunmgr-pairing-row');
  const pairingId  = pairingRow.style.display === 'none'
    ? null : document.getElementById('sunmgr-pairing').value || null;

  btn.disabled = true;
  result.style.display = 'none';
  result.style.borderColor = '';   // el aviso ambar de un intento previo no debe quedarse

  try {
    const res = await api('POST', `/api/sessions/${sunmgrSessionId}/pair`, { pin, name, pairingId });
    if (!res) return;
    if (res.ok) {
      result.className = 'auth-alert success';
      result.textContent = t('sunmgr.pair.success');
      result.style.display = '';
      document.getElementById('sunmgr-pin').value = '';
      sunmgrSetPendingPairings(null);
      await sunmgrLoadClients();
      return;
    }

    // Error: parsear body {code, message?} para traducir
    let body = null;
    try { body = await res.json(); } catch {}
    const code = body?.code;

    if (code === 'credentials_rotated') {
      result.className = 'auth-alert';
      result.style.borderColor = 'rgba(245,158,11,.5)';
      result.textContent = t('sunmgr.err.credentials_rotated');
    } else if (code === 'credentials_mismatch') {
      result.className = 'auth-alert error';
      result.textContent = t('sunmgr.err.credentials_mismatch');
    } else if (code === 'session_not_running') {
      result.className = 'auth-alert error';
      result.textContent = t('sunmgr.err.session_not_running');
    } else if (code === 'no_pending_pairing') {
      sunmgrSetPendingPairings(null);
      result.className = 'auth-alert error';
      result.textContent = t('sunmgr.err.no_pending_pairing');
    } else if (code === 'multiple_pending') {
      sunmgrSetPendingPairings(body.pairings);
      result.className = 'auth-alert';
      result.style.borderColor = 'rgba(245,158,11,.5)';
      result.textContent = t('sunmgr.err.multiple_pending');
    } else if (code === 'proxy_error') {
      result.className = 'auth-alert error';
      result.textContent = t('sunmgr.err.proxy_error', { detail: /^HTTP \d+$/.test(body?.message || '') ? body.message : t('error.request') });
    } else {
      // invalid_pin u otro → mensaje de PIN incorrecto
      result.className = 'auth-alert error';
      result.textContent = t('sunmgr.pair.failed');
    }
    result.style.display = '';
  } finally {
    btn.disabled = false;
  }
}

async function sunmgrLoadClients() {
  if (!sunmgrSessionId) return;
  const list = document.getElementById('sunmgr-clients-list');
  const res  = await api('GET', `/api/sessions/${sunmgrSessionId}/clients`);
  if (!res) return;
  if (!res.ok) {
    const msg = await sunmgrTranslateError(res);
    list.innerHTML = `<span class="sunmgr-empty">${esc(msg)}</span>`;
    document.getElementById('sunmgr-unpair-all-btn').disabled = true;
    return;
  }
  const clients = await res.json();
  if (!Array.isArray(clients) || clients.length === 0) {
    list.innerHTML = `<span class="sunmgr-empty">${esc(t('sunmgr.clients.empty'))}</span>`;
    document.getElementById('sunmgr-unpair-all-btn').disabled = true;
    return;
  }
  document.getElementById('sunmgr-unpair-all-btn').disabled = false;
  list.innerHTML = clients.map(c => `
    <div class="sunmgr-client">
      <div>
        <div class="sunmgr-client-name">${esc(c.name)}</div>
        <div class="sunmgr-client-uuid">${esc(c.uuid)}</div>
      </div>
      <button class="btn btn-ghost btn-sm" onclick="sunmgrUnpair('${esc(c.uuid)}','${esc(c.name)}')">${esc(t('sunmgr.clients.unpair'))}</button>
    </div>
  `).join('');
}

async function sunmgrUnpair(uuid, name) {
  if (!sunmgrSessionId) return;
  if (!await ask(t('sunmgr.confirm.unpair', { name }), { danger: true, ok: t('sunmgr.clients.unpair') })) return;
  const res = await api('DELETE', `/api/sessions/${sunmgrSessionId}/clients/${encodeURIComponent(uuid)}`);
  if (!res) return;
  if (!res.ok && res.status !== 204) { toast(await sunmgrTranslateError(res), true); return; }
  await sunmgrLoadClients();
}

async function sunmgrUnpairAll() {
  if (!sunmgrSessionId) return;
  if (!await ask(t('sunmgr.confirm.unpairAll'), { danger: true, ok: t('sunmgr.clients.unpairAll') })) return;
  const res = await api('DELETE', `/api/sessions/${sunmgrSessionId}/clients`);
  if (!res) return;
  if (!res.ok && res.status !== 204) { toast(await sunmgrTranslateError(res), true); return; }
  await sunmgrLoadClients();
}

// ── Drivers (ViGEmBus, HidHide) ────────────────────────────────────────────
// Misma UI para los dos: pastilla en la barra + modal. Ids de los elementos
// con prefijo <clave>- y textos i18n con prefijo <clave>.
const drivers = {
  vigem:   { api: '/api/vigembus', lastStatus: null, logTimer: null, logLines: [] },
  hidhide: { api: '/api/hidhide',  lastStatus: null, logTimer: null, logLines: [] },
};
const DRIVER_KEYS = Object.keys(drivers);

async function driverRefreshPillOnly(k) {
  const res = await api('GET', `${drivers[k].api}/status`);
  if (!res || !res.ok) return;
  const st = await res.json();
  drivers[k].lastStatus = st;
  updateDriverPill(k, st);
}

async function driverRefresh(k) {
  await driverRefreshPillOnly(k);
  if (!document.getElementById(`${k}-modal`).classList.contains('hidden')) {
    renderDriverStatus(k, drivers[k].lastStatus);
    await driverFetchLog(k);
  }
}

function updateDriverPill(k, st) {
  const pill  = document.getElementById(`${k}-pill`);
  const state = document.getElementById(`${k}-pill-state`);
  if (!pill || !state) return;

  pill.classList.remove('status-pill-ok','status-pill-missing','status-pill-broken','status-pill-working','status-pill-warn');

  let cls, key;
  if (st.busy)                  { cls = 'status-pill-working'; key = 'rdp.state.working'; }
  else if (st.rebootRequired)   { cls = 'status-pill-warn';    key = 'rdp.state.working'; }
  else if (st.installed)        { cls = 'status-pill-ok';      key = 'rdp.state.ok';      }
  else                          { cls = 'status-pill-missing'; key = 'rdp.state.missing'; }

  pill.classList.add(cls);
  state.textContent = st.rebootRequired ? '⟳' : t(key);
  updateSystemPill();
  renderSessions();
}

function renderDriverStatus(k, st) {
  if (!st) return;
  const badge = document.getElementById(`${k}-status-badge`);
  const cls   = st.busy ? 'badge-starting'
              : st.installed ? 'badge-running'
              : 'badge-error';
  const key   = st.busy ? 'rdp.state.working'
              : st.installed ? 'rdp.state.ok'
              : 'rdp.state.missing';
  badge.className = `badge ${cls}`;
  badge.innerHTML = `<span class="badge-dot"></span>${esc(t(key))}`;

  document.getElementById(`${k}-version`).textContent = st.installedVersion || '—';
  document.getElementById(`${k}-service`).textContent = I18n.driverServiceState(st.serviceState);
  document.getElementById(`${k}-driver`).textContent  = st.driverImagePath  || '—';

  document.getElementById(`${k}-reboot-banner`).style.display = st.rebootRequired ? '' : 'none';

  const busy = !!st.busy;
  document.getElementById(`${k}-btn-install`).disabled   = busy;
  document.getElementById(`${k}-btn-uninstall`).disabled = busy || !st.installed;
}

async function driverFetchLog(k) {
  const res = await api('GET', `${drivers[k].api}/log`);
  if (!res || !res.ok) return;
  const data = await res.json();
  drivers[k].logLines = data.lines || [];
  renderDriverLog(k);
}

function renderDriverLog(k) {
  const box  = document.getElementById(`${k}-log-box`);
  const lines = drivers[k].logLines;
  if (lines.length === 0) {
    box.innerHTML = `<span class="log-empty">${esc(t('vigem.log.empty'))}</span>`;
    return;
  }
  const wasAtBottom = box.scrollHeight - box.scrollTop <= box.clientHeight + 10;
  box.textContent = lines.map(line => I18n.driverLog(line)).join('\n');
  if (wasAtBottom) box.scrollTop = box.scrollHeight;
}

function openDriverModal(k) {
  show(`${k}-modal`);
  driverRefresh(k);
  clearInterval(drivers[k].logTimer);
  drivers[k].logTimer = setInterval(whenVisible(() => driverRefresh(k)), 2000);
}

function closeDriverModal(k) {
  hide(`${k}-modal`);
  clearInterval(drivers[k].logTimer);
  drivers[k].logTimer = null;
}

async function driverInstall(k) {
  if (!await ask(t(`${k}.confirm.install`), { ok: t('vigem.btn.install') })) return;
  const res = await api('POST', `${drivers[k].api}/install`);
  if (!res) return;
  if (!res.ok) { toast(await I18n.responseError(res), true); return; }
  driverRefresh(k);
}

async function driverUninstall(k) {
  if (!await ask(t(`${k}.confirm.uninstall`), { danger: true, ok: t('vigem.btn.uninstall') })) return;
  const res = await api('POST', `${drivers[k].api}/uninstall`);
  if (!res) return;
  if (!res.ok) { toast(await I18n.responseError(res), true); return; }
  driverRefresh(k);
}


// ── Power (apagar/reiniciar) ──────────────────────────────────────────────
let powerLastAction = null;

function openPowerModal() {
  show('power-modal');
  renderPowerPending();
}

function closePowerModal() {
  hide('power-modal');
}

function renderPowerPending() {
  const box  = document.getElementById('power-pending');
  const text = document.getElementById('power-pending-text');
  if (!box || !text) return;
  if (!powerLastAction) { box.style.display = 'none'; return; }
  box.style.display = '';
  text.textContent = powerLastAction;
}

async function powerAction(kind) {
  let path, confirmKey;
  if (kind === 'shutdown')      { path = '/api/power/shutdown'; confirmKey = 'power.confirm.shutdown'; }
  else if (kind === 'restart')  { path = '/api/power/restart';  confirmKey = 'power.confirm.restart';  }
  else if (kind === 'cancel')   { path = '/api/power/cancel';   confirmKey = null; }
  else return;

  const delayInput = document.getElementById('power-delay');
  const delay = Math.max(0, Math.min(3600, parseInt(delayInput?.value ?? '10', 10) || 0));

  if (confirmKey && !await ask(t(confirmKey, { delay }), {
        danger: kind === 'shutdown', ok: t(kind === 'shutdown' ? 'power.btn.shutdown' : 'power.btn.restart') })) return;

  const body = (kind === 'cancel') ? undefined : { delaySeconds: delay };
  const res  = await api('POST', path, body);
  if (!res) return;
  if (!res.ok) { toast(await I18n.responseError(res), true); return; }

  const data = await res.json().catch(() => null);
  if (kind === 'cancel') {
    powerLastAction = t('power.toast.cancel');
    toast(powerLastAction);
  } else {
    const label = t(kind === 'shutdown' ? 'power.toast.shutdown' : 'power.toast.restart',
                    { delay: data?.delaySeconds ?? delay });
    powerLastAction = label;
    toast(label);
  }
  renderPowerPending();
}

// ═══════════════════════════════════════════════════════════════════════════
//  PIEZAS DE INTERFAZ: iconos, repintado sin parpadeo, menús y estado del sistema
// ═══════════════════════════════════════════════════════════════════════════

// Iconos de trazo (heredan el color del texto).
const UI_ICONS = {
  power:    '<path d="M12 2v10"/><path d="M18.4 6.6a9 9 0 1 1-12.8 0"/>',
  grid:     '<rect x="3" y="3" width="8" height="8" rx="1.5"/><rect x="13" y="3" width="8" height="8" rx="1.5"/><rect x="3" y="13" width="8" height="8" rx="1.5"/><rect x="13" y="13" width="8" height="8" rx="1.5"/>',
  copy:     '<rect x="9" y="9" width="12" height="12" rx="2"/><path d="M5 15V5a2 2 0 0 1 2-2h10"/>',
  download: '<path d="M12 3v12"/><path d="m7 10 5 5 5-5"/><path d="M5 21h14"/>',
  help:     '<circle cx="12" cy="12" r="9"/><path d="M9.5 9.5a2.5 2.5 0 1 1 3.5 2.3c-.6.3-1 .9-1 1.6v.6"/><circle cx="12" cy="17.2" r=".6" fill="currentColor"/>',
  more:     '<circle cx="5" cy="12" r="1.6" fill="currentColor"/><circle cx="12" cy="12" r="1.6" fill="currentColor"/><circle cx="19" cy="12" r="1.6" fill="currentColor"/>',
  restart:  '<path d="M3 12a9 9 0 1 0 3-6.7L3 8"/><path d="M3 3v5h5"/>',
  moon:     '<path d="M21 12.8A9 9 0 1 1 11.2 3a7 7 0 0 0 9.8 9.8z"/>',
  web:      '<circle cx="12" cy="12" r="9"/><path d="M3 12h18M12 3a14 14 0 0 1 0 18M12 3a14 14 0 0 0 0 18"/>',
  external: '<path d="M14 3h7v7"/><path d="M10 14 21 3"/><path d="M21 14v5a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h5"/>',
  log:      '<path d="M4 6h16M4 12h16M4 18h10"/>',
  gear:     '<circle cx="12" cy="12" r="3"/><path d="M19.4 15a1.7 1.7 0 0 0 .3 1.8l.1.1a2 2 0 1 1-2.8 2.8l-.1-.1a1.7 1.7 0 0 0-1.8-.3 1.7 1.7 0 0 0-1 1.5V21a2 2 0 1 1-4 0v-.1a1.7 1.7 0 0 0-1.1-1.5 1.7 1.7 0 0 0-1.8.3l-.1.1a2 2 0 1 1-2.8-2.8l.1-.1a1.7 1.7 0 0 0 .3-1.8 1.7 1.7 0 0 0-1.5-1H3a2 2 0 1 1 0-4h.1a1.7 1.7 0 0 0 1.5-1.1 1.7 1.7 0 0 0-.3-1.8l-.1-.1a2 2 0 1 1 2.8-2.8l.1.1a1.7 1.7 0 0 0 1.8.3H9a1.7 1.7 0 0 0 1-1.5V3a2 2 0 1 1 4 0v.1a1.7 1.7 0 0 0 1 1.5 1.7 1.7 0 0 0 1.8-.3l.1-.1a2 2 0 1 1 2.8 2.8l-.1.1a1.7 1.7 0 0 0-.3 1.8V9a1.7 1.7 0 0 0 1.5 1H21a2 2 0 1 1 0 4h-.1a1.7 1.7 0 0 0-1.5 1z"/>',
  trash:    '<path d="M3 6h18M8 6V4h8v2M6 6l1 14h10l1-14"/>',
  screen:   '<rect x="2" y="3" width="20" height="13" rx="2"/><path d="M8 21h8M12 16v5"/>',
  users:    '<circle cx="9" cy="8" r="3.5"/><path d="M2.5 20a6.5 6.5 0 0 1 13 0"/><path d="M16 4.5a3.5 3.5 0 0 1 0 7M18 14a6 6 0 0 1 3.5 6"/>',
  link:     '<path d="M10 13a5 5 0 0 0 7.5.5l3-3a5 5 0 0 0-7-7l-1.7 1.7"/><path d="M14 11a5 5 0 0 0-7.5-.5l-3 3a5 5 0 0 0 7 7l1.7-1.7"/>',
  pc:       '<rect x="2" y="3" width="20" height="13" rx="2"/><path d="M8 21h8M12 16v5"/><path d="M7 8h4"/>',
  seat:     '<rect x="3" y="4" width="18" height="11" rx="2"/><path d="M7 19h10M9 15v4M15 15v4"/>',
};

function uiIcon(name) {
  return `<svg class="ui-icon" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"
              stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">${UI_ICONS[name] || ''}</svg>`;
}

// Repinta un contenedor solo si su HTML cambia: con el refresco de 3 s, rehacerlo siempre
// reiniciaría las animaciones y el hover. Lo que aparece nuevo (por id) entra animado.
function renderInto(el, html) {
  if (el.dataset.html === html) return;
  const before = new Set([...el.children].map(c => c.id).filter(Boolean));
  const first = el.dataset.html === undefined;
  el.dataset.html = html;
  el.innerHTML = html;
  [...el.children].forEach((c, i) => {
    if (c.id && !before.has(c.id)) {
      c.style.setProperty('--enter-delay', `${first ? i * 60 : 0}ms`);
      c.classList.add('card-enter');
      c.addEventListener('animationend', () => c.classList.remove('card-enter'), { once: true });
    }
  });
}

// ── Menú flotante ("⋯" de las tarjetas) ───────────────────────────────────
// Vive fuera de las tarjetas: el refresco de las tarjetas no lo cierra.
let menuEl = null;

function openMenu(e, items) {
  e.stopPropagation();
  closeMenu();
  menuEl = document.createElement('div');
  menuEl.className = 'float-menu';
  menuEl.setAttribute('role', 'menu');
  items.forEach(it => {
    if (it.sep) { menuEl.insertAdjacentHTML('beforeend', '<div class="float-menu-sep"></div>'); return; }
    const b = document.createElement('button');
    b.type = 'button';
    b.className = 'float-menu-item' + (it.danger ? ' danger' : '');
    b.disabled = !!it.disabled;
    if (it.disabled && it.hint) b.title = it.hint;
    // Algunas traducciones traen su propio símbolo delante ("↻ Reiniciar"): aquí ya va el icono.
    b.innerHTML = `${uiIcon(it.icon)}<span>${esc(it.label.replace(/^[^\p{L}\p{N}]+/u, ''))}</span>`;
    b.onclick = () => { closeMenu(); it.run(); };
    menuEl.appendChild(b);
  });
  document.body.appendChild(menuEl);
  // Bajo el botón, alineado a su derecha; si no cabe abajo, encima.
  const r = e.currentTarget.getBoundingClientRect(), m = menuEl.getBoundingClientRect();
  const top = r.bottom + 6 + m.height > innerHeight ? r.top - m.height - 6 : r.bottom + 6;
  menuEl.style.top  = `${Math.max(8, top + scrollY)}px`;
  menuEl.style.left = `${Math.max(8, Math.min(innerWidth - m.width - 8, r.right - m.width)) + scrollX}px`;
}

function closeMenu() { if (menuEl) { menuEl.remove(); menuEl = null; } }

// ── Desplegables de la barra superior ─────────────────────────────────────
function togglePop(id, e) {
  e.stopPropagation();
  const pop = document.getElementById(id);
  const open = pop.classList.contains('hidden');
  document.querySelectorAll('.nav-pop').forEach(p => p.classList.add('hidden'));
  closeMenu();
  if (open) pop.classList.remove('hidden');
}

document.addEventListener('click', e => {
  if (!e.target.closest('.nav-pop')) document.querySelectorAll('.nav-pop').forEach(p => p.classList.add('hidden'));
  if (menuEl && !menuEl.contains(e.target)) closeMenu();
});
document.addEventListener('keydown', e => {
  if (e.key === 'Escape') { closeMenu(); document.querySelectorAll('.nav-pop').forEach(p => p.classList.add('hidden')); }
});
// Solo si cambia el ancho: en el móvil, el alto cambia al hacer scroll (barra del navegador).
let menuWidth = innerWidth;
window.addEventListener('resize', () => { if (innerWidth !== menuWidth) { menuWidth = innerWidth; closeMenu(); } });

// Una píldora para las tres dependencias: verde si todas están bien; si no, cuántas faltan.
function updateSystemPill() {
  const pills = ['rdp-pill', 'vigem-pill', 'hidhide-pill'].map(id => document.getElementById(id)).filter(Boolean);
  const sys = document.getElementById('sys-pill'), state = document.getElementById('sys-pill-state');
  if (!sys || !pills.length) return;
  const known = pills.filter(p => [...p.classList].some(c => c.startsWith('status-pill-') && c !== 'status-pill'));
  const working = pills.some(p => p.classList.contains('status-pill-working'));
  const bad = pills.filter(p => !['status-pill-ok', 'status-pill-working', 'status-pill-pending']
                                   .some(c => p.classList.contains(c))).length;
  sys.classList.remove('status-pill-ok', 'status-pill-missing', 'status-pill-working', 'status-pill-warn');
  if (known.length < pills.length) { state.textContent = '…'; return; }
  if (working)  { sys.classList.add('status-pill-working'); state.textContent = t('rdp.state.working'); }
  else if (bad) { sys.classList.add('status-pill-warn');    state.textContent = t('nav.systemIssues', { n: bad }); }
  else          { sys.classList.add('status-pill-ok');      state.textContent = t('nav.systemOk'); }
}

// ── Pestañas: indicador que se desliza bajo la activa ─────────────────────
function moveTabIndicator() {
  const tab = document.querySelector('.tabs .tab.active'), ind = document.getElementById('tab-indicator');
  if (!tab || !ind) return;
  ind.style.width = `${tab.offsetWidth}px`;
  ind.style.transform = `translateX(${tab.offsetLeft}px)`;
}
window.addEventListener('resize', moveTabIndicator);
window.addEventListener('langchange', () => { moveTabIndicator(); updateSystemPill(); });
document.addEventListener('DOMContentLoaded', async () => { if (await I18n.ready) requestAnimationFrame(moveTabIndicator); });

// ── Vista previa en vivo de las ventanas de configuración ─────────────────
function setPreview(box, html, popKey) {
  if (box.dataset.html === html) return;
  const pop = box.dataset.pop !== undefined && box.dataset.pop !== popKey;
  box.dataset.html = html;
  box.dataset.pop = popKey;
  box.innerHTML = html;
  // La pantalla "rebota" cuando cambia lo que muestra (resolución, zona).
  if (pop) box.querySelector('.preview-screen')?.classList.add('preview-pop');
}

function updateSessionPreview() {
  const box = byId('sess-preview');
  if (!box) return;
  const local = isLocalKind();
  const name = byId('sess-name').value.trim();
  const fps = byId('sess-fps').value || 60;
  const { w, h } = getResolution();
  const proto = getProtocol();
  const port = byId('sess-port').value;
  const webPort = byId('sess-webrtc-port').value;
  const isolated = byId('sess-isolated').checked;
  // La miniatura toma la forma de la resolución (ultrapanorámicas más anchas).
  const ratio = w > 0 && h > 0 ? w / h : 16 / 9;
  const pw = Math.round(Math.min(170, Math.max(110, 82 * ratio)));
  const screen = local
    ? `<div class="preview-screen">${uiIcon('pc')}<span class="preview-fps">${esc(t('card.localShort'))}</span></div>`
    : `<div class="preview-screen" style="width:${pw}px"><span class="preview-res">${w}×${h}</span><span class="preview-fps">${esc(fps)} ${esc(t('unit.fps'))}</span></div>`;
  const chips = [];
  if (proto !== 'WebRtc')    chips.push(`<span class="chip chip-moonlight">${uiIcon('moon')}Moonlight <b>${esc(port || '')}</b></span>`);
  if (proto !== 'Moonlight') chips.push(`<span class="chip chip-webrtc">${uiIcon('web')}WebRTC <b>${esc(webPort || '')}</b></span>`);
  const reuse = !local && isolated && !sessEditingId ? byId('sess-reuse').value : '';
  if (!local && isolated)    chips.push(`<span class="chip">${uiIcon('seat')}${esc(reuse || t('card.isolatedUser'))}</span>`);
  setPreview(box, `${screen}
    <div class="preview-body">
      <div class="preview-name${name ? '' : ' placeholder'}">${esc(name || t('new.namePlaceholder'))}</div>
      <div class="preview-chips">${chips.join('')}</div>
    </div>`, local ? 'local' : `${w}x${h}`);
}

document.addEventListener('DOMContentLoaded', async () => {
  if (!await I18n.ready) return;
  const form = byId('sess-form');
  form?.addEventListener('input', updateSessionPreview);
  form?.addEventListener('change', updateSessionPreview);
});

// ═══════════════════════════════════════════════════════════════════════════
//  DIÁLOGOS, AVISOS Y AJUSTES
// ═══════════════════════════════════════════════════════════════════════════

// ── Confirmación propia (en vez de confirm() del navegador) ───────────────
// Explica qué va a pasar y, si es destructivo, el botón va en rojo y el foco en "Cancelar".
let confirmResolver = null;

function ask(message, { title, ok, danger = false, check = null } = {}) {
  return new Promise(resolve => {
    if (confirmResolver) confirmResolver(false);
    confirmResolver = resolve;
    byId('confirm-title').textContent = title || t(danger ? 'ux.confirm.dangerTitle' : 'ux.confirm.title');
    byId('confirm-msg').textContent   = message;
    byId('confirm-check-row').classList.toggle('hidden', !check);
    if (check) {
      byId('confirm-check-label').textContent = check.label;
      byId('confirm-check').checked = !!check.checked;
    }
    const okBtn = byId('confirm-ok');
    okBtn.textContent = plainLabel(ok || t('ux.confirm.ok'));
    okBtn.className   = `btn ${danger ? 'btn-danger' : 'btn-primary'}`;
    okBtn.onclick     = () => resolveConfirm(true);
    byId('confirm-cancel').onclick = () => resolveConfirm(false);
    show('confirm-modal');
    (danger ? byId('confirm-cancel') : okBtn).focus();
  });
}

function resolveConfirm(value) {
  hide('confirm-modal');
  const resolve = confirmResolver;
  confirmResolver = null;
  if (resolve) resolve(value);
}

// Confirmación con una casilla: null si se cancela; si no, { checked }.
async function askCheck(message, check, opts = {}) {
  const ok = await ask(message, { ...opts, check });
  return ok ? { checked: byId('confirm-check').checked } : null;
}

// Algunas traducciones llevan un símbolo delante ("↻ Reiniciar"): en los botones del diálogo, fuera.
function plainLabel(label) { return String(label).replace(/^[^\p{L}\p{N}]+/u, ''); }

// ── Avisos: los de éxito se van solos; los de error se quedan hasta cerrarlos ──
function toast(msg, isError = false) {
  const el = byId('toast');
  el.innerHTML = `<span class="toast-msg"></span>${isError
    ? `<button type="button" class="toast-close" aria-label="${esc(t('btn.close'))}" onclick="hide('toast')">×</button>` : ''}`;
  el.querySelector('.toast-msg').textContent = msg;
  el.className = isError ? 'error' : '';
  el.setAttribute('role', isError ? 'alert' : 'status');
  clearTimeout(toastTimer);
  if (!isError) toastTimer = setTimeout(() => { el.classList.add('hidden'); }, 3500);
}

// ── Ajustes globales: nivel de log ────────────────────────────────────────
// Miniaturas fijas de cada tema: se ven con sus propios colores aunque el panel use otro.
const THEME_PREVIEWS = {
  system:    null,
  dark:      { bg: '#0d0d1a', card: '#1a1a2e', accent: '#7c3aed', text: '#e2e8f0', line: '#2d2d50' },
  light:     { bg: '#f4f5fa', card: '#ffffff', accent: '#6d28d9', text: '#1c2033', line: '#d9deea' },
  synthwave: { bg: '#12062b', card: '#211046', accent: '#ff2fb3', text: '#fbeaff', line: '#2de2e6' },
  retro:     { bg: '#0e0a04', card: '#1b1308', accent: '#ffb000', text: '#ffcf70', line: '#4d3812' },
  matrix:    { bg: '#000000', card: '#03100a', accent: '#00e05a', text: '#b8ffd0', line: '#0f4024' },
  nord:      { bg: '#242933', card: '#3b4252', accent: '#88c0d0', text: '#eceff4', line: '#4c566a' },
  dracula:   { bg: '#21222c', card: '#2d2f3d', accent: '#bd93f9', text: '#f8f8f2', line: '#44475a' },
  solarized: { bg: '#fdf6e3', card: '#fffbee', accent: '#2176b5', text: '#073642', line: '#e4dcc4' },
  oled:      { bg: '#000000', card: '#0a0a0a', accent: '#8b5cf6', text: '#f5f5f5', line: '#262626' },
  contrast:  { bg: '#000000', card: '#000000', accent: '#ffff00', text: '#ffffff', line: '#ffffff' },
};
const DEFAULT_WINDOWS_ACCENT = '#0078d4';   // el azul de Windows, mientras no se sepa el del usuario

function themeSwatch(p) {
  return `<span class="theme-swatch" style="background:${p.bg}">
      <span class="ts-card" style="background:${p.card};border-color:${p.line}">
        <span class="ts-line" style="background:${p.text}"></span>
        <span class="ts-line short" style="background:${p.text}"></span>
        <span class="ts-btn" style="background:${p.accent}"></span>
      </span></span>`;
}

function renderAppearance() {
  const picker = byId('theme-picker');
  const winAccent = Theme.windows.accent || DEFAULT_WINDOWS_ACCENT;
  picker.innerHTML = Theme.THEMES.map(name => {
    // "Según el sistema": mitad oscuro, mitad claro. "Windows": igual, con el acento de Windows.
    const swatch = name === 'system'
      ? `<span class="theme-swatch split">${themeSwatch(THEME_PREVIEWS.dark)}${themeSwatch(THEME_PREVIEWS.light)}</span>`
      : name === 'windows'
      ? `<span class="theme-swatch split">${themeSwatch({ ...THEME_PREVIEWS.dark, accent: winAccent })}${themeSwatch({ ...THEME_PREVIEWS.light, accent: winAccent })}</span>`
      : themeSwatch(THEME_PREVIEWS[name]);
    return `<label class="theme-option">
        <input type="radio" name="Theme" value="${name}" ${Theme.theme === name ? 'checked' : ''}
               onchange="Theme.setTheme(this.value); updateEffectsHint(); updateWindowsThemeHint()">
        ${swatch}<span class="theme-name">${esc(t('appearance.theme.' + name))}</span>
      </label>`;
  }).join('');
  const fx = document.querySelector(`#effects-picker input[value="${Theme.effects}"]`);
  if (fx) fx.checked = true;
  updateEffectsHint();
  updateWindowsThemeHint();
}

// Tema "Windows": de dónde sale el color y qué hacer si no se puede leer.
function updateWindowsThemeHint() {
  const hint = byId('windows-theme-hint');
  if (!hint) return;
  const on = Theme.theme === 'windows';
  hint.classList.toggle('hidden', !on);
  if (on) hint.textContent = t(Theme.windows.accent ? 'appearance.windowsHint' : 'appearance.windowsUnknown');
}
document.addEventListener('windowsappearance', () => {
  if (!byId('settings-modal').classList.contains('hidden')) renderAppearance();
});

// Secciones de la ventana de ajustes (pestañas a la izquierda; arriba en el móvil).
let settingsActivePane = 'appearance';
function settingsPane(name, focus) {
  settingsActivePane = name;
  document.querySelectorAll('#settings-modal .settings-tab').forEach(tab => {
    const on = tab.dataset.pane === name;
    tab.setAttribute('aria-selected', on ? 'true' : 'false');
    tab.tabIndex = on ? 0 : -1;
    if (on && focus) tab.focus();
  });
  document.querySelectorAll('#settings-modal .settings-pane').forEach(pane => {
    pane.hidden = pane.id !== 'settings-pane-' + name;
  });
  // Solo el nivel de log se guarda con el botón; el tema y los efectos se aplican al momento.
  byId('settings-save').classList.toggle('hidden', name !== 'log');
  byId('settings-cancel').textContent = t(name === 'log' ? 'btn.cancel' : 'btn.close');
}
// Flechas para moverse entre secciones, como en cualquier lista de pestañas.
document.addEventListener('keydown', e => {
  const tab = e.target.closest?.('#settings-modal .settings-tab');
  if (!tab || !['ArrowUp', 'ArrowDown', 'ArrowLeft', 'ArrowRight', 'Home', 'End'].includes(e.key)) return;
  const tabs = [...document.querySelectorAll('#settings-modal .settings-tab')];
  let i = tabs.indexOf(tab);
  if (e.key === 'Home') i = 0;
  else if (e.key === 'End') i = tabs.length - 1;
  else i = (i + (e.key === 'ArrowUp' || e.key === 'ArrowLeft' ? -1 : 1) + tabs.length) % tabs.length;
  e.preventDefault();
  settingsPane(tabs[i].dataset.pane, true);
});

// En automático, se dice qué está aplicando ahora y por qué.
function updateEffectsHint() {
  const hint = byId('effects-hint');
  if (!hint) return;
  const key = Theme.effects !== 'auto' ? `appearance.fx.${Theme.effects}Hint`
            : Theme.resolvedEffects === 'reduced' ? 'appearance.fx.autoReduced' : 'appearance.fx.autoFull';
  hint.textContent = t(key);
}
document.addEventListener('change', e => { if (e.target.name === 'Effects') updateEffectsHint(); });

async function openSettingsModal(pane) {
  document.querySelectorAll('.nav-pop').forEach(p => p.classList.add('hidden'));
  settingsPane(typeof pane === 'string' ? pane : settingsActivePane);
  renderAppearance();
  Theme.refreshWindows(true);
  const res = await api('GET', '/api/settings');
  if (!res) return;
  if (!res.ok) { toast(t('toast.connectionError'), true); return; }
  const st = await res.json();
  const radio = document.querySelector(`#settings-log-level input[value="${st.logLevel}"]`);
  if (radio) radio.checked = true;
  show('settings-modal');
  document.querySelector('#settings-modal .settings-tab[aria-selected="true"]').focus();
}

async function saveSettings() {
  const sel = document.querySelector('#settings-log-level input:checked');
  if (!sel) return;
  if (sel.value === 'Off' &&
      !await ask(t('settings.confirmOff'), { danger: true, ok: t('settings.log.Off') })) return;
  const btn = byId('settings-save');
  btn.disabled = true;
  try {
    const res = await api('PUT', '/api/settings/logging', { logLevel: sel.value });
    if (!res) return;
    if (!res.ok) { toast(await I18n.responseError(res) || t('toast.connectionError'), true); return; }
    hide('settings-modal');
    toast(t('settings.saved', { level: t('settings.log.' + sel.value) }));
  } finally {
    btn.disabled = false;
  }
}

// ── Paquete de diagnóstico: .zip con datos del equipo, logs y eventos de Windows ──
async function downloadDiagnostics() {
  const btn = byId('diag-download');
  if (btn.disabled) return;
  btn.disabled = true;
  btn.textContent = t('settings.diagWorking');
  try {
    await saveDiagnostics(byId('diag-days').value);
  } finally {
    btn.disabled = false;
    btn.textContent = t('settings.diagDownload');
  }
}

// Genera el .zip de diagnóstico y lo descarga. También lo usan las tarjetas con error.
async function saveDiagnostics(days) {
  const res = await api('GET', `/api/diagnostics/bundle?days=${encodeURIComponent(days)}`);
  if (!res) return;
  if (!res.ok) { toast(await I18n.responseError(res) || t('settings.diagFailed'), true); return; }
  const blob = await res.blob();
  const disposition = res.headers.get('Content-Disposition') || '';
  const match = /filename\*=UTF-8''([^;]+)/i.exec(disposition) || /filename="?([^";]+)"?/i.exec(disposition);
  const url = URL.createObjectURL(blob);
  const a = document.createElement('a');
  a.href = url;
  a.download = match ? decodeURIComponent(match[1]) : 'openstreamms-diag.zip';
  document.body.appendChild(a);
  a.click();
  a.remove();
  setTimeout(() => URL.revokeObjectURL(url), 10000);
  toast(t('settings.diagDone'));
}

// ── Conectar un dispositivo: dirección del PC y puertos de la sesión ──────
let networkInfo = null;

async function loadNetworkInfo() {
  if (networkInfo) return networkInfo;
  const res = await api('GET', '/api/system/network');
  if (res && res.ok) networkInfo = await res.json();
  return networkInfo;
}

async function renderConnectInfo(s) {
  const box = byId('connect-info');
  box.innerHTML = '';
  const info = await loadNetworkInfo();
  if (sunmgrSessionId !== s.id) return;   // se cerró o se abrió otra mientras tanto
  const addresses = info && info.addresses && info.addresses.length ? info.addresses : [location.hostname];
  const ip = addresses[0];
  const row = (icon, title, how, addr) => `
    <div class="connect-row">
      <span class="connect-icon">${uiIcon(icon)}</span>
      <div class="connect-text"><strong>${esc(title)}</strong><div class="hint">${esc(how)}</div></div>
      <button type="button" class="connect-addr" onclick="copyText(this.dataset.addr)" data-addr="${esc(addr)}"
              title="${esc(t('ux.copy'))}">${esc(addr)}</button>
    </div>`;
  const rows = [];
  if (s.streamProtocol !== 'WebRtc')
    rows.push(row('moon', t('ux.connect.moonlight'), t('ux.connect.moonlightHow'),
                  s.sunshineStreamPort === 47989 ? ip : `${ip}:${s.sunshineStreamPort}`));
  if (s.streamProtocol !== 'Moonlight')
    rows.push(row('web', t('ux.connect.webrtc'), t('ux.connect.webrtcHow'),
                  s.webRtcPort === 8000 ? ip : `${ip}:${s.webRtcPort}`));
  const others = addresses.slice(1);
  box.innerHTML = `
    <h4 class="dep-log-label">${esc(t('ux.connect.addTitle'))}</h4>
    ${rows.join('')}
    ${others.length ? `<div class="hint">${esc(t('ux.connect.otherIps', { ips: others.join(', ') }))}</div>` : ''}`;
}

async function copyText(text) {
  try { await navigator.clipboard.writeText(text); toast(t('ux.copied', { text })); }
  catch { /* sin permiso de portapapeles (http sin localhost): se ve igualmente */ }
}

// ── Tarjeta de sesión: progreso, errores con acción y avisos de componentes ──
const START_PHASES = ['user', 'rdp', 'profile', 'unlock', 'sunshine'];

function startProgress(s) {
  if (s.state !== 'Starting') return '';
  const phase = s.startPhase || (s.local ? 'sunshine' : 'user');
  const label = esc(t('ux.phase.' + phase));
  if (s.local) return `<div class="start-steps" role="status"><div class="start-steps-label">${label}</div></div>`;
  return stepsHtml(phase, false);
}

// Error con el paso en que falló, una pista si se conoce la causa y sus acciones (insights.js).
function errorBox(s) { return errorBoxHtml(s); }

// Una sesión RDP necesita el multi-RDP: si falta, se dice en la tarjeta con su botón.
function rdpWarning(s) {
  if (s.local || !rdpLastStatus) return '';
  const key = rdpStateKey(rdpLastStatus);
  if (key !== 'missing' && key !== 'broken') return '';
  return `<div class="card-warning card-warning-action">
      <span>${esc(t(key === 'broken' ? 'ux.rdpBroken' : 'ux.rdpMissing'))}</span>
      <button type="button" class="btn btn-ghost btn-xs" onclick="openRdpModal()">${esc(t('ux.review'))}</button>
    </div>`;
}

// Steam sin "Recordarme" en el usuario de Windows de la sesión: "Steam Big Picture" acaba
// en la ventana de iniciar sesión. Se puede ocultar si en esa sesión no se usa Steam.
const STEAM_HINT_KEY = id => `openstreamms.steamHintHidden.${id}`;

function steamWarning(s) {
  if (s.steamLogin !== 'missing') return '';
  try { if (localStorage.getItem(STEAM_HINT_KEY(s.id))) return ''; } catch { /* sin almacenamiento */ }
  const user = s.isolated ? s.isolatedUser : s.username;
  return `<div class="card-warning card-warning-action card-warning-steam">
      <span>${esc(t('steam.missing', { user: user || '' }))}</span>
      <button type="button" class="btn btn-ghost btn-xs" onclick="hideSteamWarning('${s.id}')">${esc(t('steam.hide'))}</button>
    </div>`;
}

function hideSteamWarning(id) {
  try { localStorage.setItem(STEAM_HINT_KEY(id), '1'); } catch { /* sin almacenamiento */ }
  renderSessions();
}

// ── Usuarios dedicados y perfiles sin sesión ───────────────────────────────
let orphanUsers = [];          // GET /api/sessions/isolated-users (sin tamaños)
let orphanSized = null;        // la misma lista con tamaños, para la ventana

async function loadOrphans() {
  const res = await api('GET', '/api/sessions/isolated-users');
  if (!res || !res.ok) return;
  orphanUsers = await res.json();
  const count = byId('orphans-count');
  if (count) {
    count.textContent = orphanUsers.length;
    count.classList.toggle('orphans-some', orphanUsers.length > 0);
  }
}

async function openOrphansModal() {
  document.querySelectorAll('.nav-pop').forEach(p => p.classList.add('hidden'));
  orphanSized = null;
  renderOrphans();
  show('orphans-modal');
  // Con tamaños tarda más: primero se ve la lista y luego cuánto ocupa cada perfil.
  await loadOrphans();
  renderOrphans();
  const res = await api('GET', '/api/sessions/isolated-users?sizes=true');
  if (res && res.ok) { orphanSized = await res.json(); renderOrphans(); }
}

function formatBytes(n) {
  if (n == null) return '';
  const units = ['B', 'KB', 'MB', 'GB', 'TB'];
  let i = 0;
  while (n >= 1024 && i < units.length - 1) { n /= 1024; i++; }
  const digits = i && n < 10 ? 1 : 0;
  return `${n.toLocaleString(I18n.lang, { minimumFractionDigits: digits, maximumFractionDigits: digits })} ${units[i]}`;
}

function renderOrphans() {
  const box = byId('orphans-list');
  if (!box) return;
  const list = orphanSized || orphanUsers;
  if (!list.length) {
    box.innerHTML = `<p class="orphans-empty">${esc(t('orphans.empty'))}</p>`;
    return;
  }
  box.innerHTML = list.map(o => {
    const meta = [t(o.accountExists ? 'orphans.kindUser' : 'orphans.kindProfile')];
    if (orphanSized) meta.push(o.sizeBytes == null ? t('orphans.noSize') : formatBytes(o.sizeBytes));
    else meta.push(t('orphans.sizing'));
    if (o.steamLogin === 'saved')   meta.push(t('orphans.steamSaved'));
    if (o.signedIn)                 meta.push(t('orphans.signedIn'));
    return `<div class="orphan-row">
        <div class="orphan-main">
          <code class="orphan-name">${esc(o.name)}</code>
          <span class="orphan-meta">${esc(meta.join(' · '))}</span>
          ${o.profilePath ? `<span class="orphan-path">${esc(o.profilePath)}</span>` : ''}
        </div>
        <button type="button" class="btn btn-danger btn-xs" ${o.signedIn ? 'disabled' : ''}
                onclick="deleteOrphan(${esc(JSON.stringify(o.key))})">${esc(plainLabel(t('btn.delete')))}</button>
      </div>`;
  }).join('');
}

async function deleteOrphan(key) {
  const o = (orphanSized || orphanUsers).find(x => x.key === key);
  if (!o) return;
  const size = o.sizeBytes != null ? formatBytes(o.sizeBytes) : t('orphans.unknownSize');
  const msg = t(o.accountExists ? 'orphans.confirmUser' : 'orphans.confirmProfile',
                { name: o.name, size, steam: o.steamLogin === 'saved' ? t('orphans.confirmSteam') : '' });
  if (!await ask(msg, { danger: true, ok: t('btn.delete') })) return;
  const res = await api('DELETE', `/api/sessions/isolated-users?key=${encodeURIComponent(key)}`);
  if (!res) return;
  if (!res.ok && res.status !== 204) { toast(await I18n.responseError(res) || t('toast.deleteError'), true); return; }
  toast(t('orphans.deleted', { name: o.name }));
  if (orphanSized) orphanSized = orphanSized.filter(x => x.key !== key);
  await loadOrphans();
  renderOrphans();
}

function openSessionLog(id) {
  const s = sessions.find(x => x.id === id);
  if (s) openLogModal(s.id, s.name);
}

// Reintentar tras un error: reinicio completo sin pedir confirmación (no hay nada que cortar).
async function retrySession(id) {
  const res = await api('POST', `/api/sessions/${id}/restart`);
  if (!res) return;
  if (!res.ok) { toast(await I18n.responseError(res), true); return; }
  toast(t('toast.restarting'));
  loadSessions();
}

// ── Primeros pasos (sin sesiones todavía) ─────────────────────────────────
function onboardingHtml() {
  const deps = [
    { name: t('rdp.pill'), st: rdpLastStatus,            open: 'openRdpModal()', rdp: true },
    { name: 'ViGEmBus',    st: drivers.vigem.lastStatus,   open: "openDriverModal('vigem')" },
    { name: 'HidHide',     st: drivers.hidhide.lastStatus, open: "openDriverModal('hidhide')" },
  ];
  const depState = d => !d.st ? 'checking' : d.st.busy ? 'working' : d.st.rebootRequired ? 'reboot'
                     : d.st.installed ? 'ok' : d.rdp && d.st.pending ? 'activates'
                     : d.rdp && d.st.wrapper ? 'broken' : 'missing';
  const depsOk = deps.every(d => ['ok', 'activates'].includes(depState(d)));
  const depRow = d => {
    const state = depState(d);
    return `<li class="dep-line dep-${state}" ${state === 'activates' ? `title="${esc(rdpPendingText(d.st))}"` : ''}>
        <span class="dep-dot"></span><span>${esc(d.name)}</span>
        <span class="dep-state">${esc(t('ux.dep.' + state))}</span>
        ${['missing', 'broken', 'reboot'].includes(state)
          ? `<button type="button" class="btn btn-ghost btn-xs" onclick="${d.open}">${esc(t(state === 'missing' ? 'ux.install' : 'ux.review'))}</button>` : ''}
      </li>`;
  };
  const step = (n, done, title, body) => `
    <li class="onb-step${done ? ' done' : ''}">
      <span class="onb-num">${done ? '✓' : n}</span>
      <div class="onb-body"><div class="onb-title">${esc(title)}</div>${body}</div>
    </li>`;
  return `
    <div class="onboarding" id="onboarding">
      <div class="onb-head">
        <div class="empty-icon">${uiIcon('screen')}</div>
        <div><h2>${esc(t('ux.onb.title'))}</h2><p class="hint">${esc(t('ux.onb.subtitle'))}</p></div>
        <button type="button" class="btn btn-ghost btn-sm onb-guide" onclick="openGuide()">${uiIcon('link')}<span>${esc(t('guide.open'))}</span></button>
      </div>
      <ol class="onb-steps">
        ${step(1, depsOk, t('ux.onb.step1'), `<p class="hint">${esc(t('ux.onb.step1Hint'))}</p><ul class="dep-lines">${deps.map(depRow).join('')}</ul>`)}
        ${step(2, false, t('ux.onb.step2'), `<p class="hint">${esc(t('ux.onb.step2Hint'))}</p>
            <button type="button" class="btn btn-primary btn-sm" onclick="openNewModal()">${esc(t('toolbar.newSession'))}</button>`)}
        ${step(3, false, t('ux.onb.step3'), `<p class="hint">${esc(t('ux.onb.step3Hint'))}</p>`)}
      </ol>
    </div>`;
}

// ── Teclado: Esc cierra la ventana de arriba ──────────────────────────────
document.addEventListener('keydown', e => {
  if (e.key !== 'Escape' || menuEl || document.querySelector('.nav-pop:not(.hidden)')) return;
  if (!byId('confirm-modal').classList.contains('hidden')) { closeModal('confirm-modal'); return; }
  const open = [...document.querySelectorAll('.modal-backdrop:not(.hidden)')];
  if (open.length) closeModal(open[open.length - 1].id);
}, { capture: true });   // antes que el que cierra los menús: con un menú abierto, Esc solo cierra el menú
