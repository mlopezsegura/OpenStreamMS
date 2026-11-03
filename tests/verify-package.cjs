const fs = require('node:fs');
const path = require('node:path');
const assert = require('node:assert/strict');
const crypto = require('node:crypto');
const root = path.resolve(__dirname, '..');
const source = path.join(root, 'Core/wwwroot');
const publish = path.resolve(process.argv[2] || path.join(root,'Core/Installer/publish'));
const build = path.join(root,'bin/Release/net10.0-windows/wwwroot');
const hash = file => crypto.createHash('sha256').update(fs.readFileSync(file)).digest('hex');
const result = { dictionaries: [], installer: {}, package: [] };
assert.equal(hash(path.join(root,'THIRD_PARTY_NOTICES.md')),hash(path.join(publish,'THIRD_PARTY_NOTICES.md')));
assert.equal(hash(path.join(root,'Core/Installer/Languages/InnoSetup-LICENSE.txt')),hash(path.join(publish,'licenses/InnoSetup-LICENSE.txt')));
const languages=JSON.parse(fs.readFileSync(path.join(root,'Core/Localization/locales.json'),'utf8')).locales;
assert.deepEqual(languages.slice().sort(),fs.readdirSync(path.join(root,'Sunshine/assets/web/assets/locale')).filter(f=>f.endsWith('.json')).map(f=>f.slice(0,-5)).sort());
for (const lang of languages) {
  const relative = 'locales/' + lang + '.json';
  const original = path.join(source,relative);
  const built = path.join(build,relative);
  const published = path.join(publish,'wwwroot',relative);
  assert.equal(hash(original),hash(built), 'build:' + relative);
  assert.equal(hash(original),hash(published), 'publish:' + relative);
  result.dictionaries.push({ language:lang,keys:Object.keys(JSON.parse(fs.readFileSync(original,'utf8'))).length,path:'/locales/'+lang+'.json',sha256:hash(published) });
}
for (const relative of ['OpenStreamMS.exe','OpenStreamMS.dll','hostfxr.dll','coreclr.dll','FreeRDP/wfreerdp.exe','Sunshine/sunshine.exe','steam/osms-steamhook.dll','steam/osms-steam.exe','remoteidd-pkg/RemoteIdd.dll','remoteidd-pkg/RemoteIdd.inf','remoteidd-pkg/RemoteIdd.cat','remoteidd-pkg/RemoteIdd.cer','remoteidd-pkg/OsmsRdsProvider.dll','remoteidd-pkg/install.ps1','remoteidd-pkg/uninstall.ps1','remoteidd-pkg/termservice.ps1']) {
  assert.ok(fs.existsSync(path.join(publish,relative)), 'Missing package file:' + relative);
  result.package.push(relative);
}
assert.ok(fs.readdirSync(path.join(publish,'drivers')).some(f=>f.startsWith('ViGEmBus')));
assert.ok(fs.readdirSync(path.join(publish,'drivers')).some(f=>f.startsWith('HidHide')));
const script = fs.readFileSync(path.join(root,'Core/Installer/setup.iss'),'utf8');
const languageSection=fs.readFileSync(path.join(root,'Core/Installer/languages.generated.iss'),'utf8');
assert.match(languageSection,/Name: "russian"; MessagesFile: "compiler:Languages\\Russian.isl"/);
assert.equal([...languageSection.matchAll(/^Name:/gm)].length,22);
const section = fs.readFileSync(path.join(root,'Core/Installer/messages.generated.iss'),'utf8');
const entries = lang => Object.fromEntries([...section.matchAll(new RegExp('^'+lang+'\\.([^=]+)=(.*)$','gm'))].map(m=>[m[1],m[2].trim()]));
const english=entries('english'),russian=entries('russian');
assert.deepEqual(Object.keys(english).sort(),Object.keys(russian).sort());
for (const [key,value] of Object.entries(russian)) {
  assert.match(value,/[А-Яа-яЁё]/,key);
  assert.deepEqual(value.match(/%\w|\{[^}]+\}/g)||[],english[key].match(/%\w|\{[^}]+\}/g)||[],key);
}
result.installer={languages:22,russianCustomMessages:Object.keys(russian).length,standardLanguageResource:'compiler:Languages\\Russian.isl',appId:script.match(/^AppId=(.*)$/m)[1].trim(),product:'OpenStreamMS'};
console.log(JSON.stringify(result,null,2));
