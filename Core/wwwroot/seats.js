'use strict';

// ═══════════════════════════════════════════════════════════════════════════
//  PUESTOS LOCALES (seats) — pestaña del dashboard
// ═══════════════════════════════════════════════════════════════════════════

let seats         = [];
let seatInventory = { monitors: [], audioOutputs: [], devices: [] };
let seatTimer     = null;
let activeTab     = 'sessions';
let editingSeat   = null;   // puesto abierto en el modal (null = nuevo)
let identifyTimer = null;   // consulta de qué dispositivo se está pulsando (con el modal abierto)

const SEAT_KINDS = ['Keyboard', 'Mouse', 'Gamepad'];

function switchTab(tab) {
  activeTab = tab;
  document.getElementById('tab-sessions').classList.toggle('active', tab === 'sessions');
  document.getElementById('tab-seats').classList.toggle('active', tab === 'seats');
  document.getElementById('view-sessions').classList.toggle('hidden', tab !== 'sessions');
  document.getElementById('view-seats').classList.toggle('hidden', tab !== 'seats');
  // La pestaña va en la URL para que recargar o compartir el enlace no la pierda.
  history.replaceState(null, '', tab === 'seats' ? '#seats' : location.pathname + location.search);
  if (tab === 'seats') {
    loadSeats();
    if (!seatTimer) seatTimer = setInterval(whenVisible(loadSeats), 3000);
  } else {
    clearInterval(seatTimer);
    seatTimer = null;
  }
}

document.addEventListener('DOMContentLoaded', () => { if (location.hash === '#seats') switchTab('seats'); });
document.addEventListener('visibilitychange', () => { if (!document.hidden && seatTimer) loadSeats(); });

async function loadSeats() {
  const res = await api('GET', '/api/seats');
  if (!res) return;
  seats = await res.json();
  renderSeats();
}

function renderSeats() {
  const grid = document.getElementById('seats-grid');
  document.getElementById('seat-count').textContent = seats.length;
  if (seats.length === 0) {
    grid.innerHTML = `
      <div class="empty-state">
        <p>${esc(t('seat.empty'))}</p>
        <p>${t('seat.empty_hint')}</p>
      </div>`;
    return;
  }
  grid.innerHTML = seats.map(renderSeatCard).join('');
}

function renderSeatCard(s) {
  const running  = s.state === 'Running';
  const starting = s.state === 'Starting';
  const session  = s.sessionId ? sessions.find(x => x.id === s.sessionId) : null;

  let warning = '';
  if (!s.sessionId)   warning = t('seat.needSession');
  else if (!session && sessions.length) warning = t('seat.sessionMissing');

  const startBtn = starting
    ? `<button class="btn btn-ghost btn-sm" disabled><span class="spinner"></span> ${esc(t('btn.starting'))}</button>`
    : running
    ? `<button class="btn btn-danger btn-sm" onclick="seatAction('${s.id}','stop')">${esc(t('seat.stop'))}</button>`
    : `<button class="btn btn-success btn-sm" onclick="seatAction('${s.id}','start')" ${s.sessionId ? '' : 'disabled'}>${esc(t('seat.start'))}</button>`;

  const locked = running || starting;
  const editBtn = `<button class="btn btn-ghost btn-sm" onclick="openSeatModal('${s.id}')" ${locked ? 'disabled' : ''}
      title="${esc(t(locked ? 'seat.editLocked' : 'btn.settings_title'))}">${esc(t('btn.settings'))}</button>`;
  const deleteBtn = locked ? '' :
    `<button class="btn btn-ghost btn-sm" onclick="deleteSeat('${s.id}','${esc(s.name)}')" title="${esc(t('btn.delete'))}">✕</button>`;

  const monitor = s.monitor
    ? `${esc(s.monitor.friendlyName)} · ${esc(seatModeLabel(s.monitor))}`
    : esc(t('seat.noMonitorShort'));

  return `
    <div class="session-card" id="seat-${s.id}">
      <div class="card-header">
        <div>
          <div class="card-name">${esc(s.name)}</div>
          <div class="card-user">${session
              ? `${esc(t('seat.sessionPrefix'))} <strong>${esc(session.name)}</strong> · ${esc(t('state.' + session.state))}`
              : esc(t('seat.noSessionShort'))}${s.exclusiveSession ? ` <span class="seat-tag" title="${esc(t('seat.exclusive'))}">${esc(t('seat.exclusiveTag'))}</span>` : ''}</div>
          <div class="card-meta">${monitor}</div>
          <div class="card-meta">${esc(s.audioOutName || t('seat.noAudioShort'))}</div>
          <div class="card-meta">${esc(seatDevicesSummary(s.devices))}</div>
        </div>
        ${seatBadge(s.state)}
      </div>
      ${(starting || s.state === 'Stopped') && s.statusDetail ? `<div class="card-detail">${esc(s.statusDetail)}</div>` : ''}
      ${warning ? `<div class="card-warning">${esc(warning)}</div>` : ''}
      ${s.errorMessage ? `<div class="card-error">${esc(s.errorMessage)}</div>` : ''}
      <div class="card-footer">
        <label class="toggle" title="${esc(t('seat.autostart'))}">
          <input type="checkbox" ${s.autoStart ? 'checked' : ''} onchange="setSeatAutoStart('${s.id}', this.checked)">
          <span class="toggle-track"></span>
          ${esc(t('card.autostart'))}
        </label>
        <div class="card-actions">
          ${startBtn}
          ${editBtn}
          ${deleteBtn}
        </div>
      </div>
    </div>`;
}

function seatModeLabel(m) {
  const res = m.width > 0 && m.height > 0 ? `${m.width}×${m.height}` : t('seat.modeAuto');
  return m.refreshHz > 0 ? `${res} @ ${m.refreshHz} Hz` : res;
}

function seatDevicesSummary(devices) {
  if (!devices.length) return t('seat.noDevicesShort');
  return SEAT_KINDS
    .map(k => [k, devices.filter(d => d.kind === k).length])
    .filter(([, n]) => n > 0)
    .map(([k, n]) => n > 1 ? `${t('seat.kind.' + k)} ×${n}` : t('seat.kind.' + k))
    .join(' · ');
}

function seatBadge(state) {
  const cls = { Running: 'badge-running', Starting: 'badge-starting', Error: 'badge-error' }[state] || 'badge-stopped';
  return `<span class="badge ${cls}"><span class="badge-dot"></span>${esc(t('seat.state.' + state))}</span>`;
}

async function seatAction(id, action) {
  const res = await api('POST', `/api/seats/${id}/${action}`);
  if (!res) return;
  if (!res.ok) { toast(await res.text() || t('toast.connectionError'), true); return; }
  loadSeats();
}

async function setSeatAutoStart(id, enabled) {
  const res = await api('PATCH', `/api/seats/${id}/autostart`, { enabled });
  if (!res || !res.ok) { toast((res && await res.text()) || t('toast.autostartError'), true); loadSeats(); }
  else toast(enabled ? t('toast.autostartOn') : t('toast.autostartOff'));
}

async function deleteSeat(id, name) {
  if (!confirm(t('seat.confirmDelete', { name }))) return;
  const res = await api('DELETE', `/api/seats/${id}`);
  if (!res) return;
  if (!res.ok && res.status !== 204) { toast(t('toast.deleteError'), true); return; }
  toast(t('toast.deleted', { name }));
  loadSeats();
}

// ── Modal nuevo / editar ───────────────────────────────────────────────────

async function openSeatModal(id) {
  editingSeat = id ? seats.find(s => s.id === id) : null;
  const seat = editingSeat;
  if (!sessions.length) await loadSessions();
  await loadSeatInventory();

  document.getElementById('seat-modal-title').textContent = t(seat ? 'seat.editTitle' : 'seat.newTitle');
  document.getElementById('seat-error').style.display = 'none';
  const f = document.getElementById('seat-form');
  f.Id.value = seat ? seat.id : '';
  f.Name.value = seat ? seat.name : '';
  f.ExclusiveSession.checked = seat ? seat.exclusiveSession : true;
  f.AutoStart.checked = seat ? seat.autoStart : true;
  f.Width.value  = seat && seat.monitor && seat.monitor.width  ? seat.monitor.width  : '';
  f.Height.value = seat && seat.monitor && seat.monitor.height ? seat.monitor.height : '';
  f.RefreshHz.value = seat && seat.monitor && seat.monitor.refreshHz ? seat.monitor.refreshHz : '';

  // La consola (sesión local) no puede ser la de un puesto; las ya usadas por otro puesto se marcan.
  const owner = sid => seats.find(o => o.sessionId === sid && (!seat || o.id !== seat.id));
  fillSelect('seat-session', [{ v: '', label: t('seat.noSession') },
    ...sessions.filter(s => !s.local).map(s => {
      const o = owner(s.id);
      return { v: s.id, label: o ? `${s.name} (${t('seat.assignedTo', { name: o.name })})` : s.name, disabled: !!o };
    })], seat ? (seat.sessionId || '') : '');

  renderSeatInventory({
    monitor: seat && seat.monitor ? seat.monitor.devicePath : '',
    audio:   seat ? (seat.audioOutId || '') : '',
    devices: new Set((seat ? seat.devices : []).map(d => d.usbPort)),
  });
  show('seat-modal');
  f.Name.focus();
  startIdentify();
}

// ── Identificar el hardware ────────────────────────────────────────────────

function updateSeatTestButtons() {
  const f = document.getElementById('seat-form');
  document.getElementById('seat-monitor-test').disabled = !f.Monitor.value;
  document.getElementById('seat-audio-test').disabled = !f.AudioOut.value;
}

async function testSeatMonitor() {
  const id = document.getElementById('seat-form').Monitor.value;
  if (!id) return;
  const res = await api('POST', '/api/seats/identify/monitor', { id });
  if (!res) return;
  if (!res.ok) { toast(await res.text() || t('toast.connectionError'), true); return; }
  toast(t('seat.testingMonitor'));
}

async function testSeatAudio() {
  const id = document.getElementById('seat-form').AudioOut.value;
  if (!id) return;
  const res = await api('POST', '/api/seats/identify/audio', { id });
  if (!res) return;
  if (!res.ok) { toast(await res.text() || t('toast.connectionError'), true); return; }
  toast(t('seat.testingAudio'));
}

// Mientras el modal está abierto, el nombre del dispositivo que se pulsa se ilumina.
function startIdentify() {
  stopIdentify();
  let busy = false;
  identifyTimer = setInterval(async () => {
    if (busy || document.hidden) return;
    if (document.getElementById('seat-modal').classList.contains('hidden')) { stopIdentify(); return; }
    busy = true;
    try {
      const res = await api('GET', '/api/seats/identify/input');
      if (!res || !res.ok) return;
      const active = new Set(await res.json());
      document.querySelectorAll('#seat-devices .dev-check').forEach(el =>
        el.classList.toggle('dev-active', active.has(el.querySelector('input').value)));
    } finally { busy = false; }
  }, 250);
}

function stopIdentify() {
  clearInterval(identifyTimer);
  identifyTimer = null;
}

async function loadSeatInventory() {
  const res = await api('GET', '/api/seats/inventory' + (editingSeat ? `?seatId=${editingSeat.id}` : ''));
  seatInventory = res && res.ok ? await res.json() : { monitors: [], audioOutputs: [], devices: [] };
}

// Vuelve a buscar monitores y dispositivos (p. ej. tras enchufar un teclado) sin perder lo elegido.
async function refreshSeatInventory() {
  const f = document.getElementById('seat-form');
  const keep = {
    monitor: f.Monitor.value,
    audio:   f.AudioOut.value,
    devices: new Set([...document.querySelectorAll('#seat-devices input:checked')].map(c => c.value)),
  };
  const btn = document.getElementById('seat-refresh');
  btn.disabled = true;
  await loadSeatInventory();
  renderSeatInventory(keep);
  btn.disabled = false;
}

function renderSeatInventory(sel) {
  const seat = editingSeat;

  // Monitores: el inventario omite los de puestos encendidos; se añade el propio aunque no esté.
  const mons = [...seatInventory.monitors];
  if (seat && seat.monitor && !mons.some(m => m.devicePath === seat.monitor.devicePath))
    mons.push({ devicePath: seat.monitor.devicePath, friendlyName: seat.monitor.friendlyName,
                width: seat.monitor.width, height: seat.monitor.height });
  const monOwner = path => seats.find(o => o.monitor && o.monitor.devicePath === path && (!seat || o.id !== seat.id));
  fillSelect('seat-monitor', [{ v: '', label: t('seat.noMonitor') },
    ...mons.map(m => {
      const o = monOwner(m.devicePath);
      const mode = m.active && m.width ? ` — ${m.width}×${m.height}` : '';
      return { v: m.devicePath, label: `${m.friendlyName}${mode}${o ? ` (${t('seat.assignedTo', { name: o.name })})` : ''}`,
               data: m, disabled: !!o };
    })], sel.monitor);
  updateSeatModeFields();

  const outs = [...seatInventory.audioOutputs];
  if (seat && seat.audioOutId && !outs.some(o => o.id === seat.audioOutId))
    outs.push({ id: seat.audioOutId, name: seat.audioOutName || seat.audioOutId });
  fillSelect('seat-audio', [{ v: '', label: t('seat.noAudio') },
    ...outs.map(o => ({ v: o.id, label: o.name }))], sel.audio);
  updateSeatTestButtons();

  // Dispositivos: los libres más los que ya tiene el puesto, agrupados por tipo.
  const devs = [...seatInventory.devices];
  for (const d of (seat ? seat.devices : []))
    if (!devs.some(x => x.usbPort === d.usbPort))
      devs.push({ kind: d.kind, usbPort: d.usbPort, name: d.label, missing: true });
  const box = document.getElementById('seat-devices');
  if (!devs.length) {
    box.innerHTML = `<span class="muted">${esc(t('seat.noDevices'))}</span>`;
  } else {
    box.innerHTML = SEAT_KINDS.map(kind => {
      const group = devs.filter(d => d.kind === kind);
      if (!group.length) return '';
      return `<div class="dev-group">
          <div class="dev-group-title">${esc(t('seat.kind.' + kind))}</div>
          ${group.map(d => `<label class="dev-check" title="${esc(d.usbPort)}">
              <input type="checkbox" value="${esc(d.usbPort)}" data-kind="${esc(d.kind)}" data-name="${esc(d.name)}"
                     ${sel.devices.has(d.usbPort) ? 'checked' : ''} onchange="updateSeatInputHint()">
              <span>${esc(d.name)}</span>
              ${d.missing ? `<span class="dev-kind">${esc(t('seat.deviceMissing'))}</span>` : ''}
            </label>`).join('')}
        </div>`;
    }).join('');
  }
  updateSeatInputHint();
}

// El modo del monitor elegido (si está encendido) se sugiere como placeholder: vacío = ese.
function updateSeatModeFields() {
  const f = document.getElementById('seat-form');
  const opt = f.Monitor.selectedOptions[0];
  const mon = opt && opt.dataset.info ? JSON.parse(opt.dataset.info) : null;
  document.getElementById('seat-mode').classList.toggle('hidden', !mon);
  updateSeatTestButtons();
  f.Width.placeholder  = mon && mon.width  ? mon.width  : t('seat.modeAuto');
  f.Height.placeholder = mon && mon.height ? mon.height : t('seat.modeAuto');
}

function updateSeatInputHint() {
  const kinds = new Set([...document.querySelectorAll('#seat-devices input:checked')].map(c => c.dataset.kind));
  const warn = !kinds.has('Keyboard') && !kinds.has('Mouse') && !kinds.has('Gamepad');
  document.getElementById('seat-input-warning').classList.toggle('hidden', !warn);
}

function fillSelect(selId, options, selected) {
  const el = document.getElementById(selId);
  el.innerHTML = '';
  for (const o of options) {
    const opt = document.createElement('option');
    opt.value = o.v;
    opt.textContent = o.label;
    if (o.data) opt.dataset.info = JSON.stringify(o.data);
    if (o.disabled && o.v !== selected) opt.disabled = true;
    if (o.v === selected) opt.selected = true;
    el.appendChild(opt);
  }
}

function closeSeatModal() { hide('seat-modal'); editingSeat = null; stopIdentify(); }

async function submitSeat(e) {
  e.preventDefault();
  const f = e.target;
  const err = document.getElementById('seat-error');
  const fail = msg => { err.textContent = msg; err.style.display = 'block'; };

  const monOpt = f.Monitor.selectedOptions[0];
  const mon = monOpt && monOpt.dataset.info ? JSON.parse(monOpt.dataset.info) : null;
  const num = el => parseInt(el.value, 10) || 0;
  const width = num(f.Width), height = num(f.Height), hz = num(f.RefreshHz);
  if ((width > 0) !== (height > 0)) { fail(t('seat.modeError')); return; }
  const audioOpt = f.AudioOut.selectedOptions[0];

  const body = {
    name: f.Name.value.trim(),
    sessionId: f.SessionId.value || null,
    // 0 = el modo que Windows dé al monitor al encenderlo (ver SeatManager.ResolveMode).
    monitor: mon ? { devicePath: mon.devicePath, friendlyName: mon.friendlyName,
                     width, height, refreshHz: hz } : null,
    audioOutId: f.AudioOut.value || null,
    audioOutName: f.AudioOut.value ? audioOpt.textContent : null,
    devices: [...document.querySelectorAll('#seat-devices input:checked')].map(c => ({
      kind: c.dataset.kind, usbPort: c.value, label: c.dataset.name })),
    exclusiveSession: f.ExclusiveSession.checked,
    autoStart: f.AutoStart.checked,
  };

  const id = f.Id.value;
  const btn = f.querySelector('button[type=submit]');
  btn.disabled = true;
  const res = await api(id ? 'PUT' : 'POST', id ? `/api/seats/${id}` : '/api/seats', body);
  btn.disabled = false;
  if (!res) return;
  if (!res.ok) { fail(await res.text() || t('toast.connectionError')); return; }
  closeSeatModal();
  toast(t(id ? 'seat.saved' : 'seat.created', { name: body.name }));
  loadSeats();
}

window.addEventListener('langchange', () => { if (activeTab === 'seats') renderSeats(); });
