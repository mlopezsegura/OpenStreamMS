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
  const res = await api('GET', '/api/sessions');
  if (!res) return;
  sessions = await res.json();
  renderSessions();
}

function renderSessions() {
  const grid  = document.getElementById('sessions-grid');
  const count = document.getElementById('session-count');
  count.textContent = sessions.length;

  if (sessions.length === 0) {
    grid.innerHTML = `
      <div class="empty-state">
        <p>${esc(t('empty.noSessions'))}</p>
        <p>${t('empty.noSessions_hint')}</p>
      </div>`;
    return;
  }

  grid.innerHTML = sessions.map(renderCard).join('');
}

function renderCard(s) {
  const canStart  = ['Created','Stopped','Error'].includes(s.state);
  const isRunning = s.state === 'Running';
  const isBusy    = s.state === 'Starting' || s.state === 'Stopping';
  const canDelete = !isBusy && !isRunning;

  const startBtn = canStart
    ? `<button class="btn btn-success btn-sm" onclick="startSession('${s.id}')">${esc(t('btn.start'))}</button>`
    : isRunning
    ? `<button class="btn btn-danger btn-sm" onclick="stopSession('${s.id}')">${esc(t('btn.stop'))}</button>`
    : `<button class="btn btn-ghost btn-sm" disabled><span class="spinner"></span> ${esc(t(s.state === 'Starting' ? 'btn.starting' : 'btn.stopping'))}</button>`;

  const restartBtn = `<button class="btn btn-ghost btn-sm" onclick="restartSession('${s.id}','${esc(s.name)}')" title="${esc(t('btn.restart_title'))}">${esc(t('btn.restart'))}</button>`;

  const sunshineBtn = isRunning
    ? `<button class="btn btn-ghost btn-sm" onclick="openSunMgrModal('${s.id}')" title="${esc(t('btn.sunshine_title'))}">${esc(t('btn.sunshine'))}</button>`
    : `<button class="btn btn-ghost btn-sm" disabled title="${esc(t('btn.sunshine_disabled_title'))}">${esc(t('btn.sunshine'))}</button>`;

  const panelBtn = isRunning
    ? `<button class="btn btn-ghost btn-sm" onclick="openSunshinePanel('${s.id}')" title="${esc(t('btn.panel_title'))}">${esc(t('btn.panel'))}</button>`
    : `<button class="btn btn-ghost btn-sm" disabled title="${esc(t('btn.panel_disabled_title'))}">${esc(t('btn.panel'))}</button>`;

  const settingsBtn = `<button class="btn btn-ghost btn-sm" onclick="openEditModal('${s.id}')" title="${esc(t('btn.settings_title'))}">${esc(t('btn.settings'))}</button>`;

  const deleteBtn = canDelete
    ? `<button class="btn btn-ghost btn-sm" onclick="deleteSession('${s.id}','${esc(s.name)}')" title="${esc(t('btn.delete_title'))}">✕</button>`
    : '';

  return `
    <div class="session-card" id="card-${s.id}">
      <div class="card-header">
        <div>
          <div class="card-name">${esc(s.name)}</div>
          <div class="card-user">${s.isolated ? esc(t('card.isolatedUser')) : `${esc(s.username)}@${esc(s.domain)}`}</div>
          <div class="card-meta">${s.rdpWidth}×${s.rdpHeight} · ${s.rdpFrameRate || 60} fps · ${s.rdpColorDepth || 32} bpp · ${esc(t('card.port'))} ${s.sunshineStreamPort}</div>
        </div>
        ${badge(s.state)}
      </div>
      ${s.errorMessage ? `<div class="card-error">${esc(s.errorMessage)}</div>` : ''}
      <div class="card-footer">
        <label class="toggle" title="${esc(t('card.autostart_title'))}">
          <input type="checkbox" ${s.enabled ? 'checked' : ''}
                 onchange="setEnabled('${s.id}', this.checked)">
          <span class="toggle-track"></span>
          ${esc(t('card.autostart'))}
        </label>
        <div class="card-actions">
          ${startBtn}
          ${restartBtn}
          ${sunshineBtn}
          ${panelBtn}
          <button class="btn btn-ghost btn-sm" onclick="openLogModal('${s.id}','${esc(s.name)}')">
            ${esc(t('btn.log'))}
          </button>
          ${settingsBtn}
          ${deleteBtn}
        </div>
      </div>
    </div>`;
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

  const block = document.getElementById('new-creds-block');
  block.style.display = isolated ? 'none' : '';
  block.querySelectorAll('[name=Username],[name=Password]').forEach(el => { el.required = !isolated; });
  document.getElementById('btn-create').disabled = !isolated && !credentialsTested;
}

// ── Modal: nueva sesión ────────────────────────────────────────────────────
function openNewModal() {
  document.getElementById('new-form').reset();
  document.getElementById('new-error').style.display = 'none';
  document.getElementById('chk-bg').checked = true;
  document.getElementById('new-res-preset').value = '1920x1080';
  document.getElementById('new-res-custom').style.display = 'none';
  updateDerivedWebPort('new');
  resetCredTest();
  updateIsolatedUi('new');
  show('new-modal');
  stopRefresh();
}

function closeNewModal() {
  hide('new-modal');
  startRefresh();
}

async function submitNew(e) {
  e.preventDefault();

  const isolated = document.getElementById('chk-isolated').checked;
  if (!isolated && !credentialsTested) {
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
  };
  if (isolated) {
    // El servicio crea el usuario dedicado: las credenciales del formulario no se usan.
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

function show(id) { document.getElementById(id).classList.remove('hidden'); }
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
  else if (st.serviceDllPath && st.serviceDllPath.toLowerCase().includes('rdpwrap'))
                                       { cls = 'status-pill-broken';  key = 'rdp.state.broken';  }
  else                                 { cls = 'status-pill-missing'; key = 'rdp.state.missing'; }

  pill.classList.add(cls);
  state.textContent = t(key);
}

function renderRdpStatus(st) {
  if (!st) return;
  const badge = document.getElementById('rdp-status-badge');
  const cls   = st.busy ? 'badge-starting'
              : st.installed ? 'badge-running'
              : 'badge-error';
  const key   = st.busy ? 'rdp.state.working'
              : st.installed ? 'rdp.state.ok'
              : 'rdp.state.missing';
  badge.className = `badge ${cls}`;
  badge.innerHTML = `<span class="badge-dot"></span>${esc(t(key))}`;

  document.getElementById('rdp-install-dir').textContent = st.installDir     || '—';
  document.getElementById('rdp-bundle').textContent      = st.bundleVersion  || '—';
  document.getElementById('rdp-termsrv').textContent     = st.termsrvVersion || '—';
  document.getElementById('rdp-service-dll').textContent = st.serviceDllPath || '—';

  // Habilitar/deshabilitar botones según estado
  const busy = !!st.busy;
  document.getElementById('rdp-btn-install').disabled   = busy;
  document.getElementById('rdp-btn-update').disabled    = busy || !st.installed;
  document.getElementById('rdp-btn-uninstall').disabled = busy || !st.serviceDllPath?.toLowerCase().includes('rdpwrap');
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
  if (!confirm(t('rdp.confirm.install'))) return;
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
