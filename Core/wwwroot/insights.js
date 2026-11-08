'use strict';
// ── Uso en vivo, pasos del arranque, errores con solución y vista compacta ──
//
// El panel se abre en el mismo PC que codifica el vídeo: el uso se pide cada 2 s solo con la
// pestaña visible, y el servicio deja de medir a los 20 s sin consultas. Las gráficas son SVG
// de una línea, sin animaciones.

// ── Uso de CPU, GPU y encoder ───────────────────────────────────────────────
const Metrics = {
  last: null,
  hist: {},          // id de sesión (o 'system') → { cpu: [], gpu: [] }
  MAX: 60,           // 2 minutos de historia
  timer: null,

  start() {
    if (this.timer) return;
    this.poll();
    this.timer = setInterval(whenVisible(() => this.poll()), 2000);
    document.addEventListener('visibilitychange', () => { if (!document.hidden) this.poll(); });
  },

  async poll() {
    const res = await api('GET', '/api/metrics');
    if (!res || !res.ok) return;
    const m = await res.json();
    this.last = m;
    this.push('system', m.cpu, m.gpu);
    for (const [id, s] of Object.entries(m.sessions || {})) this.push(id, s.cpu, s.gpu);
    // Historia de sesiones que ya no están en marcha: fuera.
    for (const id of Object.keys(this.hist))
      if (id !== 'system' && !(m.sessions || {})[id]) delete this.hist[id];
    this.paint();
  },

  push(id, cpu, gpu) {
    const h = this.hist[id] || (this.hist[id] = { cpu: [], gpu: [] });
    h.cpu.push(cpu ?? null); h.gpu.push(gpu ?? null);
    if (h.cpu.length > this.MAX) { h.cpu.shift(); h.gpu.shift(); }
  },

  session(id) { return this.last && this.last.sessions ? this.last.sessions[id] : null; },

  // Solo se tocan los huecos de uso: las tarjetas no se rehacen cada 2 s.
  paint() {
    const strip = byId('res-strip');
    if (strip) {
      const html = resStripHtml();
      if (strip.dataset.html !== html) { strip.dataset.html = html; strip.innerHTML = html; }
      strip.hidden = !html;
    }
    document.querySelectorAll('[data-metrics]').forEach(el => {
      const s = sessions.find(x => x.id === el.dataset.metrics);
      const html = s ? metricsInner(s) : '';
      if (el.dataset.html !== html) { el.dataset.html = html; el.innerHTML = html; }
    });
  },
};

function pct(v) { return v == null ? '—' : `${Math.round(v)}%`; }

// Gráfica de línea de los últimos 2 minutos (0-100 %).
function sparkline(values, cls) {
  const pts = values.map((v, i) => v == null ? null : [i, v]).filter(Boolean);
  if (pts.length < 2) return '';
  const w = 64, h = 18, n = Metrics.MAX - 1;
  const d = pts.map(([i, v], k) =>
    `${k ? 'L' : 'M'}${(w - (values.length - 1 - i) * (w / n)).toFixed(1)},${(h - 1 - Math.min(100, v) / 100 * (h - 2)).toFixed(1)}`).join('');
  return `<svg class="spark ${cls}" viewBox="0 0 ${w} ${h}" width="${w}" height="${h}" aria-hidden="true"><path d="${d}"/></svg>`;
}

// "hevc_nvenc" → "HEVC · NVENC"; "libx264" → "H.264 · CPU".
function encoderLabel(name) {
  if (!name) return '';
  const n = name.toLowerCase();
  const codec = n.includes('av1') ? 'AV1' : n.includes('hevc') || n.includes('265') ? 'HEVC' : 'H.264';
  const hw = n.includes('nvenc') ? 'NVENC' : n.includes('qsv') ? 'Quick Sync' : n.includes('amf') ? 'AMF'
           : n.startsWith('lib') ? 'CPU' : n.includes('mf') ? 'Media Foundation' : name;
  return `${codec} · ${hw}`;
}

// Hueco de uso de una sesión en marcha (se rellena con cada consulta).
function metricsSlot(s) {
  if (s.state !== 'Running') return '';
  return `<div class="card-metrics" data-metrics="${s.id}">${metricsInner(s)}</div>`;
}

function metricsInner(s) {
  const m = Metrics.session(s.id);
  if (!m) return `<span class="metric metric-wait">${esc(t('metrics.waiting'))}</span>`;
  const h = Metrics.hist[s.id] || { cpu: [], gpu: [] };
  const enc = m.encoder
    ? `<span class="chip chip-encoder${m.software ? ' warn' : ''}" title="${esc(t(m.encoderActive ? 'metrics.encoderActive' : 'metrics.encoderFound', { name: m.encoder }))}">${uiIcon('screen')}${esc(encoderLabel(m.encoder))}</span>`
    : '';
  return `
    <span class="metric" title="${esc(t('metrics.cpu_title'))}"><span class="metric-k">CPU</span><b>${pct(m.cpu)}</b>${sparkline(h.cpu, 'spark-cpu')}</span>
    <span class="metric" title="${esc(t('metrics.gpu_title'))}"><span class="metric-k">GPU</span><b>${pct(m.gpu)}</b>${sparkline(h.gpu, 'spark-gpu')}</span>
    ${m.gpuEncode != null ? `<span class="metric" title="${esc(t('metrics.enc_title'))}"><span class="metric-k">${esc(t('metrics.enc'))}</span><b>${pct(m.gpuEncode)}</b></span>` : ''}
    ${enc}
    ${m.software ? `<div class="card-warning metric-warning" role="alert">${esc(t('metrics.software', { name: m.encoder }))}</div>` : ''}`;
}

// Uso del equipo en la barra de arriba.
function resStripHtml() {
  const m = Metrics.last;
  if (!m || (m.cpu == null && m.gpu == null)) return '';
  const h = Metrics.hist.system || { cpu: [], gpu: [] };
  const level = v => v == null ? '' : v >= 90 ? ' hot' : v >= 70 ? ' warm' : '';
  return `
    <span class="res-item${level(m.cpu)}" title="${esc(t('metrics.system_title'))}"><span class="metric-k">CPU</span><b>${pct(m.cpu)}</b>${sparkline(h.cpu, 'spark-cpu')}</span>
    <span class="res-item${level(m.gpu)}" title="${esc(t('metrics.system_title'))}"><span class="metric-k">GPU</span><b>${pct(m.gpu)}</b>${sparkline(h.gpu, 'spark-gpu')}</span>
    ${m.gpuEncode != null ? `<span class="res-item" title="${esc(t('metrics.enc_title'))}"><span class="metric-k">${esc(t('metrics.enc'))}</span><b>${pct(m.gpuEncode)}</b></span>` : ''}
    ${m.memTotalGb ? `<span class="res-item res-ram" title="${esc(t('metrics.ram_title'))}"><span class="metric-k">RAM</span><b>${esc(t('metrics.ramValue', { used: m.memUsedGb.toFixed(1), total: Math.round(m.memTotalGb) }))}</b></span>` : ''}`;
}

// ── Pasos del arranque ─────────────────────────────────────────────────────
// Cinco tramos: hechos, el actual (o el que falló) y los que faltan.
function phaseName(p) { return t('ux.phase.' + p).replace(/[….]+\s*$/, ''); }

function stepsHtml(phase, failed) {
  const idx = Math.max(0, START_PHASES.indexOf(phase));
  const segs = START_PHASES.map((p, i) => {
    const cls = i < idx ? 'done' : i > idx ? '' : failed ? 'failed' : 'current';
    return `<li class="${cls}" title="${i + 1}. ${esc(phaseName(p))}"></li>`;
  }).join('');
  const label = failed
    ? esc(t('err.step', { n: idx + 1, total: START_PHASES.length, step: phaseName(phase) }))
    : `${esc(t('ux.phase.' + phase))} <span class="start-steps-count">${idx + 1}/${START_PHASES.length}</span>`;
  return `<div class="start-steps${failed ? ' failed' : ''}" role="status">
      <ol class="steps" aria-hidden="true">${segs}</ol>
      <div class="start-steps-label">${label}</div>
    </div>`;
}

// ── Errores con pista y acciones ───────────────────────────────────────────
// Pistas por código de FreeRDP o texto del servicio (los códigos no dependen del idioma).
const ERROR_HINTS = [
  [/0x20016|ACCESS_DENIED/i,                                     'err.hint.accessDenied'],
  [/0x20009|0x20014|0x20015|LOGON_FAILURE|WRONG_PASSWORD|AUTHENTICATION_FAILED/i, 'err.hint.logon'],
  [/0x20003|POST_CONNECT_FAILED/i,                               'err.hint.postConnect'],
  [/0x10001|\b65537\b|RPC_INITIATED_DISCONNECT/i,                'err.hint.resolution'],
  [/0x20006|ERRCONNECT_CONNECT_FAILED/i,                         'err.hint.connect'],
];

function errorHint(msg) {
  const hit = ERROR_HINTS.find(([rx]) => rx.test(msg || ''));
  return hit ? t(hit[1]) : '';
}

function errorBoxHtml(s) {
  if (!s.errorMessage) return '';
  const settled = s.state === 'Error' || s.state === 'Stopped' || s.state === 'Created';
  // Mientras se reinicia sola (Sunshine caído) el mensaje informa; lo demás, solo si se quedó en error.
  if (!settled) return `<div class="card-error" role="alert"><span>${esc(I18n.message(s.errorMessage))}</span></div>`;
  const hint = errorHint(s.errorMessage);
  return `
    <div class="card-error" role="alert">
      ${s.failedPhase && !s.local ? stepsHtml(s.failedPhase, true) : ''}
      <span class="card-error-msg">${esc(I18n.message(s.errorMessage))}</span>
      ${hint ? `<div class="card-error-hint">${uiIcon('help')}<span>${esc(hint)}</span></div>` : ''}
      <div class="card-error-actions">
        <button type="button" class="btn btn-ghost btn-xs" onclick="retrySession('${s.id}')">${uiIcon('restart')}${esc(t('ux.retry'))}</button>
        <button type="button" class="btn btn-ghost btn-xs" onclick="openSessionLog('${s.id}')">${uiIcon('log')}${esc(t('ux.viewLog'))}</button>
        <button type="button" class="btn btn-ghost btn-xs" onclick="copySessionError('${s.id}')">${uiIcon('copy')}${esc(t('err.copy'))}</button>
        <button type="button" class="btn btn-ghost btn-xs" onclick="quickDiagnostics(this)">${uiIcon('download')}${esc(t('err.diag'))}</button>
      </div>
    </div>`;
}

async function copySessionError(id) {
  const s = sessions.find(x => x.id === id);
  if (!s || !s.errorMessage) return;
  const text = `OpenStreamMS — ${s.name}` +
    (s.failedPhase ? ` — ${START_PHASES.indexOf(s.failedPhase) + 1}/${START_PHASES.length} ${s.failedPhase}` : '') +
    `\n${s.errorMessage}`;
  try { await navigator.clipboard.writeText(text); toast(t('err.copied')); }
  catch { toast(t('err.copyFailed'), true); }
}

// Paquete de diagnóstico de los últimos 3 días, desde la propia tarjeta.
async function quickDiagnostics(btn) {
  if (btn.disabled) return;
  const label = btn.innerHTML;
  btn.disabled = true;
  btn.textContent = t('settings.diagWorking');
  try { await saveDiagnostics(3); }
  finally { btn.disabled = false; btn.innerHTML = label; }
}

// ── Vista de sesiones: tarjetas o lista compacta ───────────────────────────
const VIEW_KEY = 'openstreamms.sessionsView';

function sessionsView() {
  try { return localStorage.getItem(VIEW_KEY) === 'list' ? 'list' : 'cards'; } catch { return 'cards'; }
}

function applySessionsView() {
  const list = sessionsView() === 'list';
  byId('sessions-grid')?.classList.toggle('compact', list);
  document.querySelectorAll('.view-toggle button').forEach(b => {
    const on = b.dataset.view === (list ? 'list' : 'cards');
    b.classList.toggle('active', on);
    b.setAttribute('aria-pressed', String(on));
  });
}

function setSessionsView(v) {
  try { localStorage.setItem(VIEW_KEY, v === 'list' ? 'list' : 'cards'); } catch { /* sin almacenamiento */ }
  applySessionsView();
}

document.addEventListener('DOMContentLoaded', async () => {
  applySessionsView();
  if (!await I18n.ready) return;
  Metrics.start();
});
window.addEventListener('langchange', () => { Metrics.paint(); });
