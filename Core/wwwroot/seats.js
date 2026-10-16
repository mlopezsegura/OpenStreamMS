'use strict';

// ═══════════════════════════════════════════════════════════════════════════
//  PUESTOS LOCALES (seats) — pestaña del dashboard
// ═══════════════════════════════════════════════════════════════════════════

let seats         = [];
let seatInventory = { monitors: [], audioOutputs: [], devices: [] };
let seatTimer     = null;
let activeTab     = 'sessions';

function switchTab(tab) {
  activeTab = tab;
  document.getElementById('tab-sessions').classList.toggle('active', tab === 'sessions');
  document.getElementById('tab-seats').classList.toggle('active', tab === 'seats');
  document.getElementById('view-sessions').classList.toggle('hidden', tab !== 'sessions');
  document.getElementById('view-seats').classList.toggle('hidden', tab !== 'seats');
  if (tab === 'seats') {
    loadSeats();
    if (!seatTimer) seatTimer = setInterval(whenVisible(loadSeats), 3000);
  } else {
    clearInterval(seatTimer);
    seatTimer = null;
  }
}

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
    grid.innerHTML = `<div class="empty-state"><p>${esc(t('seat.empty'))}</p></div>`;
    return;
  }
  grid.innerHTML = seats.map(renderSeatCard).join('');
}

function renderSeatCard(s) {
  const running = s.state === 'Running';
  const session = sessions.find(x => x.id === s.sessionId);
  const devices = s.devices.map(d => esc(d.label || d.usbPort)).join(', ') || '—';
  return `
    <div class="card">
      <div class="card-head">
        <strong>${esc(s.name)}</strong>
        ${seatBadge(s.state)}
      </div>
      <div class="card-body">
        <div class="card-row"><span>${t('seat.sessionLabel')}</span><span>${session ? esc(session.name) : '—'}</span></div>
        <div class="card-row"><span>${t('seat.monitorLabel')}</span><span>${s.monitor ? esc(s.monitor.friendlyName) : '—'}</span></div>
        <div class="card-row"><span>${t('seat.audioLabel')}</span><span>${s.audioOutName ? esc(s.audioOutName) : '—'}</span></div>
        <div class="card-row"><span>${t('seat.devicesLabel')}</span><span>${devices}</span></div>
        ${s.statusDetail ? `<div class="card-row card-detail"><span>${esc(s.statusDetail)}</span></div>` : ''}
        ${s.errorMessage ? `<div class="card-row card-error"><span>${esc(s.errorMessage)}</span></div>` : ''}
      </div>
      <div class="card-actions">
        ${running
          ? `<button class="btn btn-sm" onclick="seatAction('${s.id}','stop')">${esc(t('seat.stop'))}</button>`
          : `<button class="btn btn-sm btn-primary" onclick="seatAction('${s.id}','start')">${esc(t('seat.start'))}</button>`}
        <button class="btn btn-sm btn-ghost" onclick="openSeatModal('${s.id}')" ${running ? 'disabled' : ''}>${esc(t('btn.edit'))}</button>
        <button class="btn btn-sm btn-ghost" onclick="deleteSeat('${s.id}', ${JSON.stringify(s.name)})">${esc(t('btn.delete'))}</button>
      </div>
    </div>`;
}

function seatBadge(state) {
  const map = { Running: 'running', Starting: 'starting', Error: 'error', Stopped: 'stopped' };
  return `<span class="badge badge-${map[state] || 'stopped'}">${esc(t('seat.state.' + state) || state)}</span>`;
}

async function seatAction(id, action) {
  const res = await api('POST', `/api/seats/${id}/${action}`);
  if (!res) return;
  if (!res.ok) { toast(await res.text(), true); return; }
  loadSeats();
}

async function deleteSeat(id, name) {
  if (!confirm(t('seat.confirmDelete', { name }))) return;
  const res = await api('DELETE', `/api/seats/${id}`);
  if (res && (res.ok || res.status === 204)) loadSeats();
}

async function openSeatModal(id) {
  const invRes = await api('GET', '/api/seats/inventory');
  seatInventory = invRes ? await invRes.json() : { monitors: [], audioOutputs: [], devices: [] };
  if (!sessions.length) await loadSessions();

  const seat = id ? seats.find(s => s.id === id) : null;
  document.getElementById('seat-modal-title').textContent = t(seat ? 'seat.editTitle' : 'seat.newTitle');
  document.getElementById('seat-error').style.display = 'none';
  const f = document.getElementById('seat-form');
  f.Id.value = seat ? seat.id : '';
  f.Name.value = seat ? seat.name : '';
  f.ExclusiveSession.checked = seat ? seat.exclusiveSession : true;
  f.AutoStart.checked = seat ? seat.autoStart : true;

  fillSelect('seat-session', [{ v: '', label: t('seat.noSession') },
    ...sessions.map(s => ({ v: s.id, label: s.name }))], seat ? (seat.sessionId || '') : '');

  const mons = [...seatInventory.monitors];
  if (seat && seat.monitor && !mons.some(m => m.devicePath === seat.monitor.devicePath))
    mons.push({ devicePath: seat.monitor.devicePath, friendlyName: seat.monitor.friendlyName,
                width: seat.monitor.width, height: seat.monitor.height, refreshHz: seat.monitor.refreshHz });
  fillSelect('seat-monitor', [{ v: '', label: t('seat.noMonitor') },
    ...mons.map(m => ({ v: m.devicePath, label: `${m.friendlyName}${m.width ? ` (${m.width}x${m.height})` : ''}`, data: m }))],
    seat && seat.monitor ? seat.monitor.devicePath : '');

  const outs = [...seatInventory.audioOutputs];
  if (seat && seat.audioOutId && !outs.some(o => o.id === seat.audioOutId))
    outs.push({ id: seat.audioOutId, name: seat.audioOutName || seat.audioOutId });
  fillSelect('seat-audio', [{ v: '', label: t('seat.noAudio') },
    ...outs.map(o => ({ v: o.id, label: o.name }))], seat ? (seat.audioOutId || '') : '');

  const devs = [...seatInventory.devices];
  for (const d of (seat ? seat.devices : []))
    if (!devs.some(x => x.usbPort === d.usbPort))
      devs.push({ kind: d.kind, usbPort: d.usbPort, name: d.label });
  const chosen = new Set((seat ? seat.devices : []).map(d => d.usbPort));
  document.getElementById('seat-devices').innerHTML = devs.length
    ? devs.map(d => `<label class="dev-check"><input type="checkbox" value="${esc(d.usbPort)}"
         data-kind="${d.kind}" data-name="${esc(d.name)}" ${chosen.has(d.usbPort) ? 'checked' : ''}>
         <span class="dev-kind">${esc(t('seat.kind.' + d.kind) || d.kind)}</span> ${esc(d.name)}</label>`).join('')
    : `<span class="muted">${esc(t('seat.noDevices'))}</span>`;

  show('seat-modal');
}

function fillSelect(selId, options, selected) {
  const el = document.getElementById(selId);
  el.innerHTML = '';
  for (const o of options) {
    const opt = document.createElement('option');
    opt.value = o.v;
    opt.textContent = o.label;
    if (o.data) opt.dataset.info = JSON.stringify(o.data);
    if (o.v === selected) opt.selected = true;
    el.appendChild(opt);
  }
}

function closeSeatModal() { hide('seat-modal'); }

async function submitSeat(e) {
  e.preventDefault();
  const f = e.target;
  const monOpt = f.Monitor.selectedOptions[0];
  const mon = monOpt && monOpt.dataset.info ? JSON.parse(monOpt.dataset.info) : null;
  const audioOpt = f.AudioOut.selectedOptions[0];

  const body = {
    name: f.Name.value.trim(),
    sessionId: f.SessionId.value || null,
    monitor: mon ? { devicePath: mon.devicePath, friendlyName: mon.friendlyName,
                     width: mon.width || 0, height: mon.height || 0, refreshHz: mon.refreshHz || 0 } : null,
    audioOutId: f.AudioOut.value || null,
    audioOutName: f.AudioOut.value ? audioOpt.textContent : null,
    devices: [...document.querySelectorAll('#seat-devices input:checked')].map(c => ({
      kind: c.getAttribute('data-kind'), usbPort: c.value, label: c.getAttribute('data-name') })),
    exclusiveSession: f.ExclusiveSession.checked,
    autoStart: f.AutoStart.checked,
  };

  const id = f.Id.value;
  const res = await api(id ? 'PUT' : 'POST', id ? `/api/seats/${id}` : '/api/seats', body);
  if (!res) return;
  if (!res.ok) {
    const err = document.getElementById('seat-error');
    err.textContent = await res.text();
    err.style.display = 'block';
    return;
  }
  closeSeatModal();
  loadSeats();
}

window.addEventListener('langchange', () => { if (activeTab === 'seats') renderSeats(); });
