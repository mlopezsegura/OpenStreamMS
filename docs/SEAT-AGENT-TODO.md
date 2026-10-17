# Agente de sesión y presentador — pieza pendiente de programar

El resto del sistema de puestos (modelo, API, inventario, gestión de monitores,
reconexión RDP, pausa de Sunshine, dashboard) **está hecho**. Falta la pieza que
hace que el puesto se vea y se use: el **agente de sesión** (dentro de la sesión
RDP del puesto) y el **presentador** (en la consola). Se definen aquí a nivel
conceptual, con las clases y los métodos (entry points) y lo que hace cada uno,
para implementarlos.

Está validado que todo esto funciona (ver `E:\Workspace\seat-tests` y la memoria
`openstream-multiseat-findings`): vídeo render→pantalla ~2 ms, audio ~70 ms,
entrada ~0,1 ms. Lo que queda es portarlo a OpenStreamMS.

Dónde se enchufa: en `SeatManager.DoStart` y `SeatManager.Stop` están los `TODO`
que marcan el punto exacto. El canal de comunicación (`SeatChannel`, `SeatPipes`,
`SeatMsg`, `SeatInputEvent`) y los permisos ya están en `Services/Seats/SeatIpc.cs`.

---

## 1. `SeatAgent` — corre dentro de la sesión del puesto

Nuevo proceso: `OpenStreamMS.exe --seat-agent <nombreTubería>`. Se lanza con
`ProcessInSession.LaunchInSession(session.RdpSessionId, exe, "--seat-agent " + pipe)`,
igual que hoy se lanza Sunshine. Archivo sugerido: `Services/Seats/SeatAgent.cs`.

```
static int Run(string pipeName)
```
Punto de entrada. Se conecta a la tubería del servicio (`SeatPipes.Connect`),
lee el mensaje de configuración inicial (JSON con `audio`, tamaño del anillo…),
arranca los hilos de captura de vídeo, cursor y audio, y entra en el bucle que
recibe eventos de entrada y los inyecta. Termina cuando el servicio manda `stop`
o se cierra la tubería.

```
static void CaptureLoop()
```
Captura el escritorio de la sesión por **Desktop Duplication (DDA)**:
`IDXGIOutputDuplication.AcquireNextFrame`. Cada fotograma se copia a una de las
texturas de un **anillo de 3 texturas D3D11 compartidas** (creadas con
`SharedNTHandle | SharedKeyedMutex`; con una sola se perdían fotogramas). Tras
copiar, publica por el canal un mensaje `SeatMsg.Frame` con `[int índice del
anillo][long QPC]`. Los handles NT de esas 3 texturas se envían una sola vez al
arrancar (mensaje JSON) para que el servicio los duplique al presentador.

```
static void CursorLoop()      // o integrado en CaptureLoop
```
DDA entrega el puntero por separado (`AcquireNextFrame` da `PointerShapeInfo` y
posición). Envía la forma del cursor (`SeatMsg.CursorShape`) cuando cambia y la
posición/visibilidad (`SeatMsg.CursorPos`) cada fotograma, para que el
presentador lo dibuje.

```
static IDisposable StartAudio()
```
Captura en **loopback** el dispositivo "Audio remoto" de la sesión
(`WasapiLoopbackCapture` con búfer de 10 ms; ver nota de latencia abajo). Cada
bloque PCM va por el canal como `SeatMsg.Audio`. El primer mensaje anuncia el
formato (frecuencia, canales, bits).

```
sealed class Injector { void Inject(SeatInputEvent ev); }
```
Inyecta en la sesión la entrada que llega del presentador. **Clave** (validado):
el hilo debe hacer `SetThreadDesktop(OpenInputDesktop())` **una vez** antes del
primer `SendInput`, o Windows rechaza las entradas. Traduce `SeatInputEvent`:
- ratón → `INPUT` tipo `MOUSE` con `dx/dy` relativos, botones y rueda;
- teclado → `INPUT` tipo `KEYBOARD` con `KEYEVENTF_SCANCODE` (usar el scancode,
  así la distribución del teclado sale bien).

Nota de latencia/calidad: el búfer de 10 ms sonó limpio; con los búferes por
defecto de NAudio la latencia subía a ~165 ms. Mejora posterior (fase 5): usar
la captura que ya hace el fork de Sunshine en vez de un loopback aparte.

---

## 2. `SeatHost` — corre en la consola (dentro de la app de bandeja)

La consola es la única sesión que posee los monitores físicos y los periféricos.
Archivo sugerido: `Services/Seats/SeatHost.cs`. El servicio (sesión 0) no puede
presentar ni leer Raw Input, por eso vive en la app de bandeja (`TrayApp`).

```
void OpenSeat(Guid seatId, string pipeName, SeatMonitor monitor,
              string audioOutId, IReadOnlyList<SeatDevice> devices)
```
Abre un puesto: conecta al servicio por su tubería, recibe los handles de las 3
texturas compartidas, crea el presentador en el monitor del puesto, abre la
salida de audio y empieza a enrutar la entrada de los dispositivos del puesto.

```
void Present(SeatMonitor monitor)   // bucle de presentación
```
Ventana a **pantalla completa en flip model** sobre el monitor del puesto
(independent flip, ~0,4 ms a pantalla). En cada fotograma: toma del canal el
índice del anillo, hace `AcquireSync` del keyed mutex de esa textura, la copia al
back buffer del swapchain, dibuja el cursor encima y `Present(1)`. La ventana es
`WS_EX_NOACTIVATE | WS_EX_TRANSPARENT` para no robar foco ni recibir clics de la
consola. Colocar el monitor tocando al principal solo por una esquina evita que
el cursor de la consola cruce (ver `DisplayTopology`; falta el método de
colocación en esquina, trivial con `SetDisplayConfig` cambiando la posición).

```
void PlayAudio(string audioOutId)   // reproductor
```
`WasapiOut` sobre el endpoint del puesto con un `BufferedWaveProvider`; mete los
bloques `SeatMsg.Audio` que llegan por el canal. Búfer corto (~10 ms).

```
void RouteInput(IReadOnlyList<SeatDevice> devices)
```
Registra **Raw Input** de ratón y teclado (`RIDEV_INPUTSINK`). Para cada evento,
mira el dispositivo de origen (`RAWINPUTHEADER.hDevice` → ruta → puerto USB con
`DeviceTopology.UsbPortOf`) y, si el puerto está en `devices`, empaqueta un
`SeatInputEvent` y lo manda por el canal (`SeatMsg.Input`). Los eventos de otros
dispositivos se ignoran (en el diseño de consola-anfitriona nadie usa la consola).

---

## 3. Broker de handles — en el servicio (sesión 0)

El servicio duplica los handles NT de las texturas del agente (sesión del puesto)
al presentador (consola), aunque sean usuarios distintos. Validado que funciona.

```
IntPtr DuplicateHandleTo(int fromPid, IntPtr handle, int toPid)
```
`OpenProcess(PROCESS_DUP_HANDLE)` sobre ambos procesos + `DuplicateHandle`. El
servicio recibe del agente los 3 handles (por el canal), los duplica al PID del
`SeatHost` y le manda los handles duplicados. El `SeatHost` los abre con
`ID3D11Device1.OpenSharedResource1`.

---

## 4. Mandos por puesto (extensión de `GamepadSessionIsolation`) — HECHO

Un mando físico cuyo puerto USB está en `seat.Devices` de un puesto activo se
enjaula en **la sesión del puesto** en vez de en la consola. Implementado:

- `SeatManager.TryGetSeatWindowsSession(string usbPort, out uint windowsSession)`
  devuelve la sesión de Windows del puesto activo que reclama ese puerto.
- `GamepadSessionIsolation.TargetSessionFor(nodo)` resuelve el puerto USB del
  mando (`DeviceTopology.UsbPortOf`) y usa esa sesión, o la consola si nadie lo
  reclama. `HidePhysical` enjaula cada mando a su destino.
- `SeatManager.Generation` sube al encender/apagar un puesto; `ScanPhysical`
  lo vigila para reasignar los mandos al momento, sin esperar al rescan de 30 s.

---

## Orden de arranque (resumen)

1. `SeatManager.DoStart`: arranca la sesión, pausa Sunshine, activa el monitor y
   reconecta RDP a su resolución (la fijada o la que tenga el monitor). **(hecho)**
2. Crea la tubería (`SeatPipes.CreateServer`), lanza `SeatAgent` en la sesión,
   espera la conexión. **(TODO)**
3. Recibe del agente los handles de textura, los duplica al `SeatHost`. **(TODO)**
4. `SeatHost.OpenSeat`: presenta, reproduce audio y enruta entrada. **(TODO)**
5. `SeatManager.Stop`: manda `stop`, cierra presentador, saca el monitor del
   escritorio, reanuda Sunshine. **(parcial: lo no-agente está hecho)**
