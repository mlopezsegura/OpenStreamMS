const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const cp = require('node:child_process');
const root = path.resolve(__dirname, '..');
const web = path.join(root, 'Core/wwwroot');
const loader = fs.readFileSync(path.join(web, 'i18n.js'), 'utf8');
const catalogs = Object.fromEntries(fs.readdirSync(path.join(web, 'locales')).filter(f => f.endsWith('.json')).map(file => [file.slice(0, -5), JSON.parse(fs.readFileSync(path.join(web, 'locales', file), 'utf8'))]));
const placeholders = value => [...value.matchAll(/\{(\w+)\}/g)].map(m => m[1]).sort();

test('all dictionaries have exactly the English keys and placeholders', () => {
  const expected = JSON.parse(fs.readFileSync(path.join(root, 'Core/Localization/locales.json'), 'utf8')).locales;
  assert.deepEqual(Object.keys(catalogs).sort(), expected.slice().sort());
  for (const [lang, table] of Object.entries(catalogs)) {
    assert.deepEqual(Object.keys(table).sort(), Object.keys(catalogs.en).sort(), lang);
    for (const [key, value] of Object.entries(table)) {
      assert.equal(typeof value, 'string', lang + ':' + key);
      assert.ok(value.length, lang + ':' + key);
      assert.deepEqual(placeholders(value), placeholders(catalogs.en[key]), lang + ':' + key);
    }
  }
});

test('HTML translations contain balanced formatting tags without attributes', () => {
  for (const [lang, table] of Object.entries(catalogs)) for (const [key, value] of Object.entries(table)) {
    const stack = [];
    for (const match of value.matchAll(/<([^>]*)>/g)) {
      assert.match(match[0], /^<\/?(?:strong|code|b|em|br)\s*\/?\s*>$/i, lang + ':' + key);
      const tag = match[1].replaceAll('/', '').trim();
      if (tag === 'br') continue;
      if (match[1].startsWith('/')) assert.equal(stack.pop(), tag, lang + ':' + key);
      else stack.push(tag);
    }
    assert.deepEqual(stack, [], lang + ':' + key);
  }
});

test('all original effective translations survive extraction unchanged', () => {
  const original = cp.execFileSync('git', ['show', '79a032e5d00efb37bb53a71991408acbd94c3a11:Core/wwwroot/i18n.js'], { cwd: root, encoding: 'utf8' });
  const context = vm.createContext({});
  vm.runInContext(original.slice(0, original.indexOf('const SUPPORTED')) + '\nglobalThis.catalogs = MESSAGES;', context);
  // Textos cambiados a propósito después de la extracción: asmtron se quitó y TermWrap es el único multi-RDP.
  const changed = new Set(['about.credit.rdpwrap', 'rdp.title', 'rdp.description', 'rdp.btn.install', 'rdp.row.bundle', 'rdp.confirm.install',
    'rdp.btn.update', 'rdp.confirm.update', 'rdp.btn.uninstall', 'rdp.confirm.uninstall', 'rdp.confirm.leaveRemoteIdd']);
  for (const [lang, table] of Object.entries(context.catalogs)) for (const [key, value] of Object.entries(table)) if (!changed.has(key)) assert.equal(catalogs[lang][key], value, lang + ':' + key);
});

test('every static HTML and JS translation reference exists', () => {
  for (const file of fs.readdirSync(web).filter(f => /\.(html|js)$/.test(f) && f !== 'i18n.js')) {
    const source = fs.readFileSync(path.join(web, file), 'utf8');
    for (const m of source.matchAll(/data-i18n(?:-html|-placeholder|-title|-aria|-doc-title)?="([\w.]+)"|\bt\('([\w.]+)'\s*[,)]/g)) {
      assert.ok(Object.hasOwn(catalogs.en, m[1] || m[2]), file + ':' + (m[1] || m[2]));
    }
  }
});

// A deliberately small DOM adapter exercises the actual loader without a browser
// dependency. Browser/layout checks live in i18n-browser.cjs.
function environment({ language = 'en', stored, fail = [], delay = {}, tables = catalogs, storageError = false } = {}) {
  const elements = new Map();
  const events = [];
  const writes = [];
  const classes = new Set();
  const make = () => ({ dataset: {}, value: '', children: [], textContent: '', setAttribute() {}, addEventListener() {}, querySelectorAll: () => [], append(...nodes) { this.children.push(...nodes); }, prepend(node) { this.children.unshift(node); }, replaceChildren(...nodes) { this.children = nodes; }, remove() { if (this.id) elements.delete(this.id); } });
  const document = {
    currentScript: { src: 'http://localhost/i18n.js' }, readyState: 'complete', head: make(), body: make(),
    documentElement: { dataset: {}, classList: { add: x => classes.add(x), remove: x => classes.delete(x) } },
    createElement: make, getElementById: id => elements.get(id), querySelectorAll: () => [],
  };
  const selector = make(); elements.set('lang-select', selector);
  const fetch = async url => {
    const lang = String(url).match(/([^/]+)\.json$/)[1];
    await new Promise(resolve => setTimeout(resolve, delay[lang] || 0));
    if (fail.includes(lang)) throw new Error('Offline');
    return { ok: true, json: async () => tables[lang] };
  };
  const context = vm.createContext({ document, navigator: { languages: [language], language }, location: { reload() {} }, localStorage: {
    getItem() { if (storageError) throw new Error('Disabled'); return stored; },
    setItem(k, v) { if (storageError) throw new Error('Disabled'); writes.push([k, v]); },
  }, URL, AbortController, setTimeout, clearTimeout, Event, console: { warn() {}, error() {} }, fetch });
  context.window = context;
  context.dispatchEvent = event => events.push(event.type);
  vm.runInContext(loader, context);
  vm.runInContext(fs.readFileSync(path.join(web, 'ui-messages.js'), 'utf8'), context);
  return { I18n: context.I18n, document, selector, classes, writes, events };
}

test('all existing languages initialize asynchronously with native selector names', async () => {
  for (const lang of Object.keys(catalogs)) {
    const e = environment({ language: lang.replaceAll('_', '-') });
    assert.ok(e.classes.has('i18n-loading'));
    assert.equal(await e.I18n.ready, true);
    assert.equal(e.I18n.lang, lang);
    assert.equal(e.I18n.t('login.welcome'), catalogs[lang]['login.welcome']);
    assert.equal(e.selector.value, lang);
    assert.equal(e.classes.has('i18n-loading'), false);
  }
});

test('switching persists the language and emits the compatible event', async () => {
  const e = environment({ stored: 'es', language: 'fr' });
  await e.I18n.ready;
  assert.equal(e.I18n.lang, 'es');
  assert.equal(await e.I18n.setLanguage('pt'), true);
  assert.equal(e.I18n.lang, 'pt');
  assert.deepEqual(e.writes, [['openstreamms.lang', 'pt']]);
  assert.deepEqual(e.events, ['langchange']);
  assert.equal(await e.I18n.setLanguage('xx'), false);
});

test('English fallback covers unsupported browsers, missing keys and failed locale downloads', async () => {
  for (const options of [{ language: 'zz-ZZ' }, { language: 'fr', fail: ['fr'] }, { language: 'pt', tables: { ...catalogs, pt: {} } }]) {
    const e = environment(options); await e.I18n.ready;
    assert.equal(e.I18n.t('login.welcome'), catalogs.en['login.welcome']);
    assert.notEqual(e.I18n.t('missing.internal.key'), 'missing.internal.key');
  }
});

test('failure of English produces a visible recovery screen, never keys', async () => {
  const e = environment({ language: 'fr', fail: ['en'] });
  assert.equal(await e.I18n.ready, false);
  assert.match(e.document.body.children[0].textContent, /could not load/);
  assert.equal(e.classes.has('i18n-loading'), false);
});

test('last language selection wins even when requests finish out of order', async () => {
  const e = environment({ delay: { fr: 30, es: 1 } }); await e.I18n.ready;
  await Promise.all([e.I18n.setLanguage('fr'), e.I18n.setLanguage('es')]);
  assert.equal(e.I18n.lang, 'es');
  assert.deepEqual(e.events, ['langchange']);
});

test('disabled storage does not break startup or switching', async () => {
  const e = environment({ storageError: true }); await e.I18n.ready;
  assert.equal(await e.I18n.setLanguage('fr'), true);
});

test('HTML parameters are escaped and interpolation is not recursive', async () => {
  const e = environment(); await e.I18n.ready;
  assert.equal(e.I18n.t('seat.hwAssigned', { device: '{name}', name: 'X' }), '{name} → X');
  assert.equal(e.I18n.html('edit.titleFor', { name: '<img src=x onerror=alert(1)>' }), 'Edit — &lt;img src=x onerror=alert(1)&gt;');
});

test('unsafe downloaded markup is rejected and falls back to English', async () => {
  const e = environment({ language: 'fr', tables: { ...catalogs, fr: { 'login.welcome': '<img src=x onerror=alert(1)>' } } });
  await e.I18n.ready;
  assert.equal(e.I18n.lang, 'en');
});

test('browser and stored language tags accept hyphens, underscores, regions and Chinese scripts', async () => {
  for (const [tag, code] of Object.entries({ 'pt-BR':'pt_BR', 'PT_br':'pt_BR', 'en-gb':'en_GB', 'en_US':'en_US', 'zh-Hant':'zh_TW', 'zh-HK':'zh_TW', 'zh-Hans-CN':'zh', 'de-DE':'de', 'uk_UA':'uk' })) {
    for (const options of [{ language:tag }, { stored:tag }]) {
      const e = environment(options); await e.I18n.ready;
      assert.equal(e.I18n.lang, code, tag);
      assert.equal(e.document.documentElement.lang, code.replaceAll('_','-'));
    }
  }
});

test('regional catalogs inherit missing keys from their base before English', async () => {
  for (const [code, base] of Object.entries({pt_BR:'pt',zh_TW:'zh',en_GB:'en',en_US:'en'})) {
    const e = environment({ language:code, tables:{...catalogs,[code]:{}} }); await e.I18n.ready;
    assert.equal(e.I18n.t('login.welcome'),catalogs[base]['login.welcome'],code);
  }
});

test('driver logs localize real operation messages without losing diagnostic details', async () => {
  const fixture = require('./fixtures/driver-log.json');
  const e = environment({ language: 'ru' }); await e.I18n.ready;
  for (const {raw, ru} of fixture) assert.equal(e.I18n.driverLog(raw), ru);
  assert.equal(e.I18n.driverServiceState('Running'), 'Работает');
  assert.equal(e.I18n.driverServiceState('StartPending'), 'StartPending');
  assert.equal(e.I18n.driverServiceState(null), '—');
  for (const lang of Object.keys(catalogs)) {
    await e.I18n.setLanguage(lang);
    const line = e.I18n.driverLog(fixture[1].raw);
    assert.ok(line.startsWith('[13:35:17] '), lang);
    assert.ok(line.endsWith('C:\\Tests\\drivers\\ViGEmBus_1.22.0_x64_x86_arm64.exe'), lang);
    assert.ok(!/driver\.log\./.test(line), lang);
    assert.equal(e.I18n.driverLog(fixture.at(-1).raw), fixture.at(-1).raw, lang);
  }
  await e.I18n.setLanguage('ru');
  assert.equal(e.I18n.driverLog(fixture[0].raw), fixture[0].ru);
  await e.I18n.setLanguage('en');
  assert.match(e.I18n.driverLog(fixture[0].raw), /Start: Install/);
});

test('missing driver log translations fall back to English with intact parameters', async () => {
  const ru = {...catalogs.ru}; delete ru['driver.log.exitCode'];
  const e = environment({language:'ru',tables:{...catalogs,ru}}); await e.I18n.ready;
  assert.equal(e.I18n.driverLog('[01:02:03] Instalador salió con código 1603.'), '[01:02:03] Installer exited with code 1603.');
});

test('tray, installer and backend catalogs have matching keys, formatting and no empty translations', () => {
  const codes=Object.keys(catalogs);
  for (const folder of ['Services/TrayApp/Locales','Core/Installer/messages','Services/Localization/Locales']) {
    const english=JSON.parse(fs.readFileSync(path.join(root,folder,'en.json'),'utf8'));
    for (const lang of codes) {
      const table=JSON.parse(fs.readFileSync(path.join(root,folder,lang+'.json'),'utf8'));
      assert.deepEqual(Object.keys(table).sort(),Object.keys(english).sort(),folder+':'+lang);
      for (const [key,value] of Object.entries(table)) {
        assert.ok(value.trim(),lang+':'+key);
        assert.deepEqual(value.match(/\{\w+\}|%[n1-9]/g)?.sort()||[],english[key].match(/\{\w+\}|%[n1-9]/g)?.sort()||[],lang+':'+key);
      }
    }
  }
});

test('same-version installer upgrade excludes the mutable service configuration from wildcard copying', () => {
  const script=fs.readFileSync(path.join(root,'Core/Installer/setup.iss'),'utf8');
  const wildcard=script.split('\n').find(line=>line.startsWith('Source: "publish\\*"'));
  assert.match(wildcard,/Excludes: "service\.config\.json"/);
  assert.match(script,/Source: "publish\\service\.config\.json";[^\r\n]*onlyifdoesntexist uninsneveruninstall/);
  assert.match(script,/AppId=\{\{F2A1B3C4-D5E6-7890-ABCD-EF1234567891\}/);
});
