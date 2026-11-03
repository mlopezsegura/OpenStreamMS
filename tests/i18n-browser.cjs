// Safe UI harness: never starts OpenStreamMS or talks to an installed service.
const fs = require('node:fs');
const path = require('node:path');
const http = require('node:http');
const assert = require('node:assert/strict');
const { chromium } = require('playwright');
const web = path.resolve(process.env.I18N_WEB_ROOT || path.join(__dirname, '../Core/wwwroot'));
const output = path.resolve(process.env.I18N_TEST_OUTPUT || path.join(__dirname, '../artifacts/i18n'));
fs.mkdirSync(output, { recursive: true });
const server = http.createServer((req, res) => {
  const file = path.resolve(web, '.' + new URL(req.url, 'http://localhost').pathname.replace(/\/$/, '/index.html'));
  if (!file.startsWith(web + path.sep) || !fs.existsSync(file)) { res.writeHead(404).end(); return; }
  const types = { '.js': 'text/javascript', '.json': 'application/json', '.css': 'text/css', '.html': 'text/html', '.svg': 'image/svg+xml' };
  res.writeHead(200, { 'Content-Type': types[path.extname(file)] || 'application/octet-stream' });
  fs.createReadStream(file).pipe(res);
});
const results = [];
(async () => {
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  const origin = process.env.I18N_BASE_URL || 'http://127.0.0.1:' + server.address().port;
  const browser = await chromium.launch({ channel: 'msedge', headless: true });
  try {
    const langs = fs.readdirSync(path.join(web, 'locales')).map(f => f.slice(0, -5));
    for (const lang of langs) {
      const table = JSON.parse(fs.readFileSync(path.join(web, 'locales', lang + '.json'), 'utf8'));
      const context = await browser.newContext({ locale: lang.replaceAll('_', '-'), colorScheme: 'dark', reducedMotion: 'reduce', viewport: { width: 1280, height: 900 } });
      const errors = [];
      const dictionaries = [];
      const page = await context.newPage();
      page.on('pageerror', e => errors.push(e.message));
      page.on('response', r => { if (/\/locales\/\w+\.json$/.test(r.url())) dictionaries.push({ path: new URL(r.url()).pathname, status: r.status() }); });
      // All API calls are intercepted; no host-side effects are possible.
      await page.route('**/api/**', route => {
        const url = new URL(route.request().url());
        let body = [];
        if (url.pathname.endsWith('/status')) body = { installed: true, active: true, busy: false };
        if (url.pathname === '/api/settings') body = { logLevel: 'Error' };
        if (url.pathname === '/api/seats/inventory') body = { monitors: [], audioOutputs: [], audioInputs: [], devices: [] };
        return route.fulfill({ json: body });
      });
      for (const file of ['login.html', 'setup.html', 'index.html']) {
        if (file === 'index.html' && process.env.I18N_BASE_URL) await context.addCookies([{ name: 'validation-session', value: '1', url: origin }]);
        await page.goto(origin + '/' + file);
        assert.equal(await page.evaluate(() => I18n.ready), true);
        await page.waitForFunction(() => !document.documentElement.classList.contains('i18n-loading'));
          assert.equal(await page.locator('html').getAttribute('lang'), lang.replaceAll('_', '-'));
        const mismatches = await page.evaluate(() => [...document.querySelectorAll('[data-i18n]')].filter(el => el.textContent !== t(el.dataset.i18n)).map(el => el.dataset.i18n));
        assert.deepEqual(mismatches, [], lang + ':' + file);
        if (file === 'index.html') {
          await page.waitForFunction(() => document.querySelector('#sessions-grid').textContent.includes(t('ux.onb.title')));
          await page.evaluate(() => openNewModal());
          assert.equal(await page.locator('#sess-title').textContent(), table['new.title']);
          await page.screenshot({ path: path.join(output, lang + '-new-session.png'), fullPage: true });
          await page.evaluate(() => closeSessionModal());
          await page.screenshot({ path: path.join(output, lang + '-dashboard.png'), fullPage: true });
          await page.evaluate(() => switchTab('seats'));
          await page.waitForFunction(() => document.querySelector('#seats-grid').textContent.includes(t('seat.empty')));
          await page.evaluate(() => openSeatModal());
          await page.locator('#seat-form input[name="Name"]').fill('Тестовое рабочее место');
          await page.evaluate(() => I18n.setLanguage('en'));
          await page.evaluate(l => I18n.setLanguage(l), lang);
          assert.equal(await page.locator('#seat-form input[name="Name"]').inputValue(), 'Тестовое рабочее место');
          assert.equal(await page.locator('#seat-modal-title').textContent(), table['seat.newTitle']);
          await page.screenshot({ path: path.join(output, lang + '-new-seat.png'), fullPage: true });
          await page.evaluate(() => closeSeatModal());
        }
        await page.screenshot({ path: path.join(output, lang + '-' + file.replace('.html', '') + '.png'), fullPage: true });
        results.push({ lang, page: file, status: 'passed' });
      }
      await page.evaluate(() => I18n.setLanguage('en'));
      await page.reload(); await page.evaluate(() => I18n.ready);
      assert.equal(await page.locator('html').getAttribute('lang'), 'en');
      if (lang !== 'en') {
        await page.evaluate(l => I18n.setLanguage(l), lang);
        assert.equal(await page.locator('html').getAttribute('lang'), lang.replaceAll('_', '-'));
      }
      assert.deepEqual(errors, [], lang + ' page errors');
      assert.ok(dictionaries.some(r => r.path === '/locales/en.json' && r.status === 200));
      assert.ok(dictionaries.some(r => r.path === '/locales/' + lang + '.json' && r.status === 200));
      assert.ok(dictionaries.every(r => r.status === 200));
      if (lang === 'ru') {
        const messages = await page.evaluate(() => ({ required: I18n.message("El campo 'Name' es obligatorio."), status: I18n.status('Encendiendo el monitor…'), fallback: t('nonexistent.key') }));
        assert.equal(messages.required, 'Не заполнено обязательное поле: Название.');
        assert.equal(messages.status, 'Включение монитора…');
        assert.equal(messages.fallback, table['toast.translationUnavailable']);
      }
      if (['ru','de','ja','ko','zh','zh_TW','uk'].includes(lang)) {
        for (const width of [375, 720, 1280]) for (const colorScheme of ['light', 'dark']) {
          await page.setViewportSize({ width, height: 900 });
          await page.emulateMedia({ colorScheme, reducedMotion: 'reduce' });
          for (const file of ['login.html','setup.html','index.html']) {
            await page.goto(origin + '/' + file); await page.evaluate(() => I18n.ready);
            if (file === 'index.html') await page.evaluate(() => openNewModal());
            const overflow = await page.evaluate(() => [...document.querySelectorAll('.auth-box .btn, .modal:not(.hidden) .btn, .modal-actions .btn, .modal-footer .btn')]
              .filter(el => el.getClientRects().length && (el.scrollWidth > el.clientWidth + 1 || el.getBoundingClientRect().right > innerWidth + 1 || el.getBoundingClientRect().left < -1))
              .map(el => el.textContent));
            assert.deepEqual(overflow, [], `${lang} ${width} ${colorScheme} ${file}: clipped action text`);
            await page.screenshot({ path: path.join(output, `${lang}-${width}-${colorScheme}-${file.replace('.html','')}.png`), fullPage: true });
          }
        }
        results.push({ lang, check: 'responsive action text at 375/720/1280, dark/light; localized errors/status', status: 'passed' });
      }
      results.push({ lang, check: 'HTTP dictionary paths, switching, persistence, unsaved seat fields', status: 'passed', dictionaries });
      await context.close();
    }
    // Real browser fallback: missing key, unavailable selected dictionary, and
    // unavailable English dictionary each exercise a separate startup path.
    for (const mode of ['missing-key', 'missing-locale', 'missing-english']) {
      const context = await browser.newContext({ locale: 'ru-RU' });
      const page = await context.newPage();
      await page.route('**/locales/*.json', async route => {
        const lang = new URL(route.request().url()).pathname.split('/').pop();
        if ((mode === 'missing-locale' && lang === 'ru.json') || (mode === 'missing-english' && lang === 'en.json')) return route.fulfill({ status: 503, body: '' });
        const table = JSON.parse(fs.readFileSync(path.join(web, 'locales', lang), 'utf8'));
        if (mode === 'missing-key' && lang === 'ru.json') delete table['login.welcome'];
        return route.fulfill({ json: table });
      });
      await page.goto(origin + '/login.html');
      const ready = await page.evaluate(() => I18n.ready);
      if (mode === 'missing-english') { assert.equal(ready, false); assert.match(await page.locator('body').innerText(), /could not load its translations/); }
      else { assert.equal(ready, true); assert.equal(await page.locator('h1').textContent(), 'Welcome'); }
      results.push({ check: 'fallback-' + mode, status: 'passed' });
      await context.close();
    }
    fs.writeFileSync(path.join(output, 'browser-results.json'), JSON.stringify(results, null, 2));
    console.log('PASS:', results.length, `checks: ${langs.length*3} page/language combinations, ${langs.length} switching/persistence checks, representative responsive cases, 3 fallback cases.`);
  } finally { await browser.close(); }
})().catch(e => { console.error(e); process.exitCode = 1; }).finally(() => server.close());
