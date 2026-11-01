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

// Pantalla dividida: el monitor es una cuadrícula de 2×2 (igual que SeatAreas en el servicio).
// Mitades en vertical (izquierda/derecha) u horizontal (arriba/abajo), y cuartos.
const SEAT_SPLITS = {
  Vertical:   ['Left', 'Right'],
  Horizontal: ['Top', 'Bottom'],
  Quarters:   ['TopLeft', 'TopRight', 'BottomLeft', 'BottomRight'],
};
const SEAT_AREAS = ['Full', ...SEAT_SPLITS.Vertical, ...SEAT_SPLITS.Horizontal, ...SEAT_SPLITS.Quarters];

// Celdas: 1 = arriba-izquierda, 2 = arriba-derecha, 4 = abajo-izquierda, 8 = abajo-derecha.
const SEAT_CELLS = {
  Full: 0b1111, Left: 0b0101, Right: 0b1010, Top: 0b0011, Bottom: 0b1100,
  TopLeft: 0b0001, TopRight: 0b0010, BottomLeft: 0b0100, BottomRight: 0b1000,
};
const seatCells = a => SEAT_CELLS[a || 'Full'];

// Igual que SeatMonitor.Overlaps: se pisan si comparten alguna celda.
function seatAreasOverlap(a, b) { return (seatCells(a) & seatCells(b)) !== 0; }

// Igual que SeatAreas.Cover: cubre lo libre con las partes más grandes primero.
function coverFree(free) {
  const parts = [];
  for (const a of SEAT_AREAS) {
    const c = SEAT_CELLS[a];
    if ((free & c) === c) { parts.push(a); free &= ~c; }
  }
  return parts;
}

// Tamaño de la zona dentro de un monitor de w×h (igual que SeatMonitor.Region).
function seatAreaSize(area, w, h) {
  const c = seatCells(area), w1 = Math.floor(w / 2), h1 = Math.floor(h / 2);
  const l = (c & 0b0101) !== 0, r = (c & 0b1010) !== 0, t = (c & 0b0011) !== 0, b = (c & 0b1100) !== 0;
  return [l && r ? w : l ? w1 : w - w1, t && b ? h : t ? h1 : h - h1];
}

// Otros puestos con ese monitor (no el que se edita).
function seatMonitorOwners(path) {
  return seats.filter(o => o.monitor && o.monitor.devicePath === path && (!editingSeat || o.id !== editingSeat.id));
}

// ── Piezas gráficas ────────────────────────────────────────────────────────

// Iconos de trazo (heredan el color del texto).
const SEAT_ICON_PATHS = {
  Monitor:  '<rect x="2" y="3" width="20" height="13" rx="2"/><path d="M8 21h8M12 16v5"/>',
  Keyboard: '<rect x="2" y="6" width="20" height="12" rx="2"/><path d="M6 10h.01M10 10h.01M14 10h.01M18 10h.01M7 14h10"/>',
  Mouse:    '<rect x="6" y="2" width="12" height="20" rx="6"/><path d="M12 6v4"/>',
  Gamepad:  '<path d="M6 11h4M8 9v4M15 12h.01M18 10h.01"/><path d="M17.3 5H6.7a4 4 0 0 0-3.9 3.2l-1 5.2A3 3 0 0 0 7 16.8L8.6 15h6.8l1.6 1.8a3 3 0 0 0 5.2-3.4l-1-5.2A4 4 0 0 0 17.3 5z"/>',
  Audio:    '<path d="M11 5 6 9H2v6h4l5 4V5z"/><path d="M15.5 8.5a5 5 0 0 1 0 7M19 5a10 10 0 0 1 0 14"/>',
};

function seatIcon(name) {
  return `<svg class="seat-icon" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"
              stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">${SEAT_ICON_PATHS[name]}</svg>`;
}

// Monitor en miniatura con la zona del puesto resaltada (la posición la pone el CSS por data-area).
function screenGlyph(area, extra = '') {
  return `<span class="screen-glyph ${extra}" data-area="${esc(area || 'Full')}"><span class="screen-zone"></span></span>`;
}

// ── Hardware: arrastrar y soltar ───────────────────────────────────────────
//
// Cada puesto dibuja su hardware (teclados, ratones, mandos y salida de audio) y la bandeja
// "sin asignar" el que está libre. Arrastrar una ficha la asigna, la mueve entre puestos o la
// libera. Solo con el puesto apagado: el servicio no deja editar uno encendido.

let hwFree = { devices: [], audioOutputs: [] };   // libre según el inventario
let hwLoaded = false;
let hwDragging = false;
let hwActivePorts = new Set();   // puertos USB que se acaban de pulsar (se iluminan)

// Una ficha: kind = Keyboard | Mouse | Gamepad | Audio; id = puerto USB o id de la salida de audio.
function hwChip(kind, id, label, from, draggable) {
  const active = kind !== 'Audio' && hwActivePorts.has(id) ? ' hw-active' : '';
  return `
    <span class="hw-chip hw-${kind.toLowerCase()}${active}" ${draggable ? 'draggable="true" ondragstart="hwDragStart(event)" ondragend="hwDragEnd(event)"' : ''}
          data-kind="${kind}" data-id="${esc(id)}" data-label="${esc(label)}" data-from="${from}" data-port="${kind === 'Audio' ? '' : esc(id)}"
          title="${esc(t(kind === 'Audio' ? 'seat.audioLabel' : 'seat.kind.' + kind))}: ${esc(label)}">
      ${seatIcon(kind)}<span class="hw-label">${esc(label)}</span>
    </span>`;
}

function renderSeatHardware(s, locked) {
  const chips = [
    ...(s.audioOutId ? [hwChip('Audio', s.audioOutId, s.audioOutName || s.audioOutId, s.id, !locked)] : []),
    ...SEAT_KINDS.flatMap(k => s.devices.filter(d => d.kind === k).map(d => hwChip(k, d.usbPort, d.label, s.id, !locked))),
  ];
  return `
    <div class="hw-drop ${locked ? 'hw-locked' : ''}" data-drop="${s.id}"
         ${locked ? `title="${esc(t('seat.editLocked'))}"` : 'ondragover="hwDragOver(event)" ondragleave="hwDragLeave(event)" ondrop="hwDrop(event)"'}>
      ${chips.length ? chips.join('') : `<span class="hw-empty">${esc(t(locked ? 'seat.noDevicesShort' : 'seat.hwDropHere'))}</span>`}
    </div>`;
}

function renderHardwarePool() {
  const box = document.getElementById('seat-hw-pool');
  if (!hwLoaded) { box.classList.add('hidden'); return; }
  const usedAudio = new Set(seats.map(s => s.audioOutId).filter(Boolean));
  const chips = [
    ...SEAT_KINDS.flatMap(k => hwFree.devices.filter(d => d.kind === k).map(d => hwChip(k, d.usbPort, d.name, '', true))),
    ...hwFree.audioOutputs.filter(o => !usedAudio.has(o.id)).map(o => hwChip('Audio', o.id, o.name, '', true)),
  ];
  box.classList.remove('hidden');
  renderInto(box, `
    <div class="hw-pool-head">
      <span class="monitor-map-title">${esc(t('seat.hwPoolTitle'))}</span>
      <span class="hint">${esc(t('seat.hwPoolHint'))}</span>
    </div>
    <div class="hw-drop hw-pool-drop" data-drop="" ondragover="hwDragOver(event)" ondragleave="hwDragLeave(event)" ondrop="hwDrop(event)">
      ${chips.length ? chips.join('') : `<span class="hw-empty">${esc(t('seat.hwPoolEmpty'))}</span>`}
    </div>`);
}

function hwDragStart(e) {
  const c = e.currentTarget.dataset;
  e.dataTransfer.setData('application/x-osms-hw', JSON.stringify({ kind: c.kind, id: c.id, label: c.label, from: c.from }));
  e.dataTransfer.effectAllowed = 'move';
  hwDragging = true;
  e.currentTarget.classList.add('hw-dragging');
  document.getElementById('view-seats').classList.add('hw-drag-on');
}

function hwDragEnd(e) {
  hwDragging = false;
  e.currentTarget.classList.remove('hw-dragging');
  document.getElementById('view-seats').classList.remove('hw-drag-on');
  document.querySelectorAll('.hw-drop.hw-over').forEach(el => el.classList.remove('hw-over'));
}

function hwDragOver(e) {
  if (!e.dataTransfer.types.includes('application/x-osms-hw')) return;
  e.preventDefault();
  e.dataTransfer.dropEffect = 'move';
  e.currentTarget.classList.add('hw-over');
}

function hwDragLeave(e) {
  if (!e.currentTarget.contains(e.relatedTarget)) e.currentTarget.classList.remove('hw-over');
}

async function hwDrop(e) {
  e.preventDefault();
  e.currentTarget.classList.remove('hw-over');
  hwDragging = false;
  let item;
  try { item = JSON.parse(e.dataTransfer.getData('application/x-osms-hw')); } catch { return; }
  const to = e.currentTarget.dataset.drop;
  if (item.from === to) return;

  // Primero se quita del puesto de origen: el servicio no deja un puerto USB en dos puestos.
  const source = item.from ? seats.find(s => s.id === item.from) : null;
  const target = to ? seats.find(s => s.id === to) : null;
  if (source && !await saveSeatHardware(source, hwWithout(source, item))) { loadSeats(); return; }
  if (target && !await saveSeatHardware(target, hwWith(target, item))) {
    // Si no entra en el destino, vuelve a su sitio.
    if (source) await saveSeatHardware(source, hwWith(hwWithout(source, item), item));
    loadSeats(); return;
  }
  toast(t(target ? 'seat.hwAssigned' : 'seat.hwReleased', { device: item.label, name: (target || source).name }));
  await loadSeats();
  loadMonitorMap();
}

// Copias del puesto con o sin esa ficha (el audio se sustituye: hay una salida por puesto).
function hwWithout(s, item) {
  return item.kind === 'Audio'
    ? { ...s, audioOutId: s.audioOutId === item.id ? null : s.audioOutId, audioOutName: s.audioOutId === item.id ? null : s.audioOutName }
    : { ...s, devices: s.devices.filter(d => d.usbPort !== item.id) };
}

function hwWith(s, item) {
  return item.kind === 'Audio'
    ? { ...s, audioOutId: item.id, audioOutName: item.label }
    : { ...s, devices: [...s.devices.filter(d => d.usbPort !== item.id), { kind: item.kind, usbPort: item.id, label: item.label }] };
}

// El mismo cuerpo que el editor (PUT completo del puesto).
async function saveSeatHardware(original, s) {
  const res = await api('PUT', `/api/seats/${original.id}`, {
    name: s.name, sessionId: s.sessionId, monitor: s.monitor,
    audioOutId: s.audioOutId, audioOutName: s.audioOutName,
    devices: s.devices, exclusiveSession: s.exclusiveSession, autoStart: s.autoStart,
  });
  if (res && res.ok) { Object.assign(original, s); return true; }
  toast((res && await res.text()) || t('toast.connectionError'), true);
  return false;
}

// Con la pestaña abierta, el dispositivo que se pulsa se ilumina (en las fichas y en el editor).
let hwIdentifyTimer = null;
function startHardwareIdentify() {
  if (hwIdentifyTimer) return;
  let busy = false;
  hwIdentifyTimer = setInterval(async () => {
    if (busy || document.hidden || activeTab !== 'seats' || identifyTimer) return;   // el editor tiene su propia escucha
    busy = true;
    try {
      const res = await api('GET', '/api/seats/identify/input');
      if (!res || !res.ok) return;
      hwActivePorts = new Set(await res.json());
      document.querySelectorAll('#view-seats .hw-chip[data-port]').forEach(el =>
        el.classList.toggle('hw-active', !!el.dataset.port && hwActivePorts.has(el.dataset.port)));
    } finally { busy = false; }
  }, 400);
}

function stopHardwareIdentify() { clearInterval(hwIdentifyTimer); hwIdentifyTimer = null; }

// ── Mapa de monitores ──────────────────────────────────────────────────────

let mapMonitors = [];   // inventario para el mapa: tamaño de cada monitor y cuál es el de la consola

// El inventario pregunta a la consola por los monitores: se pide al abrir la pestaña y tras
// guardar o borrar, no en cada refresco de 3 s.
async function loadMonitorMap() {
  const res = await api('GET', '/api/seats/inventory');
  if (res && res.ok) {
    const inv = await res.json();
    hwFree = { devices: inv.devices || [], audioOutputs: inv.audioOutputs || [] };
    hwLoaded = true;
    // El inventario omite los monitores en uso entero por un puesto encendido: se conserva lo
    // que ya se sabía de ellos (su tamaño) para que el mapa no cambie de forma.
    const fresh = inv.monitors || [];
    const paths = new Set(fresh.map(m => m.devicePath));
    mapMonitors = [...fresh, ...mapMonitors.filter(m => !paths.has(m.devicePath) && seatsOn(m.devicePath).length)];
  }
  renderMonitorMap();
  renderHardwarePool();
}

function seatsOn(path) {
  return seats.filter(s => s.monitor && s.monitor.devicePath === path);
}

function renderMonitorMap() {
  const box = document.getElementById('seat-monitor-map');
  const mons = new Map(mapMonitors.map(m => [m.devicePath, m]));
  for (const s of seats)
    if (s.monitor && !mons.has(s.monitor.devicePath))
      mons.set(s.monitor.devicePath, { devicePath: s.monitor.devicePath, friendlyName: s.monitor.friendlyName });
  box.classList.toggle('hidden', mons.size === 0);
  // Primero el de la consola, después los que tienen puestos.
  const list = [...mons.values()].sort((a, b) =>
    (!!b.primary - !!a.primary) || (seatsOn(b.devicePath).length - seatsOn(a.devicePath).length));
  renderInto(box, `
    <div class="monitor-map-title">${esc(t('seat.mapTitle'))}</div>
    <div class="monitor-map-list">${list.map(renderMonitorTile).join('')}</div>`);
}

// El monitor como cuadrícula de 2×2: cada puesto en sus celdas y lo libre cubierto con las
// partes más grandes. En el principal, la primera parte libre es de la consola (como en el
// servicio) y el resto queda libre.
function renderMonitorTile(m) {
  const on = seatsOn(m.devicePath);
  let free = 0b1111;
  for (const s of on) free &= ~seatCells(s.monitor.area);
  const freeParts = coverFree(free);
  const zones = [
    ...on.map(s => renderMonitorZone(m, s.monitor.area || 'Full', s, false)),
    ...freeParts.map((a, i) => renderMonitorZone(m, a, null, m.primary && i === 0)),
  ];
  const ratio = m.width > 0 && m.height > 0 ? `${m.width} / ${m.height}` : '16 / 9';
  return `
    <div class="monitor-tile">
      <div class="monitor-screen" style="aspect-ratio:${ratio}">${zones.join('')}</div>
      <div class="monitor-stand"></div>
      <div class="monitor-name">${esc(m.friendlyName)}</div>
      <div class="monitor-meta">${m.width > 0 ? `${m.width}×${m.height}` : '&nbsp;'}${m.primary
          ? ` <span class="seat-tag">${esc(t('seat.primaryTag'))}</span>` : ''}</div>
    </div>`;
}

// Zona con puesto: abre su configuración (o lleva a su tarjeta si está encendido).
// Zona libre: crea un puesto ya colocado ahí. En el principal, lo libre es de la consola.
function renderMonitorZone(m, area, seat, isConsole) {
  if (seat) {
    const locked = seat.state === 'Running' || seat.state === 'Starting';
    return `
      <button type="button" class="zone zone-seat zone-${seat.state.toLowerCase()}" data-seat="${seat.id}" data-area="${area}"
              onclick="openMapZone(this)" title="${esc(t(locked ? 'seat.editLocked' : 'btn.settings_title'))}">
        <span class="zone-name">${esc(seat.name)}</span>
        <span class="zone-state"><span class="badge-dot"></span>${esc(t('seat.state.' + seat.state))}</span>
      </button>`;
  }
  return `
    <button type="button" class="zone ${isConsole ? 'zone-console' : 'zone-free'}"
            data-monitor="${esc(m.devicePath)}" data-area="${area}" onclick="openMapZone(this)">
      <span class="zone-name">${esc(t(isConsole ? 'seat.console' : 'seat.free'))}</span>
      <span class="zone-add">+ ${esc(t('seat.addHere'))}</span>
    </button>`;
}

function openMapZone(el) {
  const id = el.dataset.seat;
  if (!id) { openSeatModal(null, { monitor: el.dataset.monitor, area: el.dataset.area }); return; }
  const s = seats.find(x => x.id === id);
  if (s && (s.state === 'Running' || s.state === 'Starting')) {
    const card = document.getElementById('seat-' + id);
    if (card) { card.scrollIntoView({ behavior: 'smooth', block: 'center' }); card.classList.add('card-flash');
                setTimeout(() => card.classList.remove('card-flash'), 1200); }
    return;
  }
  openSeatModal(id);
}

function switchTab(tab) {
  activeTab = tab;
  document.getElementById('tab-sessions').classList.toggle('active', tab === 'sessions');
  document.getElementById('tab-seats').classList.toggle('active', tab === 'seats');
  document.getElementById('view-sessions').classList.toggle('hidden', tab !== 'sessions');
  document.getElementById('view-seats').classList.toggle('hidden', tab !== 'seats');
  // La pestaña va en la URL para que recargar o compartir el enlace no la pierda.
  history.replaceState(null, '', tab === 'seats' ? '#seats' : location.pathname + location.search);
  moveTabIndicator();
  if (tab === 'seats') {
    loadSeats();
    loadMonitorMap();
    startHardwareIdentify();
    if (!seatTimer) seatTimer = setInterval(whenVisible(loadSeats), 3000);
  } else {
    clearInterval(seatTimer);
    seatTimer = null;
    stopHardwareIdentify();
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
  // Rehacer el HTML a mitad de un arrastre lo cancelaría: el refresco de 3 s espera.
  if (hwDragging) return;
  renderMonitorMap();
  renderHardwarePool();
  renderInto(grid, seats.length === 0
    ? `<div class="empty-state">
        <div class="empty-icon">${uiIcon('seat')}</div>
        <p>${esc(t('seat.empty'))}</p>
        <p>${t('seat.empty_hint')}</p>
      </div>`
    : seats.map(renderSeatCard).join(''));
}

function renderSeatCard(s) {
  const running  = s.state === 'Running';
  const starting = s.state === 'Starting';
  const session  = s.sessionId ? sessions.find(x => x.id === s.sessionId) : null;

  let warning = '';
  if (!s.sessionId)   warning = t('seat.needSession');
  else if (!session && sessions.length) warning = t('seat.sessionMissing');

  const locked = running || starting;
  const power = starting
    ? `<button class="power-btn busy" disabled title="${esc(t('btn.starting'))}">${uiIcon('power')}</button>`
    : `<button class="power-btn ${running ? 'on' : 'off'}" onclick="seatAction('${s.id}','${running ? 'stop' : 'start'}')"
               ${!running && !s.sessionId ? 'disabled' : ''} title="${esc(t(running ? 'seat.stop' : 'seat.start'))}">${uiIcon('power')}</button>`;
  const area = s.monitor && s.monitor.area && s.monitor.area !== 'Full' ? ` · ${esc(t('seat.area.' + s.monitor.area))}` : '';
  const monitor = s.monitor
    ? `${screenGlyph(s.monitor.area)}<span>${esc(s.monitor.friendlyName)}${area} · ${esc(seatModeLabel(s.monitor))}</span>`
    : `${seatIcon('Monitor')}<span>${esc(t('seat.noMonitorShort'))}</span>`;

  return `
    <div class="session-card state-${s.state.toLowerCase()}" id="seat-${s.id}">
      <div class="card-top">
        ${s.monitor ? screenGlyph(s.monitor.area, 'screen-glyph-card') : `<div class="screen-thumb screen-local">${uiIcon('screen')}</div>`}
        <div class="card-title">
          <div class="card-name">${esc(s.name)}</div>
          <div class="card-user">${session
              ? `${esc(t('seat.sessionPrefix'))} <strong>${esc(session.name)}</strong>`
              : esc(t('seat.noSessionShort'))}${s.exclusiveSession ? ` <span class="seat-tag" title="${esc(t('seat.exclusive'))}">${esc(t('seat.exclusiveTag'))}</span>` : ''}</div>
          ${seatBadge(s.state)}
        </div>
        ${power}
      </div>
      <div class="card-meta seat-line">${monitor}</div>
      ${renderSeatHardware(s, locked)}
      ${(starting || s.state === 'Stopped') && s.statusDetail ? `<div class="card-detail">${esc(s.statusDetail)}</div>` : ''}
      ${warning ? `<div class="card-warning">${esc(warning)}</div>` : ''}
      ${s.errorMessage ? `<div class="card-error">${esc(s.errorMessage)}</div>` : ''}
      <div class="card-footer">
        <label class="toggle" title="${esc(t('seat.autostart'))}">
          <input type="checkbox" ${s.autoStart ? 'checked' : ''} onchange="setSeatAutoStart('${s.id}', this.checked)">
          <span class="toggle-track"></span>
          ${esc(t('card.autostart'))}
        </label>
        <button class="icon-btn more-btn" onclick="seatMenu(event, '${s.id}')" title="${esc(t('nav.more'))}">${uiIcon('more')}</button>
      </div>
    </div>`;
}

function seatMenu(e, id) {
  const s = seats.find(x => x.id === id);
  if (!s) return;
  const locked = s.state === 'Running' || s.state === 'Starting';
  openMenu(e, [
    { icon: 'gear',  label: t('btn.settings'), run: () => openSeatModal(s.id), disabled: locked, hint: t('seat.editLocked') },
    { sep: true },
    { icon: 'trash', label: t('btn.delete'),   run: () => deleteSeat(s.id, s.name), danger: true, disabled: locked, hint: t('seat.editLocked') },
  ]);
}
function seatModeLabel(m) {
  const res = m.width > 0 && m.height > 0 ? `${m.width}×${m.height}` : t('seat.modeAuto');
  return m.refreshHz > 0 ? `${res} @ ${m.refreshHz} Hz` : res;
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
  setTimeout(loadMonitorMap, 4000);   // al encender o apagar, la consola puede ganar o perder su mitad
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
  loadMonitorMap();
}

// ── Modal nuevo / editar ───────────────────────────────────────────────────

// preset: { monitor, area } para un puesto nuevo creado desde una zona libre del mapa.
async function openSeatModal(id, preset) {
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
    monitor: seat && seat.monitor ? seat.monitor.devicePath : (preset && preset.monitor) || '',
    area:    seat && seat.monitor ? (seat.monitor.area || 'Full') : (preset && preset.area) || 'Full',
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
    area:    f.Area.value || 'Full',
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
  // Un monitor con dueño sigue disponible mientras le quede una mitad libre.
  fillSelect('seat-monitor', [{ v: '', label: t('seat.noMonitor') },
    ...mons.map(m => {
      const owners = seatMonitorOwners(m.devicePath);
      const full = !SEAT_AREAS.some(a => owners.every(o => !seatAreasOverlap(a, o.monitor.area || 'Full')));
      const mode = m.active && m.width ? ` — ${m.width}×${m.height}` : '';
      const names = owners.map(o => o.name).join(', ');
      const primary = m.primary ? ` · ${t('seat.primaryMonitor')}` : '';
      return { v: m.devicePath, label: `${m.friendlyName}${mode}${primary}${owners.length ? ` (${t('seat.assignedTo', { name: names })})` : ''}`,
               data: m, disabled: full };
    })], sel.monitor);
  updateSeatModeFields(sel.area);

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

function selectedSeatMonitor() {
  const opt = document.getElementById('seat-form').Monitor.selectedOptions[0];
  return opt && opt.dataset.info ? JSON.parse(opt.dataset.info) : null;
}

// Al cambiar de monitor: zonas que dejan libres sus otros puestos y modo sugerido.
function updateSeatModeFields(area) {
  const f = document.getElementById('seat-form');
  const mon = selectedSeatMonitor();
  document.getElementById('seat-mode').classList.toggle('hidden', !mon);
  document.getElementById('seat-area-row').classList.toggle('hidden', !mon);
  updateSeatTestButtons();

  const owners = mon ? seatMonitorOwners(mon.devicePath) : [];
  const ownerOf = a => owners.find(o => seatAreasOverlap(a, o.monitor.area || 'Full'));
  // El principal es de la consola: un puesto solo puede ocupar una mitad y ella se queda la otra.
  const primary = !!(mon && mon.primary);
  const hint = document.querySelector('#seat-area-row .hint');
  hint.dataset.i18n = primary ? 'seat.areaHintPrimary' : 'seat.areaHint';
  hint.textContent = t(hint.dataset.i18n);
  const sel = document.getElementById('seat-area');
  const wanted = area || sel.value || 'Full';
  sel.innerHTML = '';
  const add = (parent, a) => {
    const opt = document.createElement('option');
    const o = ownerOf(a);
    const consoleOnly = primary && a === 'Full';
    opt.value = a;
    opt.textContent = t('seat.area.' + a) + (o ? ` (${t('seat.assignedTo', { name: o.name })})` : consoleOnly ? ` (${t('seat.primaryMonitor')})` : '');
    opt.disabled = !!o || consoleOnly;
    opt.dataset.owner = o ? o.name : consoleOnly ? t('seat.console') : '';
    parent.appendChild(opt);
  };
  add(sel, 'Full');
  for (const [split, areas] of Object.entries(SEAT_SPLITS)) {
    const group = document.createElement('optgroup');
    group.label = t('seat.split.' + split);
    areas.forEach(a => add(group, a));
    sel.appendChild(group);
  }
  // Si la zona que tenía ya no está libre, la primera que lo esté.
  const free = [...sel.options].filter(o => !o.disabled).map(o => o.value);
  sel.value = free.includes(wanted) ? wanted : (free[0] || 'Full');
  renderAreaPicker();
  updateSeatModePlaceholders();
}

// Las zonas del select como miniaturas de monitor: completa, mitades verticales y horizontales.
function renderAreaPicker() {
  const sel = document.getElementById('seat-area');
  const tile = o => `
    <button type="button" class="area-tile ${o.value === sel.value ? 'selected' : ''}" role="radio"
            aria-checked="${o.value === sel.value}" ${o.disabled ? 'disabled' : ''}
            onclick="pickSeatArea('${o.value}')" title="${esc(o.textContent)}">
      ${screenGlyph(o.value, 'screen-glyph-lg')}
      <span class="area-label">${esc(t('seat.areaShort.' + o.value))}</span>
      <span class="area-owner">${o.disabled ? esc(o.dataset.owner) : '&nbsp;'}</span>
    </button>`;
  const opts = [...sel.options];
  const group = (key, areas) => `
    <div class="area-group">
      <div class="area-group-title">${esc(t(key))}</div>
      <div class="area-group-tiles">${opts.filter(o => areas.includes(o.value)).map(tile).join('')}</div>
    </div>`;
  document.getElementById('seat-area-picker').innerHTML =
    group('seat.area.Full', ['Full']) +
    group('seat.split.Vertical', SEAT_SPLITS.Vertical) +
    group('seat.split.Horizontal', SEAT_SPLITS.Horizontal) +
    group('seat.split.Quarters', SEAT_SPLITS.Quarters);
}

function pickSeatArea(area) {
  document.getElementById('seat-area').value = area;
  renderAreaPicker();
  updateSeatModePlaceholders();
}

// El modo del monitor elegido (si está encendido), recortado a su zona, se sugiere como placeholder: vacío = ese.
function updateSeatModePlaceholders() {
  const f = document.getElementById('seat-form');
  const mon = selectedSeatMonitor();
  const [w, h] = mon && mon.width && mon.height ? seatAreaSize(f.Area.value || 'Full', mon.width, mon.height) : [0, 0];
  f.Width.placeholder  = w || t('seat.modeAuto');
  f.Height.placeholder = h || t('seat.modeAuto');
  updateSeatPreview();   // la zona elegida se ve también en la vista previa
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

  const mon = selectedSeatMonitor();
  const num = el => parseInt(el.value, 10) || 0;
  const width = num(f.Width), height = num(f.Height), hz = num(f.RefreshHz);
  if ((width > 0) !== (height > 0)) { fail(t('seat.modeError')); return; }
  const audioOpt = f.AudioOut.selectedOptions[0];

  const body = {
    name: f.Name.value.trim(),
    sessionId: f.SessionId.value || null,
    // 0 = el modo que Windows dé al monitor al encenderlo (ver SeatManager.ResolveMode).
    monitor: mon ? { devicePath: mon.devicePath, friendlyName: mon.friendlyName,
                     width, height, refreshHz: hz, area: f.Area.value || 'Full' } : null,
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
  loadMonitorMap();
}

window.addEventListener('langchange', () => { if (activeTab === 'seats') renderSeats(); });

// ── Vista previa del editor de puestos ─────────────────────────────────────
function updateSeatPreview() {
  const box = document.getElementById('seat-preview');
  if (!box) return;
  const f = document.getElementById('seat-form');
  const mon = selectedSeatMonitor();
  const area = f.Area.value || 'Full';
  const sessionOpt = f.SessionId.selectedOptions[0];
  const name = f.Name.value.trim();
  const devices = [...document.querySelectorAll('#seat-devices input:checked')].map(c => c.dataset.kind);
  const ratio = mon && mon.width > 0 && mon.height > 0 ? mon.width / mon.height : 16 / 9;
  const pw = Math.round(Math.min(170, Math.max(110, 82 * ratio)));
  const chips = [];
  if (f.SessionId.value) chips.push(`<span class="chip chip-moonlight">${uiIcon('screen')}${esc(sessionOpt.textContent)}</span>`);
  if (mon) chips.push(`<span class="chip">${esc(mon.friendlyName)} · ${esc(t('seat.area.' + area))}</span>`);
  if (f.AudioOut.value) chips.push(`<span class="chip hw-audio">${seatIcon('Audio')}${esc(f.AudioOut.selectedOptions[0].textContent)}</span>`);
  for (const k of SEAT_KINDS) {
    const n = devices.filter(d => d === k).length;
    if (n) chips.push(`<span class="chip hw-${k.toLowerCase()}">${seatIcon(k)}${n > 1 ? `×${n}` : esc(t('seat.kind.' + k))}</span>`);
  }
  setPreview(box, `
    <div class="preview-screen" style="width:${pw}px">${mon ? screenGlyph(area) : uiIcon('screen')}</div>
    <div class="preview-body">
      <div class="preview-name${name ? '' : ' placeholder'}">${esc(name || t('seat.namePlaceholder'))}</div>
      <div class="preview-chips">${chips.join('')}</div>
    </div>`, `${mon ? mon.devicePath : ''}|${area}`);
}

document.addEventListener('DOMContentLoaded', () => {
  const form = document.getElementById('seat-form');
  form?.addEventListener('input', updateSeatPreview);
  form?.addEventListener('change', updateSeatPreview);
});
