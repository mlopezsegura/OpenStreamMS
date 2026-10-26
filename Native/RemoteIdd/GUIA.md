# RemoteIdd — guía completa

Todo lo necesario para retomar RemoteIdd sin contexto previo: qué es, por qué está hecho
así, cómo se compila, cómo se prueba en un PC real, cómo se deshace y qué falta.
Detalle de tareas en `TASKS.md`; resumen y cifras en `README.md`.

Estado (2026-10-26): **implementado y probado sin instalar** (tests unitarios, de
rendimiento entre procesos y de Sunshine en verde). **Falta la prueba con un puesto real**
(sección 5), que exige administrador y firma de test.

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
`OsmsRdsProvider.dll`, que solo atiende al listener de puestos `OSMS-Seat` (puerto 3390).
RDP-Tcp (3389) y la consola no cambian.

## 2. Decisiones de diseño (no reabrir sin motivo)

- **Nada de hooks ni inyección.** DuoStream inyecta `DuoIdd.dll` en el `RdpIdd` de
  Microsoft y engancha funciones internas por offset: frágil ante cada actualización de
  Windows. Aquí solo se usan APIs documentadas: IddCx, `wtsprotocol.h`, D3D11.
- **El proveedor no implementa RDP**: envuelve al protocolo de Microsoft (`rdpcorets.dll`,
  CLSID `{5828227c-20cf-4408-b73f-73ab70b8849f}`, el mismo de RDP-Tcp) y le delega todas
  las llamadas. Solo responde por su cuenta a `IWRdsWddmIddProps`/`IWRdsWddmIddProps1`:
  `GetHardwareId` → `OsmsRemoteIdd_IndirectDisplay` (así Windows carga RemoteIdd y no
  RdpIdd) y `OnDriverLoad` → pasa la sesión al driver por IOCTL.
- **Mínima invasión**: listener aparte (no se toca RDP-Tcp), sin dispositivo raíz (el
  dispositivo lo crea Terminal Services por sesión), driver solo en el Driver Store, objetos
  efímeros con DACL mínima (SYSTEM, LocalService, administradores), desinstalación completa.
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
| OpenStreamMS | `RdpSessionCreator.cs`, `StreamSessionService.cs`, `wwwroot` | Captura `remoteidd` → sesión contra `127.0.0.2:<puerto OSMS-Seat>`. |

Detalles que costó encontrar:
- Todo IDD necesita `UpperFilters = IndirectKmd` en el INF (lo pone también RdpIdd).
- El hardware id de un IDD remoto es un nombre plano, como `RdpIdd_IndirectDisplay`.
- `IddCxCheckOsFeatureSupport` no existe hasta IddCx 1.11 (no está en el WDK 26100).
- `DriverCLSID` es de UMDF1; en UMDF2 sobra. Falta obligatoria: `UmdfExtensions = IddCx0102`.
- La sesión no se puede deducir en el driver (el WUDFHost está en la sesión 0): la manda el
  proveedor. Sin sesión el driver no publica (dos puestos se pisarían).
- RDP no transmite los Hz del cliente: salen de `OsmsRefreshHz` (DWORD) en
  `WinStations\OSMS-Seat` (`install.ps1 -RefreshHz`). La resolución sí: `HRes`/`VRes` de
  `GetClientData`, que el proveedor lee al delegar (la estructura lleva la contraseña: no
  se copia ni se registra nada más).
- `wcscpy_s` con búfer corto **aborta el proceso**; en el proveedor eso tumbaría el svchost
  de Terminal Services. Comprobar longitudes antes.
- MinGW (Sunshine) no tiene `ReadAcquire64`: el seqlock usa lecturas volatile + barreras.
- `_wgetenv` no ve `SetEnvironmentVariableW`: usar `GetEnvironmentVariableW`.
- Un handle NT nuevo puede reutilizar el valor del anterior: el consumidor reabre por
  `TextureGeneration`, no por valor de handle.
- Crear objetos en `Global\` exige SeCreateGlobalPrivilege: los tests usan `Local\`.

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

**2. Instalar** (certificado de test, driver, proveedor y listener `OSMS-Seat`)
```powershell
cd E:\Workspace\OpenStreamMS\Native\RemoteIdd
.\Provider\install.ps1 -Bin .\bin -RefreshHz 120 -RestartTermService
```

**3. Abrir una sesión de puesto** con un usuario de puesto (no con el propio: Windows no
deja conectar por RDP al propio usuario que está en la consola). Dejar algo moviéndose:
IddCx solo presenta cuando cambia la pantalla.
```powershell
mstsc /v:127.0.0.2:3390
```

**4. Verificar** (desde el escritorio propio, no desde el puesto)
```powershell
.\test\verify-seat.ps1 -Bin .\bin
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
`install.ps1` en `%ProgramData%\OpenStreamMS\remoteidd-install.json`: listener, clase COM y
DLL del proveedor, driver del Driver Store y certificado de test.

### Alternativa en VM (Hyper-V)
Válida para comprobar que funciona; las latencias no son representativas (sin GPU real).
VM de generación 2 con TPM virtual y **Secure Boot desactivado** (`Set-VMFirmware
-EnableSecureBoot Off`), `testsigning` dentro de la VM, Escritorio remoto habilitado y
regla de firewall para el 3390. El cliente es el PC (`mstsc /v:<ip VM>:3390` con un usuario
de la VM) y la verificación se lanza por PowerShell Direct (`Invoke-Command -VMName`).
Pendiente: que `install.ps1` firme en el PC y en la VM solo instale (la VM no tiene el WDK).

## 6. Seguridad y sistema

- `testsigning` es un cambio global del arranque: solo para pruebas. En producción, firma
  *attestation* en el Partner Center de Microsoft (certificado EV); no toca el arranque.
- El proveedor corre dentro del svchost de Terminal Services: un fallo en él afecta a las
  sesiones de ese listener. Por eso solo se registra en `OSMS-Seat`.
- Los objetos con nombre solo admiten SYSTEM, LocalService y administradores. El
  dispositivo del driver: los mismos (`Security` en el INF; RdpIdd abre a todos).
- `RemoteIddPerf watch` necesita SeDebugPrivilege para duplicar el handle del WUDFHost;
  Sunshine corre como SYSTEM y no lo necesita.

## 7. Riesgos abiertos y pendientes

- **Riesgo principal**: que `rdpcorets` no acepte una sesión que use un IDD distinto del
  suyo (DuoStream engancha RdpIdd en vez de sustituirlo, lo que sugiere que no es trivial).
  Lo resuelve la prueba de la sección 5. Si falla, no recurrir a hooks: plantearlo antes.
- Prueba con un puesto real (D2): pendiente.
- `install.ps1` para VM (firmar en el PC, instalar en la VM).
- HDR (IddCx 1.10+) y cursor por hardware: no implementados; el cursor va compuesto en el
  frame y el backend de Sunshine rechaza HDR.
- Sin commits todavía en OpenStreamMS ni en sunshine-webrtc (ramas propias por crear).
