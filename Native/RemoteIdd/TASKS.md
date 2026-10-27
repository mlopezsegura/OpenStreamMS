# RemoteIdd — tareas para la pasada de corrección (Opus 5.5)

Contexto en `README.md` y `build.md`. El driver es un **spike no funcional**: `IddCxAdapterInitAsync`
falla hasta que exista un proveedor de protocolo RDS. Estas tareas **no** intentan desbloquear eso
(es un hito aparte); buscan dejar el lado IddCx correcto, sin fugas y compilable, y validar lo que
sí es testeable hoy.

Orden sugerido: primero lo que impide compilar/ejecutar limpio, luego correcciones, luego trabajo real.

## A. Que compile y no filtre (prioridad alta)

- [x] **A1. Fuga del `SwapChainProcessor`.** En `EvtMonitorAssignSwapChain` se crea con `new` y no se
  guarda. Guardarlo en `MonitorContext` (p. ej. `std::unique_ptr<SwapChainProcessor>`), destruirlo en
  `EvtMonitorUnassignSwapChain` y en el apagado del monitor. *Criterio:* `Unassign` detiene el hilo y
  libera los objetos de `SharedFrame.h`; sin handles ni hilos huérfanos.
  *Hecho:* `MonitorContext::Processor`, liberado en `Unassign` y en `EvtMonitorCleanup`.
- [x] **A2. `IddCxCheckOsFeatureSupport`.** Obligatorio en IddCx 1.11+ antes de cualquier
  `IddCxAdapterInitAsync`. Añadirlo en `EvtDeviceAdd` y actuar según el resultado.
  *No aplica:* el WDK 26100 llega hasta IddCx 1.10 y esa función no existe. Se compila
  contra 1.4; revisar solo si algún día se sube a 1.11+.
- [x] **A3. Verificar firmas de callbacks contra el `iddcx.h` del WDK instalado.** Los nombres de
  struct/arg (`IDARG_*`, `IDDCX_ADAPTER_CAPS`, versiones) cambian entre versiones de IddCx; ajustar a
  la que se compile. *Criterio:* compila sin warnings de tipos en un proyecto UMDF2 real.
  *Hecho:* `build.cmd` compila con `/W4 /WX` contra IddCx 1.4 + UMDF 2.33 (WDK NuGet).
  Se corrigieron `IDARG_IN_SETSWAPCHAIN`, `IDDCX_MONITOR_DESCRIPTION`, `IddCxMonitorArrival`,
  y el adaptador pasa a crearse en `EvtDeviceD0Entry` como en el sample.
- [x] **A4. CLSID/GUID placeholder** en `RemoteIdd.inf` (`DriverCLSID`) regenerados con `uuidgen`.
  *Hecho:* `DriverCLSID` es de UMDF1 y se quitó. Faltaba `UmdfExtensions = IddCx0102`.
  El INF pasa `infverif` (mínimo 10.0.18362, el de IddCx 1.4).

## B. Correcciones de lógica

- [x] **B1. `PublishFrame` no copia el frame.** Hoy solo publica metadatos. Implementar:
  obtener la `IDXGIResource` del frame (`buf.MetaData.pSurface`), crear/reutilizar una textura
  compartida con handle NT, copiar (`CopySubresourceRegion`) y publicar `SharedTextureHandle` +
  `Width/Height/DxgiFormat` en el slot. Decidir si el driver duplica el handle hacia el pid de
  Sunshine o publica el del productor + `ProducerPid` (ver nota en `SharedFrame.h`).
  *Hecho:* textura con handle NT + keyed mutex (clave 0, `AcquireSync` con timeout 0:
  si Sunshine la tiene, el frame se pierde). Se publica el handle del productor +
  `ProducerPid`; el driver no abre el proceso de Sunshine.
- [x] **B2. Sesión real.** `CurrentConsoleOrRemoteSession()` usa la sesión del proceso; en un IDD
  remoto debe venir de la pila remota que carga el driver. Sustituir cuando exista el proveedor;
  de momento dejar claro el supuesto.
  *Supuesto documentado* en `SessionIdPlaceholder()`: el WUDFHost está en sesión 0.
- [x] **B3. EDID y modos.** `EvtParseMonitorDescription`, `EvtMonitorGetDefaultModes` y
  `EvtMonitorQueryTargetModes` devuelven vacío/placeholder. Construir el EDID y la lista de modos
  reales (al menos el modo base que exige `USE_SMALLEST_MODE`).
  *Hecho:* monitor sin EDID + lista fija (800x600 a 3840x2160, 60-240 Hz) y resolución
  inicial 1920x1080@60 por `IddCxAdapterDisplayConfigUpdate` (la real la dará D1).
- [x] **B4. Manejo de `DXGI_ERROR_ACCESS_LOST`** en `RunCore`: hoy devuelve `false` igual que un
  fallo definitivo. Distinguir "reintentar reinit" de "terminar".
  *Hecho:* `Exit::Terminate` / `Exit::SwapChainLost`. Reintentar sobre el swapchain viejo
  no sirve: se borra y IddCx reasigna uno nuevo con dispositivo nuevo (como el sample).
- [x] **B5. Sincronización del slot.** Si `Width/Height/Handle` y `FrameId` se leen en el consumidor
  mientras el productor escribe, puede haber lectura rasgada. Añadir un seqlock sencillo (contador par/impar)
  o doble buffer de slot. *Validado por el test C, caso de carrera.*
  *Hecho:* seqlock en `SharedFrame.h` (`RemoteIddSlotPublish` / `RemoteIddSlotRead`), v2.

## C. Test mínimo (ejecutable HOY, sin WDK) — ver `test/`

- [x] **C1.** Compilar y pasar `test/SharedFrameTest.cpp` (solo MSVC, sin WDK):
  `cl /EHsc /std:c++17 test\SharedFrameTest.cpp` y ejecutar `SharedFrameTest.exe`.
  Valida el contrato de `SharedFrame.h`: creación por nombre, round-trip de eventos, `FrameId`
  monótono, y el **limitador por llegada** (el productor no se bloquea con un consumidor lento).
  *Hecho:* el test usaba `Global\` (exige SeCreateGlobalPrivilege y reventaba) y
  `sleep_for` (redondea a 15,6 ms). Ahora `Local\` y temporizador de alta resolución.
- [x] **C2.** Tras B5, extender el test con el caso de lectura concurrente y comprobar que no hay
  slot rasgado (sin seqlock debe fallar; con seqlock debe pasar).
  *Hecho:* en cada ejecución, cientos de miles de lecturas rasgadas sin seqlock y 0 con él.

## E. Sistema nativo y rendimiento (hecho 2026-10-26)

- [x] **E1.** `FramePublisher` (driver) y `FrameConsumer` (Sunshine) fuera de IddCx: solo
  D3D11, handle NT y keyed mutex. `TextureGeneration` en el contrato (un handle nuevo puede
  reutilizar el valor del viejo).
- [x] **E2.** `RemoteIddPerf.exe`: 6 escenarios entre procesos en la GPU real, todos OK
  (cifras en `README.md`). Lo ejecuta `build.cmd`.
- [x] **E3.** Backend `capture = remoteidd` en sunshine-webrtc + 4 tests gtest (MinGW), OK.

## D. Proveedor de protocolo RDS

- [x] **D1. Código.** `Provider/RdsProvider.cpp` → `OsmsRdsProvider.dll`. Vía documentada:
  envuelve el protocolo de Microsoft (rdpcorets, CLSID de RDP-Tcp) y solo responde por su
  cuenta a `IWRdsWddmIddProps`/`IWRdsWddmIddProps1` (wtsprotocol.h): `GetHardwareId` →
  `OsmsRemoteIdd_IndirectDisplay`, `OnDriverLoad` → sesión al driver. Sin hooks.
  `test/ProviderTest.cpp`: 21 comprobaciones con objetos falsos, OK.
- [x] **B2.** Resuelto con D1: `IOCTL_REMOTEIDD_SET_SESSION` (`RemoteIddIoctl.h`). El driver
  no publica hasta conocer la sesión. Interfaz de dispositivo para la variante por instancia.
- [x] **INF**: casa con el hardware id de Terminal Services (sin dispositivo raíz) y lleva
  `UpperFilters = IndirectKmd`, que exige todo IDD y faltaba.
- [x] **D1b. Modo del cliente.** IOCTL v2 con resolución (de `GetClientData`) y Hz
  (`OsmsRefreshHz`); el driver añade el modo si falta y lo aplica aunque llegue tarde.
- [ ] **D2. Verificación con puesto real.** `Provider/install.ps1` (testsigning, cert de test,
  driver y proveedor en RDP-Tcp con el valor original guardado; solo los puestos de
  127.0.0.2 reciben RemoteIdd; el listener aparte `OSMS-Seat` se descartó: Windows Pro no lo
  levanta),
  `test/verify-seat.ps1`, `Provider/uninstall.ps1`. Pendiente de ejecutar en VM/equipo de
  pruebas con permiso del usuario: exige administrador, testsigning y reiniciar TermService.
  Riesgo abierto: que rdpcorets no acepte una sesión sin su RdpIdd (DuoStream engancha
  RdpIdd en vez de sustituirlo); es lo primero que dirá la prueba.

---
**No hacer:** inyectar en `RdpIdd.dll` ni enganchar funciones internas por offset (mecanismo real de
DuoStream). Es la dependencia frágil que el proyecto evita; si parece el único camino, pararse y
plantearlo, no implementarlo.
