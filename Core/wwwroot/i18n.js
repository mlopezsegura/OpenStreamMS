'use strict';

// Dictionaries are data, never executable scripts. Keep the public synchronous t()
// API; page initialization waits for I18n.ready before creating dynamic content.
(() => {
  const NAMES = { bg: 'Български', cs: 'Čeština', de: 'Deutsch', en: 'English', en_GB: 'English (United Kingdom)', en_US: 'English (United States)', es: 'Español', fr: 'Français', hu: 'Magyar', it: 'Italiano', ja: '日本語', ko: '한국어', pl: 'Polski', pt: 'Português', pt_BR: 'Português (Brasil)', ru: 'Русский', sv: 'Svenska', tr: 'Türkçe', uk: 'Українська', vi: 'Tiếng Việt', zh: '简体中文', zh_TW: '繁體中文' };
  const SUPPORTED = Object.keys(NAMES);
  const PARENTS = { en_GB: 'en', en_US: 'en', pt_BR: 'pt', zh_TW: 'zh' };
  const DEFAULT_LANG = 'en';
  const STORAGE_KEY = 'openstreamms.lang';
  const baseUrl = new URL('locales/', document.currentScript.src);
  const tables = new Map();
  const pending = new Map();
  let changeId = 0;
  let initialized = false;

  // Added in the head: no flash of the Spanish HTML or early user actions while
  // the selected language is loading. A failed English download has a retry UI.
  const loadingStyle = document.createElement('style');
  loadingStyle.textContent = 'html.i18n-loading body{visibility:hidden}';
  document.head.append(loadingStyle);
  document.documentElement.classList.add('i18n-loading');

  function normalizeLanguage(name) {
    const tag = String(name || '').replaceAll('_', '-').toLowerCase();
    if (/^zh-(?:hant|tw|hk|mo)(?:-|$)/.test(tag)) return 'zh_TW';
    const exact = SUPPORTED.find(code => code.replaceAll('_', '-').toLowerCase() === tag);
    if (exact) return exact;
    const primary = tag.split('-')[0];
    return SUPPORTED.includes(primary) ? primary : null;
  }
  function chain(lang) { return [...new Set([lang, PARENTS[lang], DEFAULT_LANG].filter(Boolean))]; }
  function lookup(lang, key) {
    for (const code of chain(lang)) {
      const table = tables.get(code);
      if (table && Object.hasOwn(table, key)) return table[key];
    }
  }
  async function loadSelected(lang) {
    await Promise.all([load(lang), ...(PARENTS[lang] ? [load(PARENTS[lang]).catch(() => {})] : [])]);
  }
  function detectLanguage() {
    try {
      const stored = localStorage.getItem(STORAGE_KEY);
      if (normalizeLanguage(stored)) return normalizeLanguage(stored);
    } catch { /* Storage can be disabled. */ }
    for (const name of navigator.languages || [navigator.language || '']) {
      const lang = normalizeLanguage(name);
      if (lang) return lang;
    }
    return DEFAULT_LANG;
  }

  function validate(table) {
    if (!table || Array.isArray(table) || typeof table !== 'object') throw new Error('Invalid dictionary');
    for (const [key, value] of Object.entries(table)) {
      if (!key || typeof value !== 'string') throw new Error('Invalid translation');
      // Existing markup uses only these formatting tags, with no attributes.
      const markup = value.match(/<[^>]*>/g) || [];
      if (markup.some(tag => !/^<\/?(?:strong|code|b|em|br)\s*\/?\s*>$/i.test(tag))) {
        throw new Error('Unsafe translation markup');
      }
    }
    return table;
  }

  async function load(lang) {
    if (tables.has(lang)) return tables.get(lang);
    if (pending.has(lang)) return pending.get(lang);
    const request = (async () => {
      const controller = new AbortController();
      const timer = setTimeout(() => controller.abort(), 8000);
      try {
        const response = await fetch(new URL(lang + '.json', baseUrl), { signal: controller.signal, credentials: 'same-origin' });
        if (!response.ok) throw new Error('Dictionary HTTP ' + response.status);
        const table = validate(await response.json());
        tables.set(lang, table);
        return table;
      } finally { clearTimeout(timer); }
    })();
    pending.set(lang, request);
    try { return await request; } finally { pending.delete(lang); }
  }

  const own = (table, key) => table && Object.hasOwn(table, key) ? table[key] : undefined;
  const escapeHtml = value => String(value).replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[c]);

  const I18n = {
    lang: detectLanguage(),
    has(key) { return lookup(this.lang, key) !== undefined; },
    t(key, params) {
      const value = lookup(this.lang, key);
      if (value === undefined) {
        console.warn('Missing translation:', key);
        return initialized ? (own(tables.get(this.lang), 'toast.translationUnavailable') || 'Translation unavailable.') : '';
      }
      // Single pass: a parameter containing {another} is never interpolated again.
      return value.replace(/\{(\w+)\}/g, (token, name) => params && Object.hasOwn(params, name) ? String(params[name]) : token);
    },
    html(key, params) {
      const escaped = params && Object.fromEntries(Object.entries(params).map(([k, v]) => [k, escapeHtml(v)]));
      return this.t(key, escaped);
    },
    async setLanguage(lang) {
      lang = normalizeLanguage(lang);
      if (!lang) return false;
      const id = ++changeId;
      if (!await this.ready) return false;
      try { await loadSelected(lang); }
      catch {
        if (id === changeId) {
          this.apply();
          let alert = document.getElementById('i18n-error');
          if (!alert) { alert = document.createElement('div'); alert.id = 'i18n-error'; alert.setAttribute('role', 'alert'); document.body.prepend(alert); }
          alert.textContent = this.t('toast.languageLoadError');
        }
        return false;
      }
      if (id !== changeId) return false;
      document.getElementById('i18n-error')?.remove();
      this.lang = lang;
      try { localStorage.setItem(STORAGE_KEY, lang); } catch { /* Optional storage. */ }
      this.apply();
      window.dispatchEvent(new Event('langchange'));
      return true;
    },
    apply(root) {
      const r = root || document;
      const each = (attr, fn) => {
        if (r.matches?.('[' + attr + ']')) fn(r);
        r.querySelectorAll('[' + attr + ']').forEach(fn);
      };
      each('data-i18n', el => { el.textContent = this.t(el.dataset.i18n); });
      each('data-i18n-html', el => { el.innerHTML = this.html(el.dataset.i18nHtml); });
      each('data-i18n-placeholder', el => { el.placeholder = this.t(el.dataset.i18nPlaceholder); });
      each('data-i18n-title', el => { el.title = this.t(el.dataset.i18nTitle); });
      each('data-i18n-aria', el => { el.setAttribute('aria-label', this.t(el.dataset.i18nAria)); });
      const title = document.documentElement.dataset.i18nDocTitle;
      if (title) document.title = this.t(title);
      document.documentElement.lang = this.lang.replaceAll('_', '-');
      const sel = document.getElementById('lang-select');
      if (sel) sel.value = this.lang;
    },
    supported() { return SUPPORTED.slice(); },
  };
  window.I18n = I18n;
  window.t = (key, params) => I18n.t(key, params);

  const domReady = document.readyState === 'loading'
    ? new Promise(resolve => document.addEventListener('DOMContentLoaded', resolve, { once: true }))
    : Promise.resolve();
  I18n.ready = (async () => {
    const requested = I18n.lang;
    const selected = loadSelected(requested).then(() => true, () => false);
    try { await load(DEFAULT_LANG); }
    catch (error) {
      await domReady;
      const box = document.createElement('div');
      box.setAttribute('role', 'alert');
      box.textContent = 'OpenStreamMS could not load its translations. Check the connection and retry. ';
      const retry = document.createElement('button');
      retry.textContent = 'Retry';
      retry.onclick = () => location.reload();
      box.append(retry);
      document.body.replaceChildren(box);
      document.documentElement.classList.remove('i18n-loading');
      console.error('Translation initialization failed', error);
      return false;
    }
    I18n.lang = await selected ? requested : DEFAULT_LANG;
    await domReady;
    const sel = document.getElementById('lang-select');
    if (sel) {
      sel.replaceChildren(...SUPPORTED.map(lang => {
        const option = document.createElement('option'); option.value = lang; option.textContent = NAMES[lang]; return option;
      }));
      sel.addEventListener('change', () => I18n.setLanguage(sel.value));
    }
    initialized = true;
    I18n.apply();
    document.documentElement.classList.remove('i18n-loading');
    return true;
  })();
})();
