'use strict';
// ── Guía interactiva ───────────────────────────────────────────────────────
// Ventana por pasos, aparte de la bienvenida: qué es una sesión, qué es un puesto, qué hay
// que instalar (con su estado real) y por dónde empezar. Se abre desde el «?» de la barra de
// arriba, desde «Más» y desde la bienvenida. Depende de app.js (t, esc, uiIcon, show, hide,
// rdpLastStatus, drivers…) y seats.js (switchTab, openSeatModal).

const GUIDE_STEPS = ['welcome', 'session', 'seat', 'install', 'start'];
let guideStep = 0;
let guideTimer = null;

function openGuide(step = 0) {
  guideStep = Math.max(0, Math.min(GUIDE_STEPS.length - 1, step));
  document.querySelectorAll('.nav-pop').forEach(p => p.classList.add('hidden'));
  show('guide-modal');
  renderGuide(0);
  guideRefreshDeps();
  clearInterval(guideTimer);
  guideTimer = setInterval(whenVisible(guideRefreshDeps), 3000);
}

function closeGuide() {
  hide('guide-modal');
  clearInterval(guideTimer);
  guideTimer = null;
}

function guideGo(step) {
  if (step < 0 || step >= GUIDE_STEPS.length || step === guideStep) return;
  const dir = step > guideStep ? 1 : -1;
  guideStep = step;
  renderGuide(dir);
}

// Estado de los componentes: los mismos sondeos que las píldoras de la barra.
async function guideRefreshDeps() {
  if (byId('guide-modal').classList.contains('hidden')) return;
  await Promise.all([rdpRefreshPillOnly(), ...DRIVER_KEYS.map(driverRefreshPillOnly)]);
  if (GUIDE_STEPS[guideStep] === 'install') {
    // Solo si cambia algo, y sin repetir la entrada animada: si no, parpadea cada 3 s.
    const list = byId('guide-deps'), html = guideDepsHtml();
    if (list && list.dataset.html !== html) {
      list.dataset.html = html;
      list.classList.add('guide-deps-live');
      list.innerHTML = html;
    }
  }
}

function renderGuide(dir) {
  const id = GUIDE_STEPS[guideStep];
  const body = byId('guide-body');
  body.innerHTML = `<div class="guide-step ${dir > 0 ? 'guide-in-next' : dir < 0 ? 'guide-in-prev' : 'guide-in'}">${GUIDE_RENDER[id]()}</div>`;
  byId('guide-dots').innerHTML = GUIDE_STEPS.map((s, i) =>
    `<button type="button" class="guide-dot${i === guideStep ? ' active' : ''}${i < guideStep ? ' done' : ''}"
       onclick="guideGo(${i})" aria-label="${esc(t('guide.' + s + '.nav'))}" title="${esc(t('guide.' + s + '.nav'))}"></button>`).join('');
  byId('guide-progress').style.setProperty('--p', `${(guideStep / (GUIDE_STEPS.length - 1)) * 100}%`);
  byId('guide-back').disabled = guideStep === 0;
  const last = guideStep === GUIDE_STEPS.length - 1;
  byId('guide-next').textContent = t(last ? 'guide.finish' : 'guide.next');
  byId('guide-next').onclick = last ? closeGuide : () => guideGo(guideStep + 1);
  byId('guide-step-label').textContent = t('guide.stepOf', { n: guideStep + 1, total: GUIDE_STEPS.length });
}

const guidePoint = (icon, title, text, i) => `
  <li class="guide-point" style="--i:${i}">
    <span class="guide-point-icon">${uiIcon(icon)}</span>
    <div><b>${esc(title)}</b><span>${esc(text)}</span></div>
  </li>`;

const GUIDE_RENDER = {
  welcome: () => `
    <div class="guide-hero">${GUIDE_ART.welcome}</div>
    <h2 class="guide-title">${esc(t('guide.welcome.title'))}</h2>
    <p class="guide-lead">${esc(t('guide.welcome.lead'))}</p>
    <div class="guide-pair">
      <button type="button" class="guide-choice" style="--i:0" onclick="guideGo(1)">
        ${uiIcon('web')}<b>${esc(t('tab.sessions'))}</b><span>${esc(t('guide.welcome.sessions'))}</span></button>
      <button type="button" class="guide-choice" style="--i:1" onclick="guideGo(2)">
        ${uiIcon('seat')}<b>${esc(t('tab.seats'))}</b><span>${esc(t('guide.welcome.seats'))}</span></button>
    </div>`,

  session: () => `
    <div class="guide-hero">${GUIDE_ART.session}</div>
    <h2 class="guide-title">${esc(t('guide.session.title'))}</h2>
    <p class="guide-lead">${esc(t('guide.session.lead'))}</p>
    <ul class="guide-points">
      ${guidePoint('users',  t('guide.session.p1t'), t('guide.session.p1'), 0)}
      ${guidePoint('screen', t('guide.session.p2t'), t('guide.session.p2'), 1)}
      ${guidePoint('link',   t('guide.session.p3t'), t('guide.session.p3'), 2)}
      ${guidePoint('restart',t('guide.session.p4t'), t('guide.session.p4'), 3)}
    </ul>`,

  seat: () => `
    <div class="guide-hero">${GUIDE_ART.seat}</div>
    <h2 class="guide-title">${esc(t('guide.seat.title'))}</h2>
    <p class="guide-lead">${esc(t('guide.seat.lead'))}</p>
    <ul class="guide-points">
      ${guidePoint('seat',   t('guide.seat.p1t'), t('guide.seat.p1'), 0)}
      ${guidePoint('pc',     t('guide.seat.p2t'), t('guide.seat.p2'), 1)}
      ${guidePoint('screen', t('guide.seat.p3t'), t('guide.seat.p3'), 2)}
    </ul>
    <div class="guide-compare">
      <div><span class="guide-compare-k">${esc(t('tab.sessions'))}</span>${esc(t('guide.seat.cmpSession'))}</div>
      <div><span class="guide-compare-k">${esc(t('tab.seats'))}</span>${esc(t('guide.seat.cmpSeat'))}</div>
    </div>`,

  install: () => `
    <div class="guide-hero guide-hero-sm">${GUIDE_ART.install}</div>
    <h2 class="guide-title">${esc(t('guide.install.title'))}</h2>
    <p class="guide-lead">${esc(t('guide.install.lead'))}</p>
    <ul class="guide-deps" id="guide-deps">${guideDepsHtml()}</ul>`,

  start: () => `
    <div class="guide-hero guide-hero-sm">${GUIDE_ART.start}</div>
    <h2 class="guide-title">${esc(t('guide.start.title'))}</h2>
    <p class="guide-lead">${esc(t('guide.start.lead'))}</p>
    <div class="guide-pair">
      <div class="guide-action" style="--i:0">
        ${uiIcon('web')}<b>${esc(t('guide.start.sessionT'))}</b><span>${esc(t('guide.start.session'))}</span>
        <button type="button" class="btn btn-primary btn-sm" onclick="closeGuide(); switchTab('sessions'); openNewModal()">${esc(t('toolbar.newSession'))}</button>
      </div>
      <div class="guide-action" style="--i:1">
        ${uiIcon('seat')}<b>${esc(t('guide.start.seatT'))}</b><span>${esc(t('guide.start.seat'))}</span>
        <button type="button" class="btn btn-ghost btn-sm" onclick="closeGuide(); switchTab('seats'); openSeatModal()">${esc(t('toolbar.newSeat'))}</button>
      </div>
    </div>
    <p class="guide-tip">${uiIcon('moon')}<span>${esc(t('guide.start.connect'))}</span></p>`,
};

// Componentes: qué hace cada uno, cuándo hace falta y su estado real ahora mismo.
function guideDepsHtml() {
  const rdp = rdpLastStatus, vg = drivers.vigem.lastStatus, hh = drivers.hidhide.lastStatus;
  const state = (st, isRdp) => !st ? 'checking' : st.busy ? 'working' : st.rebootRequired ? 'reboot'
    : st.installed ? 'ok' : isRdp && st.pending ? 'activates' : isRdp && st.wrapper ? 'broken' : 'missing';
  const ridd = !rdp ? 'checking' : rdp.remoteIdd ? 'ok' : 'optional';
  const rows = [
    { name: 'TermWrap',  what: t('guide.install.rdp'),      need: t('guide.install.needSessions'), st: state(rdp, true), open: 'closeGuide(); openRdpModal()' },
    { name: 'ViGEmBus',  what: t('guide.install.vigem'),    need: t('guide.install.needPads'),     st: state(vg),        open: "closeGuide(); openDriverModal('vigem')" },
    { name: 'HidHide',   what: t('guide.install.hidhide'),  need: t('guide.install.needPads'),     st: state(hh),        open: "closeGuide(); openDriverModal('hidhide')" },
    { name: 'RemoteIdd', what: t('guide.install.remoteidd'), need: t('guide.install.needSeats'),   st: ridd },
  ];
  return rows.map((r, i) => {
    const mark = r.st === 'ok' || r.st === 'activates' ? '<svg viewBox="0 0 24 24" aria-hidden="true"><path class="guide-check" d="M5 12.5l4.5 4.5L19 7.5"/></svg>'
               : r.st === 'checking' || r.st === 'working' ? '<span class="guide-spin"></span>'
               : r.st === 'optional' ? '<span class="guide-dash"></span>' : '<span class="guide-bang">!</span>';
    const label = r.st === 'optional' ? t('guide.install.optional') : t('ux.dep.' + r.st);
    const btn = r.open && ['missing', 'broken', 'reboot'].includes(r.st)
      ? `<button type="button" class="btn btn-primary btn-xs" onclick="${r.open}">${esc(t(r.st === 'missing' ? 'ux.install' : 'ux.review'))}</button>` : '';
    return `<li class="guide-dep guide-dep-${r.st}" style="--i:${i}">
        <span class="guide-dep-mark">${mark}</span>
        <div class="guide-dep-text"><b>${esc(r.name)}</b><span>${esc(r.what)}</span><small>${esc(r.need)}</small></div>
        <span class="guide-dep-state">${esc(label)}</span>${btn}
      </li>`;
  }).join('');
}

// Ilustraciones: SVG en línea con colores del tema (currentColor y variables CSS).
const GUIDE_ART = {
  welcome: `<svg class="guide-art" viewBox="0 0 240 140" aria-hidden="true">
      <circle class="ga-ring" cx="120" cy="70" r="30"/><circle class="ga-ring ga-d1" cx="120" cy="70" r="30"/><circle class="ga-ring ga-d2" cx="120" cy="70" r="30"/>
      <rect class="ga-fill" x="96" y="46" width="48" height="48" rx="11"/>
      <path d="M114 58l17 12-17 12z" fill="#fff"/>
    </svg>`,

  session: `<svg class="guide-art" viewBox="0 0 280 150" aria-hidden="true">
      <rect class="ga-box" x="14" y="30" width="96" height="74" rx="8"/>
      <path class="ga-line" d="M40 118h44M62 104v14"/>
      <rect class="ga-win ga-d0" x="24" y="40" width="34" height="24" rx="3"/>
      <rect class="ga-win ga-d1" x="66" y="40" width="34" height="24" rx="3"/>
      <rect class="ga-win ga-d2" x="24" y="70" width="34" height="24" rx="3"/>
      <path class="ga-flow" d="M110 52 C150 52 160 30 196 30"/>
      <path class="ga-flow ga-d1" d="M110 67 C150 67 160 75 196 75"/>
      <path class="ga-flow ga-d2" d="M110 82 C150 82 160 120 196 120"/>
      <rect class="ga-dev" x="200" y="14" width="56" height="34" rx="4"/><path class="ga-line" d="M218 54h20"/>
      <rect class="ga-dev ga-d1" x="204" y="62" width="44" height="26" rx="3"/><path class="ga-line" d="M198 92h56"/>
      <rect class="ga-dev ga-d2" x="214" y="104" width="22" height="36" rx="4"/>
    </svg>`,

  seat: `<svg class="guide-art" viewBox="0 0 280 150" aria-hidden="true">
      <rect class="ga-box" x="118" y="92" width="44" height="50" rx="5"/><path class="ga-line" d="M128 104h24M128 112h24"/>
      <rect class="ga-dev" x="20" y="14" width="96" height="60" rx="5"/><rect class="ga-screen ga-d0" x="26" y="20" width="84" height="48" rx="2"/>
      <rect class="ga-dev" x="164" y="14" width="96" height="60" rx="5"/><rect class="ga-screen ga-d1" x="170" y="20" width="84" height="48" rx="2"/>
      <path class="ga-line" d="M58 74v10M222 74v10"/>
      <rect class="ga-dev ga-small" x="24" y="96" width="60" height="16" rx="3"/>
      <path class="ga-dev ga-small" d="M196 98h40a8 8 0 0 1 8 10l-4 18a5 5 0 0 1-9 1l-5-9h-20l-5 9a5 5 0 0 1-9-1l-4-18a8 8 0 0 1 8-10z"/>
      <path class="ga-flow" d="M84 104 C100 104 104 116 118 116"/>
      <path class="ga-flow ga-d1" d="M188 110 C178 110 172 116 162 116"/>
      <path class="ga-flow ga-d2" d="M68 74 C68 90 110 96 122 96"/>
      <path class="ga-flow ga-d0" d="M212 74 C212 90 170 96 158 96"/>
    </svg>`,

  install: `<svg class="guide-art" viewBox="0 0 200 90" aria-hidden="true">
      <rect class="ga-box" x="40" y="10" width="120" height="70" rx="8"/>
      <path class="ga-line" d="M60 30h50M60 46h70M60 62h40"/>
      <circle class="ga-tick ga-d0" cx="140" cy="30" r="6"/><circle class="ga-tick ga-d1" cx="148" cy="46" r="6"/><circle class="ga-tick ga-d2" cx="128" cy="62" r="6"/>
    </svg>`,

  start: `<svg class="guide-art" viewBox="0 0 200 90" aria-hidden="true">
      <path class="ga-flow" d="M20 70 C60 70 70 20 110 20 S170 50 180 30"/>
      <circle class="ga-fill ga-pulse" cx="180" cy="30" r="9"/>
      <path d="M177 25l8 5-8 5z" fill="#fff"/>
    </svg>`,
};

document.addEventListener('keydown', e => {
  if (byId('guide-modal')?.classList.contains('hidden') !== false) return;
  if (e.target.closest('input, select, textarea')) return;
  if (e.key === 'ArrowRight') guideGo(guideStep + 1);
  if (e.key === 'ArrowLeft')  guideGo(guideStep - 1);
});

// Al cambiar de idioma se repinta el paso abierto (sus textos salen de JS, no de data-i18n).
window.addEventListener('langchange', () => {
  if (!byId('guide-modal').classList.contains('hidden')) renderGuide(0);
});

// Enlace directo: /#guide abre la guía; /#guide-3, en el paso 3.
document.addEventListener('DOMContentLoaded', async () => {
  const m = /^#guide(?:-(\d))?$/.exec(location.hash);
  if (m && await I18n.ready) openGuide(Number(m[1] || 1) - 1);
});
