# RemoteIdd — guía completa

Todo lo necesario para retomar RemoteIdd sin contexto previo: qué es, por qué está hecho
así, cómo se compila, cómo se prueba en un PC real, cómo se deshace y qué falta.
Detalle de tareas en `TASKS.md`; resumen y cifras en `README.md`.

Estado (2026-10-27): **implementado y probado sin instalar** (tests unitarios, de
rendimiento entre procesos y de Sunshine en verde). **Falta la prueba con un puesto real**
(sección 5), que exige administrador y firma de test. El primer intento falló porque
Windows Pro no carga el proveedor sin parchear `termsrv`; ya se instala TermWrap para eso
(sección 8).

---

## 1. Qué es y para qué

Un puesto de OpenStreamMS es una sesión RDP local cuyo escritorio captura Sunshine y envía
al cliente. Hoy Sunshine captura esa sesión con DDA/WGC sobre la pantalla remota de
Microsoft (RdpIdd). RemoteIdd sustituye esa pantalla por un **driver de pantalla propio**
que entrega cada frame directamente a Sunshine por memoria de GPU compartida, sin pasar por
DDA/WGC ni por la codificación RDP.

```
 sesión del puesto (escritorio, juego)
        │ composición DWM
        ▼
 RemoteIdd.dll (driver IddCx, WUDFHost, sesión 0)
        │ FramePublisher: copia a textura compartida (handle NT + keyed mutex)
        │ + slot con seqlock + evento  → Global\OsmsRidd<sesión>*
        ▼
 Sunshine (SYSTEM, sesión del puesto), capture = remoteidd
        │ FrameConsumer: abre la textura, copia y suelta
        ▼
 encoder (AMF/NVENC…) → cliente
```

Quién carga el driver: Terminal Services, a petición del **proveedor de protocolo**
`OsmsRdsProvider.dll`, registrado en RDP-Tcp. Solo las conexiones de puesto, es decir, las
RDP desde el propio equipo (OpenStreamMS conecta a `127.0.0.2`; el origen puede declararse
`127.0.0.1`), reciben RemoteIdd. Cualquier conexión que llegue por la red sigue entera con
el protocolo y el RdpIdd de Microsoft, salvo que se instale con `-AllConnections`: entonces
también las sesiones RDP que llegan por la red reciben RemoteIdd (valor
`OsmsRemoteIddAllConnections` en `WinStations\RDP-Tcp`).

## 2. Decisiones de diseño (no reabrir sin motivo)

- **Nada de hooks ni inyección en el camino de los frames.** DuoStream inyecta `DuoIdd.dll` en el
  `RdpIdd` de Microsoft y engancha funciones internas por offset: frágil ante cada actualización de
  Windows. Aquí el driver y el proveedor solo usan APIs documentadas: IddCx, `wtsprotocol.h`, D3D11.
- **TermWrap en vez de RDP Wrapper de asmtron (decidido 2026-10-27).** En Windows cliente,
  `termsrv` rechaza cualquier pila de protocolo que no sea la de Microsoft
  (`CRemoteConnectionManager::IsAllowNonRDPStack`): con el proveedor en RDP-Tcp, TermService ni
  carga la DLL ni levanta RDP-Tcp (sin eventos 20523/258, nada en el 3389, log vacío). asmtron
  no parchea eso; TermWrap (llccd, MIT) sí (`NonRDPPatch`, junto con `IsAppServerInstalled` y la
  multisesión), y busca las direcciones desensamblando `termsrv` (Zydis), sin `.ini` por
  versión. DuoStream trae su propio `TermWrap.dll` por lo mismo. Es el único parche, y está
  en `termsrv` (lo mismo que ya hacía asmtron), no en el camino de los frames.
- **El proveedor no implementa RDP**: envuelve al protocolo de Microsoft (`rdpcorets.dll`,
  CLSID `{5828227c-20cf-4408-b73f-73ab70b8849f}`, el mismo de RDP-Tcp) y le delega todas
  las llamadas. Solo responde por su cuenta a `IWRdsWddmIddProps`/`IWRdsWddmIddProps1`:
  `GetHardwareId` → `OsmsRemoteIdd_IndirectDisplay` (así Windows carga RemoteIdd y no
  RdpIdd) y `OnDriverLoad` → pasa la sesión al driver por IOCTL.
- **En RDP-Tcp, no en un listener aparte.** Windows cliente (Pro) solo levanta el listener
  RDP-Tcp: un listener propio (`OSMS-Seat`) se probó el 2026-10-27 y Terminal Services no lo
  arranca (evento 1136, sin rol de Host de sesión). DuoStream hace lo mismo: su `DuoRds.dll`
  agrega el listener de RDP-Tcp. La decisión es por conexión: solo las que vienen del propio
  equipo (loopback) reciben RemoteIdd; en el resto, hasta las interfaces del IDD se delegan
  en Microsoft.
- **Mínima invasión**: un solo valor de RDP-Tcp cambiado (el original se guarda y se
  restaura), sin dispositivo raíz (el dispositivo lo crea Terminal Services por sesión),
  driver solo en el Driver Store, objetos efímeros con DACL mínima (SYSTEM, LocalService,
  administradores), desinstalación completa.
- **Base de SO mínima**: IddCx 1.4 + UMDF 2.33 (Windows 10 1903+).
- **Limitador por llegada**: el driver nunca espera a Sunshine. Si Sunshine tiene la
  textura (keyed mutex ocupado) el frame se pierde; la sesión no se frena.
- **Sin WDK instalado**: se compila contra el paquete NuGet oficial del WDK.

## 3. Componentes

| Pieza | Archivo | Notas |
|---|---|---|
| Contrato de frames | `SharedFrame.h` | Slot v2 con seqlock, `TextureGeneration`, nombres y DACL. |
| Productor | `FramePublisher.*` | Lo usa el driver; textura NT + keyed mutex, nunca bloquea. |
| Consumidor | `FrameConsumer.*` | Copiado en sunshine-webrtc `src/platform/windows/remoteidd/` (no editar allí). |
| Driver | `Driver.*`, `RemoteIdd.inf` | IddCx remoto; modos, swapchain, IOCTL de sesión/modo. |
| Canal proveedor→driver | `RemoteIddIoctl.h` | Hardware id, interfaz de dispositivo, `IOCTL_REMOTEIDD_SET_SESSION` v2. |
| Proveedor RDS | `Provider/RdsProvider.*` | `OsmsRdsProvider.dll`, envoltorio de rdpcorets. |
| Instalación de prueba | `Provider/install.ps1`, `uninstall.ps1` | Con fichero de estado para deshacer exactamente. |
| Tests | `test/*.cpp`, `test/verify-seat.ps1` | Ver sección 4. |
| Sunshine | sunshine-webrtc: `display_remoteidd.cpp`, `display_vram.cpp`, `display_base.cpp` | Backend `capture = remoteidd`, nunca automático. |
| OpenStreamMS | `RdpSessionCreator.cs`, `StreamSessionService.cs`, `wwwroot` | Captura `remoteidd` → comprueba el proveedor en RDP-Tcp y conecta a `127.0.0.2`. |

Detalles que costó encontrar:
- Todo IDD necesita `UpperFilters = IndirectKmd` en el INF (lo pone también RdpIdd).
- El hardware id de un IDD remoto es un nombre plano, como `RdpIdd_IndirectDisplay`.
- `IddCxCheckOsFeatureSupport` no existe hasta IddCx 1.11 (no está en el WDK 26100).
- `DriverCLSID` es de UMDF1; en UMDF2 sobra. Falta obligatoria: `UmdfExtensions = IddCx0102`,
  **en la sección del servicio** (`[RemoteIdd_Service]`, como `rdpidd.inf`). En la sección
  `.Wdf` se ignora sin que `infverif` avise: el dispositivo queda en `CM_PROB_FAILED_ADD`
  (0xC0000001) y rdpcorets corta la sesión con `ERRINFO_CLOSE_STACK_ON_DRIVER_FAILURE` (0x11).
- El DACL del dispositivo tiene que incluir a NetworkService (`NS`): el proveedor abre el
  dispositivo desde TermService, que corre con esa cuenta.
- La sesión no se puede deducir en el driver (el WUDFHost está en la sesión 0): la manda el
  proveedor. Sin sesión el driver no publica (dos puestos se pisarían).
- RDP no transmite los Hz del cliente: salen de `OsmsRefreshHz` (DWORD) en
  `WinStations\RDP-Tcp` (`install.ps1 -RefreshHz`). La resolución sí: `HRes`/`VRes` de
  `GetClientData`, que el proveedor lee al delegar (la estructura lleva la contraseña: no
  se copia ni se registra nada más).
- `wcscpy_s` con búfer corto **aborta el proceso**; en el proveedor eso tumbaría el svchost
  de Terminal Services. Comprobar longitudes antes.
- MinGW (Sunshine) no tiene `ReadAcquire64`: el seqlock usa lecturas volatile + barreras.
- `_wgetenv` no ve `SetEnvironmentVariableW`: usar `GetEnvironmentVariableW`.
- Un handle NT nuevo puede reutilizar el valor del anterior: el consumidor reabre por
  `TextureGeneration`, no por valor de handle.
- Crear objetos en `Global\` exige SeCreateGlobalPrivilege: los tests usan `Local\`.
- Windows cliente no levanta listeners adicionales (ni copiando RDP-Tcp en otro puerto: además
  se copian los puertos WebSocket 3387/3392 y chocan). Por eso el proveedor va en RDP-Tcp.
  (El evento 1136 lo da `CSLQuery::IsAppServerInstalled`, que TermWrap también parchea: con él
  quizá volvería a ser posible un listener aparte; no se ha probado.)
- Sin TermWrap, un proveedor no-Microsoft en RDP-Tcp **deja el equipo sin RDP**: TermService
  arranca pero RDP-Tcp no escucha. Síntoma: mstsc no conecta y `remoteidd-provider.log` vacío.
- El "Instalar/Actualizar" de RDP Wrapper en OpenStreamMS ejecuta `autoupdate.bat`, que vuelve a
  poner `rdpwrap.dll` como ServiceDll si no lo encuentra: no usarlo con RemoteIdd instalado.
- Scripts en Windows en español: el grupo es "Administradores" y `bcdedit` dice "Sí". Se
  usan el rol integrado y `SystemStartOptions` del registro, que no dependen del idioma.
- `regsvr32` es gráfico: PowerShell no lo espera; `Start-Process -Wait -PassThru`.

## 4. Compilar y probar sin instalar

Requisito: Visual Studio / Build Tools con C++.

```
Native\RemoteIdd\build.cmd          (salida en Native\RemoteIdd\bin)
```

Descarga una vez el WDK NuGet (~110 MB) en `%LOCALAPPDATA%\OpenStreamMS\wdk-nuget` y, en
orden, para en el primer fallo:
1. `SharedFrameTest` — contrato, seqlock (0 lecturas rasgadas), limitador por llegada.
2. `RemoteIddPerf` — 6 escenarios entre procesos en la GPU real (`set RIDD_NOPERF=1` lo salta).
3. `RemoteIddProviderTest` — proveedor con objetos falsos (delegación, hardware id, sesión,
   resolución, sin fugas de referencias).
4. `OsmsRdsProvider.dll`, `RemoteIdd.dll` (`/W4 /WX`), INF sellado y validado con `infverif`.

Sunshine (MSYS2 UCRT64): `ninja -C cmake-build-webrtc sunshine test_sunshine` y
`tests\test_sunshine.exe --gtest_filter=RemoteIdd*` (4 tests; cifras en `test_sunshine.log`).

Rendimiento medido (RX 9060 XT, entre procesos, present → píxel p99): 1080p60 0,71 ms,
4K60 0,82 ms, 4K120 0,63 ms, 4K240 0,56 ms; `Publish()` p99 ≤ 0,27 ms. Tabla completa en
`README.md`.

## 5. Prueba con un puesto real (en el PC)

Todo en **PowerShell como administrador**, con el mismo usuario que compiló (el script
busca el WDK en su `%LOCALAPPDATA%`). El paso 2 desconecta los puestos abiertos.

**1. Firma de prueba**
```powershell
Confirm-SecureBootUEFI      # True → desactivar Secure Boot en la BIOS antes
bcdedit /set testsigning on
Restart-Computer
```
Aparece la marca de agua "Modo de prueba": es normal.

**2. Instalar** (certificado de test, driver, TermWrap como ServiceDll de TermService y
proveedor en RDP-Tcp; descarga TermWrap 0.7 de GitHub y comprueba sus hashes)
```powershell
cd E:\Workspace\OpenStreamMS\Native\RemoteIdd
.\Provider\install.ps1 -Bin .\bin -RefreshHz 120 -RestartTermService
```

**3. Abrir una sesión de puesto** con un usuario de puesto y **desde el propio equipo**
(`127.0.0.2`; solo las conexiones locales reciben RemoteIdd; no con el propio usuario, que
está en la consola). Dejar algo
moviéndose: IddCx solo presenta cuando cambia la pantalla.
```powershell
mstsc /v:127.0.0.2
```

**4. Verificar** (desde el escritorio propio, no desde el puesto)
```powershell
.\test\verify-seat.ps1 -Bin .\bin -User <usuario del puesto>
```
- `RESULTADO: OK` → el driver entrega frames del puesto: funciona de extremo a extremo.
- Sesión en negro o que se cierra → rdpcorets no acepta el IDD propio (riesgo abierto,
  sección 7). Recoger con DebugView (admin, *Capture Global Win32*) las líneas
  `RemoteIdd:` y `OsmsRds:`.

**5. Usarlo en OpenStreamMS** (solo si el paso 4 da OK): instalar la build nueva de
OpenStreamMS (cerrando antes los `OpenStreamMS.exe` de bandeja y puesto, o el DLL queda
bloqueado) y crear un puesto con **Captura → `remoteidd`**. El `Sunshine\sunshine.exe` del
repo ya lleva el backend; la copia de cada sesión se actualiza sola desde la plantilla.

### Deshacer
```powershell
cd E:\Workspace\OpenStreamMS\Native\RemoteIdd
.\Provider\uninstall.ps1 -RestartTermService
bcdedit /set testsigning off
Restart-Computer
```
Y reactivar Secure Boot si se desactivó. `uninstall.ps1` deshace exactamente lo que anotó
`install.ps1` en `%ProgramData%\OpenStreamMS\remoteidd-install.json`: primero devuelve
RDP-Tcp a su protocolo original y TermService a su ServiceDll anterior (rdpwrap.dll), y luego
quita clase COM y DLL del proveedor, los ficheros de TermWrap que copió, driver del Driver
Store y certificado de test.

### Alternativa en VM (Hyper-V)
Válida para comprobar que funciona; las latencias no son representativas (sin GPU real).
VM de generación 2 con TPM virtual y **Secure Boot desactivado** (`Set-VMFirmware
-EnableSecureBoot Off`), `testsigning` dentro de la VM, Escritorio remoto habilitado y
y conexión del puesto desde `127.0.0.2` dentro de la propia VM (es la condición para recibir
RemoteIdd). La verificación se lanza por PowerShell Direct (`Invoke-Command -VMName`).
Pendiente: que `install.ps1` firme en el PC y en la VM solo instale (la VM no tiene el WDK).

## 6. Seguridad y sistema

- `testsigning` es un cambio global del arranque: solo para pruebas. En producción, firma
  *attestation* en el Partner Center de Microsoft (certificado EV); no toca el arranque.
- El proveedor corre dentro del svchost de Terminal Services y está en el camino de **todas**
  las conexiones RDP del equipo: un fallo en él afecta también al RDP normal. Lo mitigan la
  delegación completa fuera de los puestos, los tests con objetos falsos y una desinstalación
  que restaura RDP-Tcp antes que nada.
- Los objetos con nombre solo admiten SYSTEM, LocalService y administradores. El
  dispositivo del driver: los mismos (`Security` en el INF; RdpIdd abre a todos).
- `RemoteIddPerf watch` necesita SeDebugPrivilege para duplicar el handle del WUDFHost;
  Sunshine corre como SYSTEM y no lo necesita.

## 7. Riesgos abiertos y pendientes

- **Riesgo principal**: que `rdpcorets` no acepte una sesión que use un IDD distinto del
  suyo (DuoStream engancha RdpIdd en vez de sustituirlo, lo que sugiere que no es trivial).
  Lo resuelve la prueba de la sección 5. Si falla, no recurrir a hooks: plantearlo antes.
- Prueba con un puesto real (D2): OK el 2026-10-27 con TermWrap (sección 8).
- OpenStreamMS ya detecta TermWrap y vuelve solo a asmtron si falla (sección 8). Falta que
  la UI instale TermWrap; ahora lo hace `install.ps1`. El `TermWrapManager` borrado en el
  commit 2d01aa4 sirve de base.
- `install.ps1` para VM (firmar en el PC, instalar en la VM).
- HDR (IddCx 1.10+) y cursor por hardware: no implementados; el cursor va compuesto en el
  frame y el backend de Sunshine rechaza HDR.
- Ramas: `feature/remote-idd` en OpenStreamMS y en sunshine-webrtc (commits locales, sin
  push ni PR). El `Sunshine/sunshine.exe` de OpenStreamMS se compiló desde el árbol de
  sunshine-webrtc, que incluye también el limitador por llegada (aún sin commit allí).

## 8. Avances

### 2026-10-27 — primer intento de prueba real (D2) y cambio a TermWrap
- **Ya estaba:** `testsigning` activo en el PC; `bin\` compilado a las 20:44.
- **Intento con el proveedor en RDP-Tcp** (20:59, `install.ps1 -RefreshHz 120
  -RestartTermService`): el registro quedó bien (CLSID `{8B0F3C51-…}`, clase COM en HKLM,
  DLL x64 legible por NETWORK SERVICE), pero `mstsc /v:127.0.0.2` no conectaba. TermService
  arrancó sin los eventos 20523/258 de RDP-Tcp, sin nada en el 3389 y con
  `remoteidd-provider.log` vacío. Es decir, ni siquiera llamó a `DllGetClassObject`: la DLL
  no llegó a cargarse. Se desinstaló y el RDP volvió.
- **Causa:** `termsrv` en Windows cliente rechaza las pilas de protocolo que no son de
  Microsoft (`CRemoteConnectionManager::IsAllowNonRDPStack`). Duo lleva su propio
  `TermWrap.dll` con `NonRDPPatch` e `IsAppServerInstalled`, y RDP Wrapper de asmtron no los
  parchea. Es una deducción por las cadenas de Duo y por el síntoma; se confirma si la prueba
  con TermWrap carga el proveedor.
- **Decisión:** usar TermWrap de llccd (v0.7, MIT) en vez de asmtron. Se ha comprobado en su
  `TermWrap/Patch.cpp` que aplica `NonRDPPatch` en x64 (busca las funciones con Zydis).
- **Hecho:**
  - `install.ps1`: paso 4. Descarga `TermWrap-0.7.zip` y comprueba el SHA-256 del zip, de
    `TermWrap.dll` y de `Zydis.dll`. Las copia a `%ProgramFiles%\RDP Wrapper` y pone
    `ServiceDll = %ProgramFiles%\RDP Wrapper\TermWrap.dll`. Guarda en el estado el valor
    anterior y los ficheros copiados.
  - `uninstall.ps1`: restaura RDP-Tcp y `ServiceDll`, reinicia TermService una vez y borra
    solo lo que copió.
  - Los dos scripts pasan el analizador de PowerShell; **aún no se han ejecutado**.
- **21:07, segundo intento con TermWrap: el proveedor carga.** El log muestra `DllGetClassObject`,
  `Initialize`, `CreateListener(RDP-Tcp)` y `StartListen` con 0x00000000; aparecen los eventos
  20523 (con nuestro CLSID) y 258, y el 3389 escucha. Queda confirmada la causa
  (`IsAllowNonRDPStack`).
- **El puesto no mostraba pantalla en Moonlight** porque su sesión (la 2) se abrió a las 21:02, antes
  de instalar: al reiniciar TermService se cortó su RDP y quedó desconectada con el RdpIdd de
  Microsoft. Después no llegó ninguna conexión RDP nueva. Hay que **cerrar el puesto y volver a
  abrirlo** después de instalar. La comprobación es que en el log aparezca
  `RemoteIdd cargado para el puesto`.
- **21:16-21:22, con un puesto nuevo:** Windows crea
  `SWD\RemoteDisplayEnum\OsmsRemoteIdd_IndirectDisplay&SessionId_000N`, es decir, rdpcorets **sí
  acepta** nuestro hardware id. Pero el dispositivo queda en `CM_PROB_FAILED_ADD` (0xC0000001,
  según `setupapi.dev.log`) y la sesión se corta al instante (`SetErrorInfo(0x11)`, motivo 17).
  Causa: `UmdfExtensions` estaba en la sección del INF que no tocaba (ver sección 3). Arreglado,
  junto con el DACL para NetworkService y trazas de fallo en `EvtDeviceAdd`. Compilado a las
  21:24; falta reinstalar.
- **21:26-21:35, con el INF corregido:** `EvtDeviceAdd` ya da 0x00000000, pero
  `IddCxAdapterInitAsync` devuelve **0xC000000D (STATUS_INVALID_PARAMETER)**. Lo muestra el log
  nuevo del driver, `remoteidd-driver.log`, que crea `install.ps1` con escritura para LOCAL
  SERVICE. El dispositivo queda en `CM_PROB_FAILED_START`/`STATUS_DEVICE_REMOVED` y rdpcorets
  corta con `SetErrorInfo(0x11)`. El proveedor también registra ahora `GetHardwareId`,
  `PreDisconnect` y `SetErrorInfo` de los puestos.
  La documentación de IddCx 1.4 para IDD remotos no dice qué parámetro sobra o falta (`Flags`
  ya lleva REMOTE_SESSION_DRIVER y USE_SMALLEST_MODE). Para verlo: `test	race-iddcx.ps1`
  captura el ETW de IddCx, UMDF y RdpIdd durante un intento.
- **21:38, causa del 0xC000000D:** los eventos WPP de IddCx no se pueden decodificar (no hay TMF
  público), pero la traza `.etl` de `test	race-iddcx.ps1` lleva el texto en claro:
  `Invalid IDDCX_TRANSMISSION_TYPE, value 9` (`ddivalidation.cpp`). `NETWORK_OTHER` (9) viene
  en `iddcx.h` 1.4, pero el IddCx de 26100 no lo acepta. Cambiado a `IDDCX_TRANSMISSION_TYPE_OTHER`.
  Compilado; falta reinstalar el driver. Truco útil: buscar texto en el `.etl` cuando IddCx
  devuelve un error sin explicación.
- **21:40, con OTHER: el adaptador remoto se crea.** Salen `AdapterInitFinished 0x00000000`,
  `modo 1920x1080@60` y el swapchain asignado, y una sesión aguantó 46 s con RemoteIdd.
- **21:41, nuevo corte con `SetErrorInfo(0x12)`** (`ERRINFO_CLOSE_STACK_ON_DRIVER_IFACE_FAILURE`):
  en `IWRdsWddmIddProps1::OnDriverLoad`, rdpcorets pasa una **ruta de interfaz**
  (`\\?\SWD#RemoteDisplayEnum#...#{1ca05181-a699-450a-9a0c-de4fbe3ddd89}`), no un id de
  instancia. El proveedor no encontraba el dispositivo y devolvía error, y eso cierra la sesión.
  Arreglado: se saca la instancia con `DEVPKEY_Device_InstanceId`, y un fallo al pasar la sesión
  se registra pero ya no corta la conexión. `build.cmd` compila con `/utf-8` (los logs salían
  con mojibake).
- **21:45, primer puesto en pie con RemoteIdd** ("Sala de juegos", usuario `osms-…`, sesión 8,
  1920x1080@60), pero Moonlight no recibe imagen. Terminal Services **no llamó a
  `OnDriverLoad`** (a las 21:41 sí lo hizo; no siempre ocurre), así que el driver no sabía su
  sesión y no publicaba. Arreglado: el driver saca la sesión de su id de instancia
  (`…&SessionId_0008`, decimal, como `RdpIdd_IndirectDisplay&SessionId_0025`) y los Hz de
  `OsmsRefreshHz`. Si llega el IOCTL del proveedor, sus datos mandan.
- **21:49, driver completo en el puesto:** sesión 8 por id de instancia y por IOCTL,
  1920x1080@120. Pero Sunshine (`capture = remoteidd`) falla al arrancar con
  `RemoteIdd: no frame slot for session 8`, prueba todos los encoders y acaba en `Fatal`. Causa:
  el slot y los eventos se creaban en el primer frame publicado. Tras el cambio de modo el
  swapchain se recrea, y con el escritorio quieto no llega ningún frame. Arreglado: el driver
  los crea en cuanto conoce la sesión y los mantiene abiertos toda su vida (`KeepSharedObjects`,
  `FramePublisher::Prepare`). Nuevas trazas: `swapchain asignado` y `primer frame publicado`.
  `test\collect-sunshine.ps1` copia el log de Sunshine de cada puesto a `diag\` (sin credenciales).
- **21:55-21:57, el driver publica frames** (`primer frame publicado (sesión 9)`), pero Sunshine
  sigue con `no frame slot`. Los objetos `Global\OsmsRidd9*` existen (desde otra cuenta,
  `OpenFileMapping` da error 5, no 2). Causa: **OpenStreamMS lanzaba Sunshine con el usuario del
  puesto** (`LaunchInSession` con `asSystem = false`), no como SYSTEM como suponía el diseño. El
  DACL lo deja fuera y tampoco podría duplicar la textura del WUDFHost (LocalService).
  Decisión (usuario): **SYSTEM solo en los puestos con `capture = remoteidd`** (`SunshineManager`
  con `runAsSystem`), como hace sunshinesvc en la consola. Para que las apps no se abran en la
  consola, sunshine-webrtc (`misc.cpp`, `retrieve_users_token`) usa ahora la sesión del propio
  proceso cuando no es la 0. `Sunshine\sunshine.exe` está recompilado sin símbolos (`strip`).
- **22:07-22:15, sesión colgada.** Tras `deploy-full.ps1`, OpenStreamMS **reutilizó la sesión 8**
  ("Sesion existente", sin FreeRDP nuevo). La sesión figuraba como activa, pero sin ningún
  dispositivo de pantalla presente. Todo lo lanzado en ella se colgaba (`ChangeDisplaySettings`,
  `sunshine.exe --creds`, así que las credenciales rotadas no se aplicaron), y ni `logoff 8` ni
  `rwinsta 8` la cerraban: hubo que reiniciar. Los WUDFHost de RemoteIdd de las sesiones 8 y 9
  seguían vivos, también el de la 9, ya cerrada. Sospecha (no confirmada, sin depurador):
  interbloqueo al retirar el dispositivo. El hilo del swapchain hacía `WdfObjectDelete(swapchain)`
  también cuando lo paraba IddCx (`EvtMonitorCleanup`/Unassign). Ahora solo lo borra si lo pierde
  él (`Exit::SwapChainLost`); si lo para IddCx, registra `hilo del swapchain parado por IddCx`.
  Pendiente en OpenStreamMS: con `remoteidd`, no reutilizar sesiones de puesto ya existentes y
  matar el Sunshine SYSTEM antes de cerrar la sesión (OpenStreamMS lo relanza si muere).
- **22:18, tras reiniciar:** al arrancar se ejecuta el `autoupdate.bat` de asmtron
  (`RDPWInst -i`, ver `C:\Program Files\RDP Wrapper\autoupdate.log`). Si `ServiceDll` no es
  `rdpwrap.dll`, lo reinstala. Esta vez TermService ya corría con TermWrap y el reinicio falló
  (1056), pero en el siguiente reinicio de TermService RDP-Tcp habría dejado de escuchar.
  `install.ps1` desactiva ahora las tareas programadas que ejecutan `autoupdate.bat`/`RDPWInst`
  (las guarda en el estado) y `uninstall.ps1` las reactiva.
- **22:30, reinstalación tras el reinicio:** `install.ps1` puso TermWrap y desactivó la tarea
  `\RDP Wrapper Autoupdate`. OpenStreamMS arrancó "Sala de juegos" en la sesión 3. En el log
  del driver: sesión 3 (id de instancia), objetos compartidos listos, 1920x1080@120, swapchain
  asignado y `primer frame publicado`. Sunshine (SYSTEM) confirmó el encoder `amdvce`.
- **D2 OK:** Moonlight en la tele con "Sala de juegos": 0 frames perdidos y 0 saltos, 60 Hz
  estables (comprobado por el usuario en las estadísticas de Moonlight).

### 2026-10-27 — OpenStreamMS: TermWrap con asmtron de respaldo

- La píldora Multi-RDP decía "No instalado" con TermWrap, porque solo reconocía `rdpwrap.dll`.
  `RdpWrapperManager.GetStatus()` ahora devuelve:
  - `Wrapper`: `asmtron` o `TermWrap`, según `ServiceDll`.
  - `Loaded`: si la DLL está en los módulos del svchost de TermService. El PID sale de
    `QueryServiceStatusEx`.
  - `RemoteIdd`: si el proveedor está en RDP-Tcp.

  "Activo" exige que el archivo exista y esté cargado. Si `ServiceDll` apunta a un wrapper que
  no está cargado, sale "Roto".
- Respaldo: asmtron se mantiene instalado en la misma carpeta. Al arrancar OpenStreamMS, antes
  de los puestos, `EnsureMultiRdp` comprueba si RemoteIdd está en RDP-Tcp. Si lo está, espera
  hasta 60 s a que TermWrap esté cargado y RDP-Tcp escuche. Si no ocurre:
  - devuelve RDP-Tcp a `OsmsInnerProtocol_Object` (rdpcorets);
  - reactiva la tarea de autoupdate;
  - pone `ServiceDll` en `rdpwrap.dll`, si están `rdpwrap.dll` y `rdpwrap.ini`;
  - reinicia TermService.

  Los puestos con `capture = remoteidd` usan entonces la captura por defecto y
  `UsesRemoteIdd` exige el proveedor activo. Se vuelve a RemoteIdd con `install.ps1`.
- Los botones Instalar, Actualizar y Desinstalar de asmtron siguen disponibles. Antes de
  actuar sacan RemoteIdd de RDP-Tcp, porque con asmtron RDP-Tcp no escucharía con el
  proveedor, y la confirmación de Instalar lo avisa.
- **Pendiente:** probar el respaldo de verdad, por ejemplo poniendo a mano `ServiceDll` en
  `rdpwrap.dll` y reiniciando el PC. También quedan los dos pendientes de las 22:07 (no
  reutilizar sesiones con `remoteidd` y matar el Sunshine SYSTEM antes del logoff).
