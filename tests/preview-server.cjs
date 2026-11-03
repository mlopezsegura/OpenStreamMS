// Visual-only harness: serves the published UI with inert API fixtures.
// Does not import or launch any OpenStreamMS service or native component.
const fs = require('node:fs');
const path = require('node:path');
const http = require('node:http');
const web = path.resolve(process.argv[2]);
const port = Number(process.argv[3] || 51973);
const host = process.argv[4] || '127.0.0.1';
const fixtureSession = { id:'11111111-1111-1111-1111-111111111111', name:'Тестовый сеанс', state:'Stopped', username:'testuser', domain:'.', local:false, isolatedUser:null, enabled:false, rdpWidth:1920, rdpHeight:1080, rdpFrameRate:60, sunshineStreamPort:47989, streamProtocol:'Both', webRtcPort:8000 };
http.createServer((req,res) => {
  const url = new URL(req.url, 'http://localhost');
  const json = (value,status=200) => { res.writeHead(status,{'Content-Type':'application/json; charset=utf-8'}); res.end(JSON.stringify(value)); };
  if (url.pathname.startsWith('/api/')) {
    if (req.method !== 'GET') return json('Visual test fixture: no system action was performed.',400);
    if (url.pathname === '/api/sessions') return json([fixtureSession]);
    if (url.pathname.endsWith('/status')) return json({ installed:true,active:true,busy:false });
    if (url.pathname.endsWith('/logs') || url.pathname.endsWith('/log')) return json({ lines:[],linesReturned:0 });
    if (url.pathname === '/api/settings') return json({logLevel:'Error'});
    if (url.pathname === '/api/seats/inventory') return json({monitors:[{devicePath:'TEST-DISPLAY',friendlyName:'Тестовый монитор',width:1920,height:1080,active:true,primary:false}],audioOutputs:[{id:'test-output',name:'Тестовый аудиовыход'}],audioInputs:[{id:'test-mic',name:'Тестовый микрофон'}],devices:[{kind:'Keyboard',usbPort:'TEST-USB-1',name:'Тестовая клавиатура'},{kind:'Mouse',usbPort:'TEST-USB-2',name:'Тестовая мышь'}]});
    if (url.pathname === '/api/sessions/ports/suggest') return json({ sunshineStreamPort:48010,webRtcPort:8010,webRtcMediaPortMin:48100,webRtcMediaPortMax:48200 });
    if (url.pathname === '/api/system/network') return json({ addresses:['192.0.2.10'] });
    return json([]);
  }
  const file = path.resolve(web, '.' + url.pathname.replace(/\/$/, '/index.html'));
  if (!file.startsWith(web + path.sep) || !fs.existsSync(file) || !fs.statSync(file).isFile()) { res.writeHead(404).end(); return; }
  res.writeHead(200,{'Content-Type':({'.html':'text/html; charset=utf-8','.js':'text/javascript; charset=utf-8','.css':'text/css; charset=utf-8','.json':'application/json; charset=utf-8','.svg':'image/svg+xml'})[path.extname(file)]||'application/octet-stream'});
  fs.createReadStream(file).pipe(res);
}).listen(port,host,()=>console.log(`Inert published-UI preview: http://${host}:${port}`));
