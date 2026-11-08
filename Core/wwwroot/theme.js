'use strict';
// ── Tema y efectos ──────────────────────────────────────────────────────────
// Se carga en el <head>, antes de pintar nada: así la página no parpadea con el tema oscuro
// antes de ponerse el elegido. Es una preferencia de cada navegador (localStorage), como el
// idioma: no se guarda en el servicio.
//
//   <html data-theme="dark|light|synthwave|retro|matrix|nord|dracula|solarized|oled|contrast"
//         data-effects="full|reduced">
//
// "system" sigue al sistema (claro u oscuro). "windows" es el claro u oscuro de Windows con su
// color de acento: el servicio lo lee del usuario de la consola (/api/settings/windows-appearance)
// y aquí se guarda, para pintarlo ya en la próxima carga (y en el login, que no puede llamar a la API). Efectos "auto": reducidos si el sistema pide
// menos movimiento o si hay algo emitiendo (sesión o puesto encendido), porque el panel se
// abre en el mismo PC que codifica el vídeo y el desenfoque y las animaciones gastan GPU.
(function () {
  const THEMES  = ['system', 'windows', 'dark', 'light', 'nord', 'dracula', 'solarized',
                   'oled', 'contrast', 'synthwave', 'retro', 'matrix'];
  const EFFECTS = ['auto', 'full', 'reduced'];
  const KEY_THEME = 'openstreamms.theme', KEY_FX = 'openstreamms.effects', KEY_WIN = 'openstreamms.windowsAppearance';

  const read = (k, list, def) => {
    try { const v = localStorage.getItem(k); return list.includes(v) ? v : def; } catch { return def; }
  };
  const write = (k, v) => { try { localStorage.setItem(k, v); } catch { /* modo privado */ } };

  const darkQuery   = window.matchMedia ? matchMedia('(prefers-color-scheme: dark)') : null;
  const motionQuery = window.matchMedia ? matchMedia('(prefers-reduced-motion: reduce)') : null;

  let theme = read(KEY_THEME, THEMES, 'system');
  let effects = read(KEY_FX, EFFECTS, 'auto');
  let busy = false;   // algo emitiendo: lo dice app.js con Theme.setBusy()
  let win = (() => {  // { accent: '#rrggbb' | null, light: bool | null } del último aviso del servicio
    try { const v = JSON.parse(localStorage.getItem(KEY_WIN)); return v && typeof v === 'object' ? v : {}; } catch { return {}; }
  })();

  function systemIsLight() { return !!(darkQuery && !darkQuery.matches); }

  function resolvedTheme() {
    if (theme === 'windows') return (typeof win.light === 'boolean' ? win.light : systemIsLight()) ? 'light' : 'dark';
    if (theme !== 'system') return theme;
    return systemIsLight() ? 'light' : 'dark';
  }

  // ── Acento de Windows: tonos derivados con contraste suficiente ───────────
  const ACCENT_VARS = ['--accent', '--accent-h', '--accent-mid', '--accent-alt', '--accent-text',
                       '--accent-soft', '--on-accent', '--c-accent', '--brand-from'];
  const hexRgb = h => { const m = /^#?([0-9a-f]{6})$/i.exec(h || ''); if (!m) return null; const n = parseInt(m[1], 16); return [n >> 16, (n >> 8) & 255, n & 255]; };
  const rgbHex = c => '#' + c.map(v => Math.round(Math.min(255, Math.max(0, v))).toString(16).padStart(2, '0')).join('');
  const mix = (c, to, t) => c.map((v, i) => v + (to[i] - v) * t);
  const lum = c => { const [r, g, b] = c.map(v => { v /= 255; return v <= .03928 ? v / 12.92 : ((v + .055) / 1.055) ** 2.4; }); return .2126 * r + .7152 * g + .0722 * b; };
  const contrast = (a, b) => { const x = lum(a), y = lum(b); return (Math.max(x, y) + .05) / (Math.min(x, y) + .05); };
  const WHITE = [255, 255, 255], BLACK = [0, 0, 0];
  // Acerca un color al blanco (o al negro) hasta que contraste lo pedido con el fondo.
  function until(c, bg, ratio, to) { let t = 0; while (contrast(mix(c, to, t), bg) < ratio && t < 1) t += .05; return mix(c, to, Math.min(t, 1)); }

  function accentTokens(hex, light) {
    const base = hexRgb(hex);
    if (!base) return null;
    const card = light ? WHITE : [26, 26, 46];
    // Relleno de botones: que se distinga del fondo; el texto encima, blanco o negro según convenga.
    const fill = light ? until(base, WHITE, 3, BLACK) : until(base, card, 3, WHITE);
    const onFill = contrast(WHITE, fill) >= contrast([17, 17, 17], fill) ? '#ffffff' : '#111111';
    // Texto de acento sobre las tarjetas: 4,5:1 (WCAG AA).
    const text = light ? until(mix(base, BLACK, .2), WHITE, 4.5, BLACK) : until(mix(base, WHITE, .45), card, 4.5, WHITE);
    return {
      '--accent': rgbHex(fill),
      '--accent-h': rgbHex(mix(fill, BLACK, .14)),
      '--accent-mid': rgbHex(mix(fill, WHITE, light ? .1 : .18)),
      '--accent-alt': rgbHex(mix(fill, light ? BLACK : WHITE, .28)),
      '--accent-text': rgbHex(text),
      '--accent-soft': rgbHex(light ? mix(text, WHITE, .15) : mix(base, WHITE, .3)),
      '--on-accent': onFill,
      '--c-accent': fill.map(Math.round).join(','),
      '--brand-from': light ? rgbHex(mix(fill, BLACK, .35)) : rgbHex(mix(base, WHITE, .75)),
    };
  }

  function applyAccent(root, t) {
    const tokens = theme === 'windows' && win.accent ? accentTokens(win.accent, t === 'light') : null;
    for (const name of ACCENT_VARS) {
      if (tokens) root.style.setProperty(name, tokens[name]);
      else root.style.removeProperty(name);
    }
    if (theme === 'windows') root.dataset.accent = 'windows';
    else delete root.dataset.accent;
  }

  // Pide al servicio el acento y el modo de Windows (solo con el tema "windows"; sin sesión
  // responde 401 y se sigue con lo guardado).
  let winFetch = null;
  function refreshWindows(force) {
    if (theme !== 'windows' && !force) return Promise.resolve(win);
    if (winFetch) return winFetch;
    winFetch = fetch('/api/settings/windows-appearance', { credentials: 'same-origin', cache: 'no-store' })
      .then(r => r.ok ? r.json() : null)
      .then(v => {
        if (!v) return win;
        const next = { accent: hexRgb(v.accent) ? v.accent : null, light: typeof v.lightTheme === 'boolean' ? v.lightTheme : null };
        if (next.accent !== win.accent || next.light !== win.light) {
          win = next;
          try { localStorage.setItem(KEY_WIN, JSON.stringify(win)); } catch { /* modo privado */ }
          apply();
          document.dispatchEvent(new CustomEvent('windowsappearance'));
        }
        return win;
      })
      .catch(() => win)
      .finally(() => { winFetch = null; });
    return winFetch;
  }

  function resolvedEffects() {
    if (effects !== 'auto') return effects;
    return (motionQuery && motionQuery.matches) || busy ? 'reduced' : 'full';
  }

  function apply() {
    const root = document.documentElement;
    const t = resolvedTheme(), fx = resolvedEffects();
    if (root.dataset.theme !== t) root.dataset.theme = t;
    if (root.dataset.effects !== fx) root.dataset.effects = fx;
    applyAccent(root, t);
    // Barra del navegador en el móvil, del color del fondo del tema
    let meta = document.querySelector('meta[name="theme-color"]');
    if (!meta && document.head) {
      meta = document.createElement('meta');
      meta.name = 'theme-color';
      document.head.appendChild(meta);
    }
    if (meta && document.body) meta.content = getComputedStyle(document.body).backgroundColor;
    // Ventana propia del programa (WebView2): su barra de título toma el color de la barra del panel.
    const host = window.chrome && window.chrome.webview;
    if (host && document.body) {
      const bar = document.querySelector('.nav');
      const color = getComputedStyle(bar && bar.offsetParent !== null ? bar : document.body).backgroundColor;
      try { host.postMessage({ type: 'theme', color, dark: t !== 'light' }); } catch { /* sin ventana */ }
    }
    Rain.update(t === 'matrix' && fx === 'full');
  }

  // ── Lluvia de caracteres del tema Matrix ──────────────────────────────────
  // Un canvas detrás de todo, a 20 fps y solo con efectos completos y la pestaña visible.
  const Rain = {
    canvas: null, timer: null, drops: [],
    update(on) {
      if (on && !this.canvas && document.body) this.start();
      else if (!on && this.canvas) this.stop();
    },
    start() {
      const c = this.canvas = document.createElement('canvas');
      c.className = 'matrix-rain';
      c.setAttribute('aria-hidden', 'true');
      document.body.prepend(c);
      const chars = 'ｱｲｳｴｵｶｷｸｹｺｻｼｽｾｿﾀﾁﾂﾃﾄﾅﾆﾇﾈﾉ0123456789';
      const size = 16;
      const resize = () => {
        c.width = innerWidth; c.height = innerHeight;
        this.drops = Array.from({ length: Math.ceil(c.width / size) }, () => Math.random() * -50);
      };
      resize();
      this.onResize = resize;
      addEventListener('resize', resize);
      const ctx = c.getContext('2d');
      this.timer = setInterval(() => {
        if (document.hidden) return;
        ctx.fillStyle = 'rgba(0,0,0,.08)';
        ctx.fillRect(0, 0, c.width, c.height);
        ctx.font = `${size}px monospace`;
        for (let i = 0; i < this.drops.length; i++) {
          const y = this.drops[i] * size;
          ctx.fillStyle = Math.random() < .03 ? '#d6ffe4' : '#00c853';
          ctx.fillText(chars[(Math.random() * chars.length) | 0], i * size, y);
          if (y > c.height && Math.random() > .975) this.drops[i] = 0;
          this.drops[i]++;
        }
      }, 50);
    },
    stop() {
      clearInterval(this.timer);
      removeEventListener('resize', this.onResize);
      this.canvas.remove();
      this.canvas = null;
    },
  };

  darkQuery && darkQuery.addEventListener && darkQuery.addEventListener('change', apply);
  motionQuery && motionQuery.addEventListener && motionQuery.addEventListener('change', apply);
  // Otra pestaña del panel cambió el tema
  addEventListener('storage', e => {
    if (e.key === KEY_THEME) theme = read(KEY_THEME, THEMES, 'system');
    if (e.key === KEY_FX) effects = read(KEY_FX, EFFECTS, 'auto');
    if (e.key === KEY_THEME || e.key === KEY_FX) apply();
  });
  // Hasta que exista <body> no se puede poner el canvas ni leer el color de fondo
  document.addEventListener('DOMContentLoaded', () => { apply(); refreshWindows(); });
  // Al volver a la pestaña, por si se cambió el acento o el modo en Windows
  document.addEventListener('visibilitychange', () => { if (!document.hidden) refreshWindows(); });

  window.Theme = {
    THEMES, EFFECTS,
    get theme() { return theme; },
    get effects() { return effects; },
    get resolvedTheme() { return resolvedTheme(); },
    get resolvedEffects() { return resolvedEffects(); },
    get windows() { return { ...win }; },
    refreshWindows,
    setTheme(v) { if (THEMES.includes(v)) { theme = v; write(KEY_THEME, v); apply(); refreshWindows(); } },
    setEffects(v) { if (EFFECTS.includes(v)) { effects = v; write(KEY_FX, v); apply(); } },
    setBusy(v) { if (busy !== !!v) { busy = !!v; apply(); } },
  };

  apply();
})();
