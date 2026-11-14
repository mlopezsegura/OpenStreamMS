# Puestos locales (varios usuarios en un solo PC)

Un **puesto** es un conjunto de periféricos físicos (monitor, salida de audio,
teclados, ratones y mandos) al que se le asigna una **sesión** de OpenStreamMS.
La sesión se ve en el monitor del puesto y se controla con sus periféricos, como
si fuera un PC independiente. Varias personas pueden usar el mismo PC a la vez.

## Decisiones de diseño (validadas en pruebas, oct 2026)

| Tema | Decisión | Motivo |
|---|---|---|
| Monitor fuera del escritorio | OpenStreamMS lo activa/saca con CCD (`SetDisplayConfig`) | En Windows Pro los monitores "especializados" están bloqueados por licencia (`Display-Specialized-Displays-Enabled=0`) |
| Vídeo sesión → monitor | DDA en la sesión → texturas NT compartidas → presentador en la consola | ~2 ms render→pantalla, sin compresión, hasta 125 Hz |
| Resolución/Hz por puesto | Reconectar el cliente RDP (desconectar antes) | La pantalla RDP solo cambia al conectar; ~1,2 s |
| Frecuencia RDP | Global (`DWMFRAMEINTERVAL`) | No se puede fijar por sesión; FreeSync adapta cada monitor |
| Teclado/ratón | Mientras el puesto está encendido, su dispositivo USB pasa a WinUSB (`winusb.inf`, de Windows); el servicio lee los informes HID y los inyecta en la sesión. Si no se puede, Raw Input en la consola + escudo | Separación total sin drivers de terceros: la consola deja de ver el dispositivo. HidHide no permite leer HID de teclado/ratón, y la colección HID con `input.inf` queda detenida (hidclass no da acceso en crudo a ratón/teclado) |
| Mandos | HidHide: jail a la sesión del puesto | Ya lo usaba OpenStreamMS para la consola |
| Audio | Loopback de "Audio remoto" → salida del puesto | ~70 ms con búferes de 10 ms |
| Micrófono | Redirección de micrófono de RDP (canal `audin`): el cliente FreeRDP abre el micrófono del puesto y en la sesión aparece como «Remote Audio» | Sin drivers propios. El `wfreerdp` original ignora `dev:` y usa siempre el micrófono predeterminado: va parcheado (`FreeRDP/patches`) para elegirlo por id de endpoint |
| Consola | Solo anfitriona (usuario técnico, autologon) | Recomendado; con WinUSB los periféricos del puesto ya no llegan a la consola |

## Pantalla dividida

Hasta cuatro puestos comparten un monitor, que funciona como una cuadrícula de 2×2:
`SeatMonitor.Area` es `Full`, una mitad (`Left`/`Right`, `Top`/`Bottom`) o un cuarto
(`TopLeft`…`BottomRight`). `SeatAreas` traduce cada parte a sus celdas.

- **Validación**: dos puestos chocan si comparten alguna celda (`SeatMonitor.Overlaps`).
- **Resolución**: sin resolución fijada, la sesión toma el tamaño de su parte del modo
  actual del monitor (`SeatMonitor.Region`); una resolución fijada es la de la sesión.
- **Presentador**: la ventana de cada puesto ocupa solo su mitad. Con dos ventanas en el
  mismo monitor DWM compone en vez de hacer *flip* directo: cuenta con ~1 fotograma más.
- **Monitor**: se activa con el primer puesto y solo se saca del escritorio cuando se
  apaga el último que lo usa. En una parte sin puesto se ve el escritorio de la consola.
- **Driver RemoteIdd**: anuncia cada modo base también partido en mitades y cuartos
  (`AllModes`); un modo que no esté en la lista no llega a la sesión.
- **Panel**: la pestaña Puestos dibuja cada monitor con sus partes (mapa) y el hardware de
  cada puesto; teclados, ratones, mandos y salidas de audio se asignan arrastrándolos a un
  puesto apagado o a la bandeja «sin asignar».
- **Cambio de modo del monitor**: cada mitad se reajusta por su cuenta, como un puesto normal.
- **Monitor principal** (activo en 0,0): admite puestos solo en una mitad, nunca entero.
  Con un solo puesto, la consola se queda la otra mitad **con un escritorio de ese tamaño**
  (`SeatManager`, "mitad de la consola"):
  1. Se crea una pantalla virtual SudoVDA (`VirtualDisplay`) del tamaño de la mitad y a la
     frecuencia del monitor. El servicio la mantiene con pings; si se cae, el driver la quita sola.
  2. Se hace principal (`DisplayTopology.MakePrimary`, que desplaza todas las pantallas a la
     vez) y las ventanas de la consola pasan del monitor físico a ella (`ConsoleWindows`).
  3. Un agente como SYSTEM en la consola la captura (`--seat-agent --output \\.\DISPLAYn
     --no-audio`; SYSTEM para ver también el UAC) y el presentador la pinta en la mitad libre,
     igual que un puesto. El cursor de la consola se encierra en la pantalla virtual
     (`ConsoleShare.SetVirtual`, reaplicado en el bucle del presentador).
  4. Al apagar el puesto, el físico vuelve a ser principal y se quita la pantalla virtual;
     Windows devuelve sus ventanas al físico. Si después se enciende un puesto en la otra mitad,
     la consola queda detrás solo como anfitriona; al apagarlo, recupera su mitad.

  Sin SudoVDA queda el respaldo anterior: la mitad del puesto como barra de aplicación
  (`SHAppBarMessage`), que solo respetan las ventanas maximizadas (la barra de tareas e Inicio
  siguen ocupando el monitor entero), y el cursor encerrado en la mitad física.
  Limitaciones: la mitad de la consola también se ve a través de captura (~1 fotograma más); con
  el principal dividido, el ratón de la consola no llega a sus otros monitores; las ventanas de
  programas elevados no se pueden mover a la pantalla virtual.

## Componentes

```
Servicio (SYSTEM, sesión 0)
 ├─ SeatManager ............ puestos, inventario, encendido/apagado          [hecho]
 ├─ StreamSessionService ... ReconnectRdp, PauseSunshine/ResumeSunshine       [hecho]
 ├─ GamepadSessionIsolation  mandos → sesión del puesto (por puerto USB)      [hecho]
 ├─ HandleBroker ........... duplica texturas agente → presentador            [hecho]
 ├─ UsbCapture/UsbHidReader  ratón/teclado del puesto en WinUSB → sesión      [hecho]
 ├─ SeatIdentify ........... identificar: logo en el monitor, aviso de audio,
 │                           resaltado del dispositivo pulsado (--identify)  [hecho]
 └─ SeatHostControl ........ coordinación de apertura/cierre de puestos       [hecho]

Consola (app de bandeja)
 └─ SeatHost ............... presentador, audio, Raw Input                    [hecho]

Sesión del puesto
 └─ SeatAgent .............. captura, cursor, audio, inyección                [hecho]
```

## Archivos

| Archivo | Qué hace |
|---|---|
| `Services/Seats/Seat.cs` | Modelo (`Seat`, `SeatMonitor`, `SeatDevice`) y persistencia en `seats.json` |
| `Services/Seats/SeatManager.cs` | Orquestación del ciclo de encendido/apagado y puente de canales |
| `Services/Seats/SeatAgent.cs` | Proceso en sesión: captura DDA, cursor, audio loopback e inyección |
| `Services/Seats/SeatHost.cs` | Proceso consola: ventana flip model, WASAPI out, Raw Input sink |
| `Services/Seats/SeatHostControl.cs` | Tubería de control entre servicio (SYSTEM) y anfitrión de consola (TrayApp) |
| `Services/Seats/HandleBroker.cs` | Duplica handles NT de texturas compartidas entre procesos de distintas sesiones |
| `Services/Seats/DisplayTopology.cs` | Enumerar monitores, activarlos, colocación en esquina y sacarlos del escritorio |
| `Services/Seats/SeatAudio.cs` | Salidas de audio y micrófonos disponibles |
| `FreeRDP/patches/0001-audin-winmm-choose-microphone.patch` | Parche de FreeRDP 3.33.0: `/microphone:sys:winmm,dev:<id de endpoint>` abre ese micrófono |
| `Services/Seats/SeatUsbCapture.cs` | Cambio de driver a WinUSB y vuelta (con registro en `%ProgramData%\OpenStreamMS\usb-capture.json`) y lectura de los informes USB |
| `Services/Seats/SeatHid.cs` | Descriptor de informe HID → eventos de ratón y scancodes de teclado |
| `Services/Seats/SeatInput.cs` | Respaldo: Raw Input en la consola y escudo del cursor |
| `Services/Seats/SeatIpc.cs` | Tuberías (ACL SYSTEM + usuario) y formato de mensajes binarios/JSON |
| `Core/Helpers/DeviceTopology.cs` | Dispositivos de entrada por puerto USB |
| `Core/Api/SeatEndpoints.cs` | API REST |
| `Core/wwwroot/seats.js` | Pestaña "Puestos" del dashboard |

## API

| Método | Ruta | Descripción |
|---|---|---|
| GET | `/api/seats` | Lista de puestos con su estado |
| GET | `/api/seats/inventory` | Monitores, salidas de audio, micrófonos y dispositivos USB libres (`?seatId=` incluye los del puesto que se edita) |
| POST | `/api/seats` | Crear puesto |
| PUT | `/api/seats/{id}` | Editar. Encendido solo admite cambiar el nombre y el autoarranque |
| PATCH | `/api/seats/{id}/autostart` | Cambiar el autoarranque (`{ "enabled": true }`), también con el puesto encendido |
| DELETE | `/api/seats/{id}` | Borrar |
| POST | `/api/seats/{id}/start` | Encender |
| POST | `/api/seats/{id}/stop` | Apagar |

## Encendido de un puesto

1. Arranca la sesión asignada si no lo está.
2. Si es exclusivo, pausa Sunshine (deja de servirse por Moonlight/WebRTC).
3. Activa el monitor (lo añade al escritorio) y lo coloca tocando solo la esquina inferior derecha.
4. Reconecta la sesión RDP a la resolución/Hz del puesto (la fijada o, si no hay, la que Windows le dio al monitor) y, si el puesto tiene micrófono, con él redirigido (siempre con FreeRDP: mstsc no deja elegirlo). Al apagar se restaura la de la sesión, sin micrófono.
5. Los mandos del puesto pasan a su sesión (HidHide).
6. Lanza `SeatAgent` en la sesión, duplica los handles NT de textura con `HandleBroker` y conecta `SeatHost` en la consola para presentar y enrutar entrada y audio.
7. Pasa los ratones y teclados del puesto a WinUSB (el dispositivo, o solo las interfaces con ratón/teclado si es compuesto) y empieza a leerlos.

Al apagar: se devuelven los ratones y teclados a su driver HID, se para el agente y
el presentador, se saca el monitor del escritorio, se reanuda Sunshine y los mandos
vuelven a la consola. Si el servicio se cae con un puesto encendido, los drivers se
restauran en su siguiente arranque.

## Requisitos y trampas conocidas

- **Usuarios nuevos**: la pantalla de privacidad de primer uso deja el escritorio
  estático. Completarla una vez o usar la directiva `DisablePrivacyExperience`.
- **Varias sesiones por usuario** (`fSingleSessionPerUser=0`): OpenStreamMS lo pone
  al crear la sesión. Windows lo trae a 1, y entonces, si el usuario ya tiene sesión
  (por ejemplo, la de consola), la RDP se queda con esa aunque TermWrap permita sesiones
  simultáneas. Si una directiva lo fuerza a 1, se avisa en el log. Efecto secundario:
  reconectar sin desconectar antes crea otra sesión.
- **Misma GPU**: el monitor del puesto y la sesión deben compartir adaptador.
- **Antitrampas**: la entrada inyectada lleva la marca `INJECTED`; algunos juegos
  con antitrampas pueden rechazarla.
- **Ratón/teclado del puesto en WinUSB**: mientras el puesto está encendido no
  funciona en la consola, y los LED del teclado (Bloq Mayús…) no se encienden.
  Si quedara así por un fallo: Administrador de dispositivos → "Dispositivo
  WinUsb" → Actualizar controlador → elegir "Dispositivo de entrada USB".
- **Contenido con DRM**: sale en negro en la captura (igual que en Sunshine).
- **Micrófono**: lo abre el cliente FreeRDP, que corre en la sesión 0 como SYSTEM. Si
  no llega audio, comprobar en Configuración → Privacidad → Micrófono que el acceso
  al micrófono esté permitido para el equipo y las aplicaciones de escritorio, y que
  la directiva «Permitir la redirección de grabación de audio» no esté deshabilitada
  (`fDisableAudioCapture`). Un micrófono desconectado al encender el puesto se omite
  (el puesto arranca sin él y queda un aviso en el log).
- **Recompilar FreeRDP**: `wfreerdp.exe` sale de [SlimFreeRDP](https://github.com/mlopezsegura/SlimFreeRDP)
  (FreeRDP 3.33.0 + OpenSSL 3.6.5, estático, solo los canales que se usan y con el parche
  del micrófono). Compilar con su `build.cmd` y copiar `wfreerdp.exe` a `FreeRDP/`. No trae
  `/gfx` ni `/rfx`, ni portapapeles ni unidades.
- **Audio del cliente RDP en el host**: `wfreerdp` va con `/sound:sys:fake` (descarta el
  audio redirigido; Sunshine lo captura antes, en «Remote Audio»). mstsc sí lo reproduce y
  se silencia su sesión de audio (`AudioSessionMuter`).
- **Consola anfitriona**: recomendable un usuario técnico con autologon, sin
  bloqueo, sin UAC en escritorio seguro y sin apagado de pantalla.

## Pruebas de referencia

El harness con el que se validó todo está en `E:\Workspace\seat-tests` (fuera del
repo) y sirve como batería de regresión si una actualización de Windows cambia el
comportamiento de RDP.
