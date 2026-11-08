'use strict';
// ── Tema y efectos ──────────────────────────────────────────────────────────
// Se carga en el <head>, antes de pintar nada: así la página no parpadea con el tema oscuro
// antes de ponerse el elegido. Es una preferencia de cada navegador (localStorage), como el
// idioma: no se guarda en el servicio.
//
//   <html data-theme="dark|light|synthwave|retro|matrix" data-effects="full|reduced">
//
// "system" sigue al sistema (claro u oscuro). Efectos "auto": reducidos si el sistema pide
// menos movimiento o si hay algo emitiendo (sesión o puesto encendido), porque el panel se
// abre en el mismo PC que codifica el vídeo y el desenfoque y las animaciones gastan GPU.
(function () {
  const THEMES  = ['system', 'dark', 'light', 'synthwave', 'retro', 'matrix'];
  const EFFECTS = ['auto', 'full', 'reduced'];
  const KEY_THEME = 'openstreamms.theme', KEY_FX = 'openstreamms.effects';

  const read = (k, list, def) => {
    try { const v = localStorage.getItem(k); return list.includes(v) ? v : def; } catch { return def; }
  };
  const write = (k, v) => { try { localStorage.setItem(k, v); } catch { /* modo privado */ } };

  const darkQuery   = window.matchMedia ? matchMedia('(prefers-color-scheme: dark)') : null;
  const motionQuery = window.matchMedia ? matchMedia('(prefers-reduced-motion: reduce)') : null;

  let theme = read(KEY_THEME, THEMES, 'system');
  let effects = read(KEY_FX, EFFECTS, 'auto');
  let busy = false;   // algo emitiendo: lo dice app.js con Theme.setBusy()

  function resolvedTheme() {
    if (theme !== 'system') return theme;
    return darkQuery && !darkQuery.matches ? 'light' : 'dark';
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
  document.addEventListener('DOMContentLoaded', apply);

  window.Theme = {
    THEMES, EFFECTS,
    get theme() { return theme; },
    get effects() { return effects; },
    get resolvedTheme() { return resolvedTheme(); },
    get resolvedEffects() { return resolvedEffects(); },
    setTheme(v) { if (THEMES.includes(v)) { theme = v; write(KEY_THEME, v); apply(); } },
    setEffects(v) { if (EFFECTS.includes(v)) { effects = v; write(KEY_FX, v); apply(); } },
    setBusy(v) { if (busy !== !!v) { busy = !!v; apply(); } },
  };

  apply();
})();
