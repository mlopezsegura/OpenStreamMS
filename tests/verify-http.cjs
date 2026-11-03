const assert = require('node:assert/strict');
const path = require('node:path');
const fs = require('node:fs');
const origin = process.argv[2] || 'http://127.0.0.1:51972';
(async () => {
  for (const lang of JSON.parse(fs.readFileSync(path.join(__dirname,'../Core/Localization/locales.json'),'utf8')).locales) {
    const response = await fetch(origin + '/locales/' + lang + '.json', {redirect:'manual'});
    assert.equal(response.status,200,lang);
    assert.match(response.headers.get('content-type'),/application\/json/);
    const table = await response.json();
    assert.ok(table['login.welcome'] && table['setup.title']);
    console.log('PASS unauthenticated GET /locales/' + lang + '.json: HTTP 200 application/json, ' + Object.keys(table).length + ' keys');
  }
  for (const url of ['/login.html','/setup.html','/i18n.js','/ui-messages.js']) {
    assert.equal((await fetch(origin + url,{redirect:'manual'})).status,200,url);
  }
  for (const url of ['/service.config.json','/locales/private.json','/index.html']) {
    assert.equal((await fetch(origin + url,{redirect:'manual'})).status,302,url);
  }
  console.log('PASS public assets available; config, unknown locale and dashboard still require authentication in the validation host.');
})().catch(e=>{console.error(e);process.exitCode=1;});
