# RemoteIdd — pantalla propia para los puestos (experimental, pendiente de prueba real)

> **Guía completa** (arquitectura, decisiones, compilar, probar en un puesto real,
> deshacer, riesgos): [`GUIA.md`](GUIA.md).

Esqueleto de un **driver IddCx de sesión remota** para OpenStreamMS, como alternativa
*limpia* (API documentada de Microsoft, sin enganchar el `RdpIdd.dll` de Windows) a la
técnica de DuoStream.

## ⚠️ Estado: inerte a propósito

Este driver **no arranca solo**. Microsoft documenta que un IddCx remoto
> *"needs to be part of a remote driver package which includes a remote protocol stack
> driver, which is beyond the scope of this documentation."*

Y `IddCxAdapterInitAsync` **falla** si el adaptador no lo creó la pila de escritorio
remoto que atiende la sesión. Con el RDP estándar de Windows, quien atiende es el
`RdpIdd` de Microsoft, así que este driver **nunca llega a inicializarse**: en la primera
carga devolverá error en `IddCxAdapterInitAsync` (ver `EvtDeviceAdd` en `Driver.cpp`).

Para que llegue a cargarse hace falta la pieza grande que **todavía no existe** en este
repo: un **proveedor de protocolo RDS** propio (equivalente a `DuoRds.dll`, interfaces
`IWRdsProtocol*`) que sustituya a RDP-Tcp y pida a Windows cargar *este* IddCx para la
sesión. Eso es trabajo de escala Citrix/Horizon y está fuera de este spike.

Qué aporta igualmente este spike:
- Base reutilizable y correcta del lado IddCx (flags remotos, modos, swapchain) para
  cuando/si se aborde el proveedor de protocolo.
- Deja aislado y documentado el contrato de entrega de frames a Sunshine
  (`SharedFrame.h`), que es independiente del protocolo.

## Qué incluye
| Archivo | Qué es |
|---|---|
| `Driver.h` / `Driver.cpp` | Driver UMDF2 + IddCx: adaptador remoto, monitor, swapchain. |
| `SharedFrame.h` | Contrato de memoria compartida + eventos hacia Sunshine. |
| `FramePublisher.*` | Productor: copia cada frame a textura compartida y lo publica (lo usa el driver). |
| `FrameConsumer.*` | Consumidor: lo copia Sunshine en `src/platform/windows/remoteidd/` (`capture = remoteidd`). |
| `RemoteIdd.inf` | INF del driver (flags remotos, `ProcessSharingDisabled`). |
| `RemoteIddIoctl.h` | Hardware id e IOCTL de sesión entre proveedor y driver. |
| `Provider/` | `OsmsRdsProvider.dll` (D1) + `install.ps1` / `uninstall.ps1` de pruebas. |
| `test/verify-seat.ps1` | Verificación con un puesto real (`RemoteIddPerf watch`). |
| `build.cmd` / `build.md` | Compila test + driver + INF sin instalar el WDK (NuGet). |
| `test/SharedFrameTest.cpp` | Test del contrato de `SharedFrame.h` (seqlock, limitador por llegada). |
| `test/PerfTest.cpp` | `RemoteIddPerf.exe`: rendimiento entre dos procesos en la GPU real. |

## Diferencias clave frente a un IddCx de consola (p. ej. SudoVDA)
1. `IDDCX_ADAPTER_FLAGS_REMOTE_SESSION_DRIVER` **+** `IDDCX_ADAPTER_FLAGS_USE_SMALLEST_MODE`
   en `IDDCX_ADAPTER_CAPS.Flags` (ambos obligatorios; si falta el segundo, InitAsync falla).
2. `UmdfHostProcessSharing = ProcessSharingDisabled` en el INF (un host por sesión, lo
   recomienda Microsoft para IDD remotos; es lo que hace también `DuoIdd`).
3. La resolución la fija el propio driver con `IddCxAdapterDisplayConfigUpdate` (la del
   cliente remoto), no un EDID; el monitor se crea sin EDID y los modos salen de
   `EvtMonitorGetDefaultModes`.

## Mínima invasión (criterio del proyecto)
Lo que se hace y lo que no, para que el puesto no cambie nada del Windows anfitrión:
- **Nada de hooks ni inyección** en `RdpIdd.dll`, WUDFHost ni ningún proceso de Windows.
- **Driver aislado**: dispositivo raíz propio (`Root\OsmsRemoteIdd`) que se crea solo
  cuando se usa, binario solo en el Driver Store, sin registro ni ficheros propios, y
  desinstalable del todo con `pnputil` (ver `build.md`).
- **Recursos efímeros**: objetos con nombre y textura compartida viven lo que vive el
  WUDFHost; DACL limitada a SYSTEM y LocalService.
- **Base mínima de SO**: IddCx 1.4 / UMDF 2.33 (Windows 10 1903+).
- **Para D1**: el proveedor de protocolo debe registrarse como un *listener* aparte solo
  para los puestos, **sin sustituir RDP-Tcp**. La consola y el RDP normal siguen con el
  `RdpIdd` de Microsoft; si se quita OpenStreamMS, Windows queda como estaba.
- **Firma**: `testsigning` solo en VM/equipo de pruebas; en uso real, firma attestation.

## Estado de lo que falta
- [x] Copia real del frame a textura compartida (handle NT + keyed mutex) en `PublishFrame`.
- [x] Modos reales (lista fija sin EDID) y resolución inicial por `IddCxAdapterDisplayConfigUpdate`.
- [x] Contrato de `SharedFrame.h` con seqlock, probado por `test/`.
- [x] Proveedor de protocolo RDS (`Provider/`, envoltorio de rdpcorets por API documentada).
- [x] Sesión real del puesto: la pasa el proveedor por IOCTL al cargar el driver.
- [ ] Verificación con un puesto real (D2): scripts listos, falta ejecutarlos en VM.
- [x] Resolución del cliente: el proveedor la toma de `GetClientData` (HRes/VRes) y los Hz de
  `OsmsRefreshHz` del listener (`install.ps1 -RefreshHz`); el driver la aplica en caliente
  (`IddCxMonitorUpdateModes` + `IddCxAdapterDisplayConfigUpdate`).
- [x] Lado consumidor en Sunshine: backend `capture = remoteidd` (repo sunshine-webrtc).

Referencias: [IddCx 1.4 remote IDDs](https://learn.microsoft.com/en-us/windows-hardware/drivers/display/iddcx1.4-updates-for-remote-idds)
· [IDDCX_ADAPTER_FLAGS](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/iddcx/ne-iddcx-iddcx_adapter_flags)
· [IndirectDisplay sample](https://github.com/microsoft/Windows-driver-samples/tree/main/video/IndirectDisplay)

## Rendimiento medido (2026-10-26, RX 9060 XT)
`RemoteIddPerf.exe`, productor y consumidor en procesos distintos, mismo `FramePublisher`
que el driver. "Píxel en CPU" = desde el present hasta que el consumidor tiene el contenido
copiado y leído (incluye la GPU).

| Escenario | Publish() p99 | Entrega | Present → píxel p99 |
|---|---|---|---|
| 1080p60 | 0,25 ms | 100 % | 0,71 ms |
| 4K60 | 0,21 ms | 99,6 % | 0,82 ms |
| 4K120 | 0,26 ms | 99,6 % | 0,63 ms |
| 4K240 | 0,27 ms | 99,5 % | 0,56 ms |
| 4K120, encoder de 25 ms | 0,20 ms | pierde frames, el productor sigue a 120 | 9,6 ms (≤ 1 intervalo) |
| 4K120, consumidor retiene 12 ms | 0,20 ms | pierde frames, el productor sigue a 120 | 12,7 ms |

En Sunshine (`test_sunshine --gtest_filter=RemoteIdd*`): el backend real captura 481/480
frames a 2560x1440 y 240 Hz, sin reinicios.
