'use strict';

// ── Estado global ──────────────────────────────────────────────────────────
let sessions         = [];
let sessionTimer     = null;
let logTimer         = null;
let logSessionId     = null;
let autoScroll       = true;
let toastTimer       = null;
let credentialsTested = false;   // true solo si el último test de credenciales fue exitoso

// ── Inicialización ─────────────────────────────────────────────────────────
document.addEventListener('DOMContentLoaded', () => {
  startRefresh();
  rdpRefresh();
  DRIVER_KEYS.forEach(driverRefresh);
  setInterval(whenVisible(() => {
    rdpRefreshPillOnly();
    DRIVER_KEYS.forEach(driverRefreshPillOnly);
  }), 15000);
});

// Re-renderizar al cambiar de idioma para que los botones/badges generados
// dinámicamente por JS se traduzcan inmediatamente.
window.addEventListener('langchange', () => {
  renderSessions();
  if (rdpLastStatus) {
    updateRdpPill(rdpLastStatus);
    renderRdpStatus(rdpLastStatus);
  }
  for (const k of DRIVER_KEYS) {
    const st = drivers[k].lastStatus;
    if (st) { updateDriverPill(k, st); renderDriverStatus(k, st); }
  }
  // Puerto derivado en ambos modales
  updateDerivedWebPort('new');
  updateDerivedWebPort('edit');
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
  sessions = await res.json();
  if (seatRes && seatRes.ok) seats = await seatRes.json();
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
  const grid  = document.getElementById('sessions-grid');
  const count = document.getElementById('session-count');
  count.textContent = sessions.length;

  const html = sessions.length === 0
    ? `<div class="empty-state">
        <div class="empty-icon">${uiIcon('screen')}</div>
        <p>${esc(t('empty.noSessions'))}</p>
        <p>${t('empty.noSessions_hint')}</p>
      </div>`
    : sessions.map(renderCard).join('');
  renderInto(grid, html);
}

// Una sesión: miniatura de su pantalla, nombre, estado, botón de encender y el resto en "⋯".
function renderCard(s) {
  const isRunning = s.state === 'Running';
  const isBusy    = s.state === 'Starting' || s.state === 'Stopping';
  const screen = s.local
    ? `<div class="screen-thumb screen-local">${uiIcon('pc')}<span class="screen-res">${esc(t('card.localShort'))}</span></div>`
    : `<div class="screen-thumb"><span class="screen-res">${s.rdpWidth}×${s.rdpHeight}</span>
         <span class="screen-fps">${s.rdpFrameRate || 60} fps</span></div>`;
  const power = isBusy
    ? `<button class="power-btn busy" disabled title="${esc(t(s.state === 'Starting' ? 'btn.starting' : 'btn.stopping'))}">${uiIcon('power')}</button>`
    : `<button class="power-btn ${isRunning ? 'on' : 'off'}" onclick="${isRunning ? 'stopSession' : 'startSession'}('${s.id}')"
               title="${esc(t(isRunning ? 'btn.stop' : 'btn.start'))}">${uiIcon('power')}</button>`;

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
          <div class="card-user">${s.local ? esc(t('card.localUser')) : s.isolated ? esc(t('card.isolatedUser')) : `${esc(s.username)}@${esc(s.domain)}`}</div>
          ${badge(s.state)}
        </div>
        ${power}
      </div>
      <div class="card-chips">${protos.join('')}${sessionSeatLine(s)}</div>
      ${s.streamProtocol !== 'Moonlight' && !s.sunshineSupportsWebRtc
        ? `<div class="card-warning">${esc(t('proto.unsupported'))}</div>` : ''}
      ${s.errorMessage ? `<div class="card-error">${esc(s.errorMessage)}</div>` : ''}
      <div class="card-footer">
        <label class="toggle" title="${esc(t('card.autostart_title'))}">
          <input type="checkbox" ${s.enabled ? 'checked' : ''} onchange="setEnabled('${s.id}', this.checked)">
          <span class="toggle-track"></span>
          ${esc(t('card.autostart'))}
        </label>
        <button class="icon-btn more-btn" onclick="sessionMenu(event, '${s.id}')" title="${esc(t('nav.more'))}">${uiIcon('more')}</button>
      </div>
    </div>`;
}

// Acciones secundarias de una sesión (menú "⋯").
function sessionMenu(e, id) {
  const s = sessions.find(x => x.id === id);
  if (!s) return;
  const running = s.state === 'Running', busy = s.state === 'Starting' || s.state === 'Stopping';
  openMenu(e, [
    { icon: 'restart',  label: t('btn.restart'),  run: () => restartSession(s.id, s.name) },
    { icon: 'moon',     label: t('btn.sunshine'), run: () => openSunMgrModal(s.id), disabled: !running, hint: t('btn.sunshine_disabled_title') },
    { icon: 'external', label: t('btn.panel'),    run: () => openSunshinePanel(s.id), disabled: !running, hint: t('btn.panel_disabled_title') },
    { icon: 'log',      label: t('btn.log'),      run: () => openLogModal(s.id, s.name) },
    { icon: 'gear',     label: t('btn.settings'), run: () => openEditModal(s.id) },
    { sep: true },
    { icon: 'trash',    label: t('btn.delete'),   run: () => deleteSession(s.id, s.name), danger: true, disabled: running || busy },
  ]);
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

function getProtocol(prefix) {
  const checked = document.querySelector(`#${prefix}-modal input[id^="${prefix}-proto-"]:checked`);
  return checked ? checked.value : 'Both';
}

function setProtocolFields(prefix, protocol, webRtcPort, mediaMin, mediaMax) {
  const id = { Moonlight: 'moonlight', WebRtc: 'webrtc', Both: 'both' }[protocol] || 'both';
  document.getElementById(`${prefix}-proto-${id}`).checked = true;
  document.getElementById(`${prefix}-webrtc-port`).value      = webRtcPort || '';
  document.getElementById(`${prefix}-webrtc-media-min`).value = mediaMin   || '';
  document.getElementById(`${prefix}-webrtc-media-max`).value = mediaMax   || '';
  updateProtocolFields(prefix);
}

// Los campos WebRTC solo tienen sentido si el protocolo incluye WebRTC.
function updateProtocolFields(prefix) {
  const webrtc = getProtocol(prefix) !== 'Moonlight';
  document.getElementById(`${prefix}-webrtc-ports`)
    .querySelectorAll('input').forEach(el => { el.disabled = !webrtc; });
}

function readProtocolFields(prefix) {
  const num = id => parseInt(document.getElementById(id).value, 10) || 0;
  return {
    streamProtocol:     getProtocol(prefix),
    webRtcPort:         num(`${prefix}-webrtc-port`),
    webRtcMediaPortMin: num(`${prefix}-webrtc-media-min`),
    webRtcMediaPortMax: num(`${prefix}-webrtc-media-max`),
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
  if (!res.ok) { toast(await res.text(), true); return; }
  toast(t('toast.starting'));
  loadSessions();
}

async function stopSession(id) {
  const res = await api('POST', `/api/sessions/${id}/stop`);
  if (!res) return;
  if (!res.ok) { toast(await res.text(), true); return; }
  toast(t('toast.stopped'));
  loadSessions();
}

async function restartSession(id, name) {
  if (!confirm(t('confirm.restart', { name }))) return;
  const res = await api('POST', `/api/sessions/${id}/restart`);
  if (!res) return;
  if (!res.ok) { toast(await res.text(), true); return; }
  toast(t('toast.restarting'));
  loadSessions();
}

async function deleteSession(id, name) {
  if (!confirm(t('confirm.delete', { name }))) return;
  const res = await api('DELETE', `/api/sessions/${id}`);
  if (!res) return;
  if (res.status === 400) { toast(await res.text(), true); return; }
  if (!res.ok && res.status !== 204) { toast(t('toast.deleteError'), true); return; }
  toast(t('toast.deleted', { name }));
  loadSessions();
}

async function setEnabled(id, enabled) {
  const res = await api('PATCH', `/api/sessions/${id}/enabled`, { enabled });
  if (!res || !res.ok) { toast(t('toast.autostartError'), true); loadSessions(); }
  else toast(enabled ? t('toast.autostartOn') : t('toast.autostartOff'));
}

// ── Prueba de credenciales ─────────────────────────────────────────────────
function resetCredTest() {
  credentialsTested = false;
  document.getElementById('cred-test-result').style.display = 'none';
  document.getElementById('btn-create').disabled = true;
}

async function testCredentials() {
  const form     = document.getElementById('new-form');
  const username = form.querySelector('[name=Username]').value.trim();
  const domain   = form.querySelector('[name=Domain]').value.trim() || '.';
  const password = form.querySelector('[name=Password]').value;

  if (!username || !password) {
    showCredTestResult(false, t('credTest.needCreds'));
    return;
  }

  const btn     = document.getElementById('btn-test-creds');
  const spinner = document.getElementById('btn-test-spinner');
  btn.disabled          = true;
  spinner.style.display = '';

  try {
    const res = await api('POST', '/api/sessions/test-credentials',
                          { username, domain, password });
    if (!res) return;
    const data = await res.json();
    showCredTestResult(data.success, data.message);
    credentialsTested = data.success;
    document.getElementById('btn-create').disabled = !data.success;
  } finally {
    btn.disabled          = false;
    spinner.style.display = 'none';
  }
}

function showCredTestResult(success, message) {
  const box  = document.getElementById('cred-test-result');
  const icon = document.getElementById('cred-test-icon');
  const msg  = document.getElementById('cred-test-msg');
  box.className         = `cred-test ${success ? 'cred-test-ok' : 'cred-test-err'}`;
  box.style.display     = '';
  icon.textContent      = success ? '✓' : '✗';
  msg.textContent       = message;
}

// Sesión aislada: el servicio crea el usuario, así que se ocultan las credenciales
// (y su prueba obligatoria) y se muestra la opción de perfil efímero.
function updateIsolatedUi(prefix) {
  const isolated = document.getElementById(prefix === 'new' ? 'chk-isolated' : 'edit-isolated').checked;
  document.getElementById(`${prefix}-ephemeral-row`).style.display = isolated ? '' : 'none';
  if (prefix !== 'new') return;

  // La sesión local usa el usuario del escritorio: tampoco pide credenciales.
  const needsCreds = !isolated && !isLocalKind();
  const block = document.getElementById('new-creds-block');
  block.style.display = needsCreds ? '' : 'none';
  block.querySelectorAll('[name=Username],[name=Password]').forEach(el => { el.required = needsCreds; });
  document.getElementById('btn-create').disabled = needsCreds && !credentialsTested;
}

// ── Tipo de sesión: RDP en segundo plano o escritorio local ───────────────
function isLocalKind() {
  return document.getElementById('new-kind-local').checked;
}

function setKindFields(prefix, local) {
  document.getElementById(`${prefix}-modal`).classList.toggle('kind-local', local);
}

function updateKindUi(prefix) {
  setKindFields(prefix, isLocalKind());
  updateIsolatedUi(prefix);
}

// ── Modal: nueva sesión ────────────────────────────────────────────────────
function openNewModal() {
  document.getElementById('new-form').reset();
  document.getElementById('new-error').style.display = 'none';
  document.getElementById('chk-bg').checked = true;
  document.getElementById('new-res-preset').value = '1920x1080';
  document.getElementById('new-res-custom').style.display = 'none';
  setProtocolFields('new', 'Both', 8000, 40000, 40019);
  updateDerivedWebPort('new');
  resetCredTest();
  updateKindUi('new');
  show('new-modal');
  stopRefresh();
  suggestPorts();
}

// Rellena el formulario con puertos que no chocan con ninguna sesión existente.
async function suggestPorts() {
  const res = await api('GET', '/api/sessions/ports/suggest');
  if (!res || !res.ok) return;
  const p = await res.json();
  document.getElementById('new-stream-port').value = p.sunshineStreamPort;
  setProtocolFields('new', getProtocol('new'), p.webRtcPort, p.webRtcMediaPortMin, p.webRtcMediaPortMax);
  updateDerivedWebPort('new');
}

function closeNewModal() {
  hide('new-modal');
  startRefresh();
}

async function submitNew(e) {
  e.preventDefault();

  const local    = isLocalKind();
  const isolated = !local && document.getElementById('chk-isolated').checked;
  if (!isolated && !local && !credentialsTested) {
    const errEl = document.getElementById('new-error');
    errEl.textContent   = t('new.errorTestCreds');
    errEl.style.display = '';
    return;
  }

  const fd  = new FormData(e.target);
  const resolution = getResolution('new');
  const body = {
    name:               fd.get('Name'),
    username:           fd.get('Username'),
    domain:             fd.get('Domain') || '.',
    password:           fd.get('Password'),
    sunshineExePath:    fd.get('SunshineExePath') || null,
    rdpBackground:      !!fd.get('RdpBackground'),
    vddEnabled:         !!fd.get('VddEnabled'),
    rdpWidth:           resolution.w,
    rdpHeight:          resolution.h,
    rdpFrameRate:       parseInt(fd.get('RdpFrameRate'), 10) || 60,
    rdpColorDepth:      parseInt(fd.get('RdpColorDepth'), 10) || 32,
    sunshineStreamPort: parseInt(fd.get('SunshineStreamPort'), 10) || 47989,
    enabled:            !!fd.get('Enabled'),
    useStreamProfile:   !!fd.get('UseStreamProfile'),
    sunshineName:       (fd.get('SunshineName')       || '').trim() || null,
    capture:            fd.get('Capture')             || null,
    encoder:            fd.get('Encoder')             || null,
    outputName:         (fd.get('OutputName')         || '').trim() || null,
    originWebUiAllowed: fd.get('OriginWebUiAllowed')  || null,
    sunshineAuthUser:   (fd.get('SunshineAuthUser')   || '').trim() || null,
    sunshineAuthPass:   (fd.get('SunshineAuthPass')   || '')        || null,
    rotateSunshineCredentials: !!fd.get('RotateSunshineCredentials'),
    isolated,
    isolatedEphemeral:  isolated && !!fd.get('IsolatedEphemeral'),
    local,
    ...readProtocolFields('new'),
  };
  if (isolated || local) {
    // El servicio crea el usuario dedicado o usa el del escritorio: las credenciales del formulario no se usan.
    body.username = '';
    body.domain   = '.';
    body.password = '';
  }

  const errEl = document.getElementById('new-error');
  errEl.style.display = 'none';

  const res = await api('POST', '/api/sessions', body);
  if (!res) return;
  if (!res.ok) {
    errEl.textContent  = await res.text();
    errEl.style.display = '';
    return;
  }

  closeNewModal();
  toast(t('toast.created'));
  loadSessions();
}

// ── Modal: log ─────────────────────────────────────────────────────────────
function openLogModal(id, name) {
  logSessionId = id;
  autoScroll   = true;
  document.getElementById('scroll-toggle').checked = true;
  document.getElementById('log-title').textContent = t('log.titleFor', { name });
  document.getElementById('log-box').innerHTML = `<span class="log-empty">${esc(t('log.loading'))}</span>`;
  document.getElementById('log-status').textContent = '';
  show('log-modal');
  stopRefresh();
  fetchLogs();
  logTimer = setInterval(whenVisible(fetchLogs), 2500);
}

function closeLogModal() {
  clearInterval(logTimer);
  logTimer = null;
  logSessionId = null;
  hide('log-modal');
  startRefresh();
}

async function fetchLogs() {
  if (!logSessionId) return;
  const lines = document.getElementById('log-lines').value;
  const res   = await api('GET', `/api/sessions/${logSessionId}/logs?lines=${lines}`);
  if (!res || !res.ok) return;

  const data  = await res.json();
  const box   = document.getElementById('log-box');
  const status = document.getElementById('log-status');

  status.textContent = t('log.status', {
    count: data.linesReturned,
    time:  new Date().toLocaleTimeString(),
  });

  if (data.lines.length === 0) {
    box.innerHTML = `<span class="log-empty">${esc(t('log.empty'))}</span>`;
    return;
  }

  const wasAtBottom = box.scrollHeight - box.scrollTop <= box.clientHeight + 10;
  box.textContent   = data.lines.join('\n');

  if (autoScroll && wasAtBottom) box.scrollTop = box.scrollHeight;
}

// ── Puerto derivado (stream + 1 = panel HTTPS interno) ────────────────────
function updateDerivedWebPort(prefix) {
  const input   = document.getElementById(prefix === 'new' ? 'new-stream-port' : 'edit-port');
  const derived = document.getElementById(`${prefix}-port-derived`);
  if (!input || !derived) return;
  const v = parseInt(input.value, 10);
  derived.textContent = Number.isFinite(v) && v > 0
    ? t('new.portDerived', { web: v + 1 })
    : '';
}

// ── Resolución — combobox helpers ─────────────────────────────────────────
const RES_PRESETS = [
  '1280x720', '1366x768', '1600x900', '1920x1080',
  '2560x1080', '2560x1440', '3440x1440', '3840x2160',
];

function onResPreset(prefix) {
  const sel    = document.getElementById(`${prefix}-res-preset`);
  const custom = document.getElementById(`${prefix}-res-custom`);
  custom.style.display = sel.value === 'custom' ? '' : 'none';
}

function getResolution(prefix) {
  const sel = document.getElementById(`${prefix}-res-preset`);
  if (sel.value === 'custom') {
    return {
      w: parseInt(document.getElementById(`${prefix}-res-w`).value, 10) || 1920,
      h: parseInt(document.getElementById(`${prefix}-res-h`).value, 10) || 1080,
    };
  }
  const [w, h] = sel.value.split('x').map(Number);
  return { w, h };
}

function setResPreset(prefix, w, h) {
  const key = `${w}x${h}`;
  const sel  = document.getElementById(`${prefix}-res-preset`);
  const custom = document.getElementById(`${prefix}-res-custom`);

  if (RES_PRESETS.includes(key)) {
    sel.value = key;
    custom.style.display = 'none';
  } else {
    sel.value = 'custom';
    document.getElementById(`${prefix}-res-w`).value = w;
    document.getElementById(`${prefix}-res-h`).value = h;
    custom.style.display = '';
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
  if (id === 'new-modal' || id === 'edit-modal') updateSessionPreview(id.split('-')[0]);
  if (id === 'seat-modal' && typeof updateSeatPreview === 'function') updateSeatPreview();
}
function hide(id) { document.getElementById(id).classList.add('hidden'); }

function handleBackdropClick(e, id) {
  if (e.target.id === id) {
    if (id === 'new-modal')    closeNewModal();
    else if (id === 'log-modal')    closeLogModal();
    else if (id === 'edit-modal')   closeEditModal();
    else if (id === 'rdp-modal')    closeRdpModal();
    else if (id === 'vigem-modal')  closeDriverModal('vigem');
    else if (id === 'hidhide-modal') closeDriverModal('hidhide');
    else if (id === 'sunmgr-modal') closeSunMgrModal();
    else if (id === 'power-modal')  closePowerModal();
  }
}

// ── Modal: editar sesión ───────────────────────────────────────────────────
function openEditModal(id) {
  const s = sessions.find(x => x.id === id);
  if (!s) return;

  document.getElementById('edit-id').value      = s.id;
  document.getElementById('edit-title').textContent = t('edit.titleFor', { name: s.name });
  document.getElementById('edit-name').value    = s.name;
  document.getElementById('edit-exe').value     = s.sunshineExePath || '';
  document.getElementById('edit-port').value    = s.sunshineStreamPort;
  updateDerivedWebPort('edit');
  setProtocolFields('edit', s.streamProtocol, s.webRtcPort, s.webRtcMediaPortMin, s.webRtcMediaPortMax);
  document.getElementById('edit-bg').checked    = s.rdpBackground;
  document.getElementById('edit-vdd').checked   = s.vddEnabled;
  document.getElementById('edit-profile').checked = s.useStreamProfile;
  document.getElementById('edit-rdp-fps').value = s.rdpFrameRate || 60;
  document.getElementById('edit-rdp-bpp').value = String(s.rdpColorDepth || 32);
  document.getElementById('edit-sunshine-name').value = s.sunshineName || '';
  document.getElementById('edit-capture').value       = s.capture     || '';
  document.getElementById('edit-encoder').value       = s.encoder     || '';
  document.getElementById('edit-output-name').value   = s.outputName  || '';
  document.getElementById('edit-origin').value        = s.originWebUiAllowed || '';
  document.getElementById('edit-panel-user').value    = s.sunshineAuthUser || '';
  document.getElementById('edit-panel-pass').value    = s.sunshineAuthPass || '';
  document.getElementById('edit-rotate-creds').checked = !!s.rotateSunshineCredentials;
  document.getElementById('edit-isolated').checked = !!s.isolated;
  document.getElementById('edit-isolated-ephemeral').checked = !!s.isolatedEphemeral;
  updateIsolatedUi('edit');
  setKindFields('edit', !!s.local);
  document.getElementById('edit-error').style.display = 'none';
  setResPreset('edit', s.rdpWidth, s.rdpHeight);

  show('edit-modal');
  stopRefresh();
}

function closeEditModal() {
  hide('edit-modal');
  startRefresh();
}

async function submitEdit(e) {
  e.preventDefault();

  const id  = document.getElementById('edit-id').value;
  const resolution = getResolution('edit');
  const body = {
    name:               document.getElementById('edit-name').value.trim(),
    sunshineExePath:    document.getElementById('edit-exe').value.trim() || null,
    rdpWidth:           resolution.w,
    rdpHeight:          resolution.h,
    rdpFrameRate:       parseInt(document.getElementById('edit-rdp-fps').value, 10) || 60,
    rdpColorDepth:      parseInt(document.getElementById('edit-rdp-bpp').value, 10) || 32,
    sunshineStreamPort: parseInt(document.getElementById('edit-port').value, 10) || 47989,
    rdpBackground:      document.getElementById('edit-bg').checked,
    vddEnabled:         document.getElementById('edit-vdd').checked,
    useStreamProfile:   document.getElementById('edit-profile').checked,
    sunshineName:       document.getElementById('edit-sunshine-name').value.trim() || null,
    capture:            document.getElementById('edit-capture').value   || null,
    encoder:            document.getElementById('edit-encoder').value   || null,
    outputName:         document.getElementById('edit-output-name').value.trim() || null,
    originWebUiAllowed: document.getElementById('edit-origin').value    || null,
    sunshineAuthUser:   document.getElementById('edit-panel-user').value.trim() || null,
    sunshineAuthPass:   document.getElementById('edit-panel-pass').value        || null,
    rotateSunshineCredentials: document.getElementById('edit-rotate-creds').checked,
    isolated:           document.getElementById('edit-isolated').checked,
    isolatedEphemeral:  document.getElementById('edit-isolated').checked &&
                        document.getElementById('edit-isolated-ephemeral').checked,
    ...readProtocolFields('edit'),
  };

  const errEl = document.getElementById('edit-error');
  errEl.style.display = 'none';

  const res = await api('PATCH', `/api/sessions/${id}/config`, body);
  if (!res) return;
  if (!res.ok) {
    errEl.textContent   = await res.text();
    errEl.style.display = '';
    return;
  }

  closeEditModal();
  toast(t('toast.saved'));
  loadSessions();
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

  pill.classList.remove('status-pill-ok','status-pill-missing','status-pill-broken','status-pill-working','status-pill-warn');

  let cls, key;
  if (st.busy)                         { cls = 'status-pill-working'; key = 'rdp.state.working'; }
  else if (st.installed)               { cls = 'status-pill-ok';      key = 'rdp.state.ok';      }
  else if (st.wrapper)                 { cls = 'status-pill-broken';  key = 'rdp.state.broken';  }
  else                                 { cls = 'status-pill-missing'; key = 'rdp.state.missing'; }

  pill.classList.add(cls);
  state.textContent = t(key);
  updateSystemPill();
}

function renderRdpStatus(st) {
  if (!st) return;
  const badge = document.getElementById('rdp-status-badge');
  const cls   = st.busy ? 'badge-starting'
              : st.installed ? 'badge-running'
              : 'badge-error';
  const key   = st.busy ? 'rdp.state.working'
              : st.installed ? 'rdp.state.ok'
              : st.wrapper ? 'rdp.state.broken'
              : 'rdp.state.missing';
  badge.className = `badge ${cls}`;
  badge.innerHTML = `<span class="badge-dot"></span>${esc(t(key))}`;
  // ServiceDll apunta al wrapper pero TermService no lo tiene cargado (falta reiniciarlo, o
  // la tarea de autoupdate de asmtron lo cambió por detrás).
  badge.title = st.wrapper && st.loaded === false ? t('rdp.notLoaded') : '';

  document.getElementById('rdp-install-dir').textContent = st.installDir     || '—';
  document.getElementById('rdp-bundle').textContent      =
    st.wrapper ? `${st.wrapper} ${st.bundleVersion || ''}`.trim() : (st.bundleVersion || '—');
  document.getElementById('rdp-termsrv').textContent     = st.termsrvVersion || '—';
  document.getElementById('rdp-service-dll').textContent = st.serviceDllPath || '—';

  // Habilitar/deshabilitar botones según estado. Actualizar solo tiene sentido con asmtron
  // (con TermWrap no hay rdpwrap.ini que refrescar).
  const busy = !!st.busy;
  document.getElementById('rdp-btn-install').disabled   = busy;
  document.getElementById('rdp-btn-update').disabled    = busy || st.wrapper !== 'asmtron' || !st.installed;
  document.getElementById('rdp-btn-uninstall').disabled = busy || st.wrapper !== 'asmtron';
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
  const msg = t('rdp.confirm.install') + (rdpLastStatus?.remoteIdd ? '\n\n' + t('rdp.confirm.leaveRemoteIdd') : '');
  if (!confirm(msg)) return;
  const res = await api('POST', '/api/rdpwrap/install');
  if (!res) return;
  if (!res.ok) { toast(await res.text(), true); return; }
  rdpRefresh();
}

async function rdpUpdate() {
  if (!confirm(t('rdp.confirm.update'))) return;
  const res = await api('POST', '/api/rdpwrap/update');
  if (!res) return;
  if (!res.ok) { toast(await res.text(), true); return; }
  rdpRefresh();
}

async function rdpUninstall() {
  if (!confirm(t('rdp.confirm.uninstall'))) return;
  const res = await api('POST', '/api/rdpwrap/uninstall');
  if (!res) return;
  if (!res.ok) { toast(await res.text(), true); return; }
  rdpRefresh();
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
      return t(key, { detail: body.message || '' });
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
  document.getElementById('sunmgr-title').textContent = t('sunmgr.title', { name: s.name });
  document.getElementById('sunmgr-pair-form').reset();
  document.getElementById('sunmgr-pair-result').style.display = 'none';
  sunmgrSetPendingPairings(null);
  document.getElementById('sunmgr-clients-list').innerHTML = `<span class="sunmgr-empty">${esc(t('sunmgr.loading'))}</span>`;
  show('sunmgr-modal');
  sunmgrLoadClients();
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
      result.textContent = t('sunmgr.err.proxy_error', { detail: body?.message || '' });
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
  if (!confirm(t('sunmgr.confirm.unpair', { name }))) return;
  const res = await api('DELETE', `/api/sessions/${sunmgrSessionId}/clients/${encodeURIComponent(uuid)}`);
  if (!res) return;
  if (!res.ok && res.status !== 204) { toast(await sunmgrTranslateError(res), true); return; }
  await sunmgrLoadClients();
}

async function sunmgrUnpairAll() {
  if (!sunmgrSessionId) return;
  if (!confirm(t('sunmgr.confirm.unpairAll'))) return;
  const res = await api('DELETE', `/api/sessions/${sunmgrSessionId}/clients`);
  if (!res) return;
  if (!res.ok && res.status !== 204) { toast(await sunmgrTranslateError(res), true); return; }
  await sunmgrLoadClients();
}

// ── Drivers (ViGEmBus, HidHide) ────────────────────────────────────────────
// Misma UI para los dos: pastilla en la barra + modal. Ids de los elementos
// con prefijo <clave>- y textos i18n con prefijo <clave>.
const drivers = {
  vigem:   { api: '/api/vigembus', lastStatus: null, logTimer: null },
  hidhide: { api: '/api/hidhide',  lastStatus: null, logTimer: null },
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
  document.getElementById(`${k}-service`).textContent = st.serviceState     || '—';
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
  const box  = document.getElementById(`${k}-log-box`);
  if (!data.lines || data.lines.length === 0) {
    box.innerHTML = `<span class="log-empty">${esc(t('vigem.log.empty'))}</span>`;
    return;
  }
  const wasAtBottom = box.scrollHeight - box.scrollTop <= box.clientHeight + 10;
  box.textContent = data.lines.join('\n');
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
  if (!confirm(t(`${k}.confirm.install`))) return;
  const res = await api('POST', `${drivers[k].api}/install`);
  if (!res) return;
  if (!res.ok) { toast(await res.text(), true); return; }
  driverRefresh(k);
}

async function driverUninstall(k) {
  if (!confirm(t(`${k}.confirm.uninstall`))) return;
  const res = await api('POST', `${drivers[k].api}/uninstall`);
  if (!res) return;
  if (!res.ok) { toast(await res.text(), true); return; }
  driverRefresh(k);
}

function toast(msg, isError = false) {
  const el = document.getElementById('toast');
  el.textContent = msg;
  el.className   = isError ? 'error' : '';
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => { el.classList.add('hidden'); }, 3500);
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

  if (confirmKey && !confirm(t(confirmKey, { delay }))) return;

  const body = (kind === 'cancel') ? undefined : { delaySeconds: delay };
  const res  = await api('POST', path, body);
  if (!res) return;
  if (!res.ok) { toast(await res.text(), true); return; }

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
  more:     '<circle cx="5" cy="12" r="1.6" fill="currentColor"/><circle cx="12" cy="12" r="1.6" fill="currentColor"/><circle cx="19" cy="12" r="1.6" fill="currentColor"/>',
  restart:  '<path d="M3 12a9 9 0 1 0 3-6.7L3 8"/><path d="M3 3v5h5"/>',
  moon:     '<path d="M21 12.8A9 9 0 1 1 11.2 3a7 7 0 0 0 9.8 9.8z"/>',
  web:      '<circle cx="12" cy="12" r="9"/><path d="M3 12h18M12 3a14 14 0 0 1 0 18M12 3a14 14 0 0 0 0 18"/>',
  external: '<path d="M14 3h7v7"/><path d="M10 14 21 3"/><path d="M21 14v5a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h5"/>',
  log:      '<path d="M4 6h16M4 12h16M4 18h10"/>',
  gear:     '<circle cx="12" cy="12" r="3"/><path d="M19.4 15a1.7 1.7 0 0 0 .3 1.8l.1.1a2 2 0 1 1-2.8 2.8l-.1-.1a1.7 1.7 0 0 0-1.8-.3 1.7 1.7 0 0 0-1 1.5V21a2 2 0 1 1-4 0v-.1a1.7 1.7 0 0 0-1.1-1.5 1.7 1.7 0 0 0-1.8.3l-.1.1a2 2 0 1 1-2.8-2.8l.1-.1a1.7 1.7 0 0 0 .3-1.8 1.7 1.7 0 0 0-1.5-1H3a2 2 0 1 1 0-4h.1a1.7 1.7 0 0 0 1.5-1.1 1.7 1.7 0 0 0-.3-1.8l-.1-.1a2 2 0 1 1 2.8-2.8l.1.1a1.7 1.7 0 0 0 1.8.3H9a1.7 1.7 0 0 0 1-1.5V3a2 2 0 1 1 4 0v.1a1.7 1.7 0 0 0 1 1.5 1.7 1.7 0 0 0 1.8-.3l.1-.1a2 2 0 1 1 2.8 2.8l-.1.1a1.7 1.7 0 0 0-.3 1.8V9a1.7 1.7 0 0 0 1.5 1H21a2 2 0 1 1 0 4h-.1a1.7 1.7 0 0 0-1.5 1z"/>',
  trash:    '<path d="M3 6h18M8 6V4h8v2M6 6l1 14h10l1-14"/>',
  screen:   '<rect x="2" y="3" width="20" height="13" rx="2"/><path d="M8 21h8M12 16v5"/>',
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
  const bad = pills.filter(p => !p.classList.contains('status-pill-ok') && !p.classList.contains('status-pill-working')).length;
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
document.addEventListener('DOMContentLoaded', () => requestAnimationFrame(moveTabIndicator));

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

function updateSessionPreview(prefix) {
  const box = document.getElementById(`${prefix}-preview`);
  if (!box) return;
  const field = (newSel, editId) => prefix === 'new' ? document.querySelector(`#new-form ${newSel}`) : document.getElementById(editId);
  const local = document.getElementById(`${prefix}-modal`).classList.contains('kind-local');
  const name = field('[name=Name]', 'edit-name')?.value.trim();
  const fps = field('[name=RdpFrameRate]', 'edit-rdp-fps')?.value || 60;
  const { w, h } = getResolution(prefix);
  const proto = getProtocol(prefix);
  const port = document.getElementById(prefix === 'new' ? 'new-stream-port' : 'edit-port')?.value;
  const webPort = document.getElementById(`${prefix}-webrtc-port`)?.value;
  const isolated = document.getElementById(prefix === 'new' ? 'chk-isolated' : 'edit-isolated')?.checked;
  // La miniatura toma la forma de la resolución (ultrapanorámicas más anchas).
  const ratio = w > 0 && h > 0 ? w / h : 16 / 9;
  const pw = Math.round(Math.min(170, Math.max(110, 82 * ratio)));
  const screen = local
    ? `<div class="preview-screen">${uiIcon('pc')}<span class="preview-fps">${esc(t('card.localShort'))}</span></div>`
    : `<div class="preview-screen" style="width:${pw}px"><span class="preview-res">${w}×${h}</span><span class="preview-fps">${esc(fps)} fps</span></div>`;
  const chips = [];
  if (proto !== 'WebRtc')    chips.push(`<span class="chip chip-moonlight">${uiIcon('moon')}Moonlight <b>${esc(port || '')}</b></span>`);
  if (proto !== 'Moonlight') chips.push(`<span class="chip chip-webrtc">${uiIcon('web')}WebRTC <b>${esc(webPort || '')}</b></span>`);
  if (!local && isolated)    chips.push(`<span class="chip">${uiIcon('seat')}${esc(t('card.isolatedUser'))}</span>`);
  setPreview(box, `${screen}
    <div class="preview-body">
      <div class="preview-name${name ? '' : ' placeholder'}">${esc(name || t('new.namePlaceholder'))}</div>
      <div class="preview-chips">${chips.join('')}</div>
    </div>`, local ? 'local' : `${w}x${h}`);
}

document.addEventListener('DOMContentLoaded', () => {
  for (const prefix of ['new', 'edit']) {
    const form = document.getElementById(`${prefix}-form`);
    const update = () => updateSessionPreview(prefix);
    form?.addEventListener('input', update);
    form?.addEventListener('change', update);
  }
});
