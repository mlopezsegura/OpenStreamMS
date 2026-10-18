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
| Consola | Solo anfitriona (usuario técnico, autologon) | Recomendado; con WinUSB los periféricos del puesto ya no llegan a la consola |

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
| `Services/Seats/SeatAudio.cs` | Salidas de audio disponibles |
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
| GET | `/api/seats/inventory` | Monitores, salidas de audio y dispositivos USB libres (`?seatId=` incluye los del puesto que se edita) |
| POST | `/api/seats` | Crear puesto |
| PUT | `/api/seats/{id}` | Editar (puesto detenido) |
| PATCH | `/api/seats/{id}/autostart` | Cambiar el autoarranque (`{ "enabled": true }`), también con el puesto encendido |
| DELETE | `/api/seats/{id}` | Borrar |
| POST | `/api/seats/{id}/start` | Encender |
| POST | `/api/seats/{id}/stop` | Apagar |

## Encendido de un puesto

1. Arranca la sesión asignada si no lo está.
2. Si es exclusivo, pausa Sunshine (deja de servirse por Moonlight/WebRTC).
3. Activa el monitor (lo añade al escritorio) y lo coloca tocando solo la esquina inferior derecha.
4. Reconecta la sesión RDP a la resolución/Hz del puesto (la fijada o, si no hay, la que Windows le dio al monitor). Al apagar se restaura la de la sesión.
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
- **Varias sesiones por usuario** (`fSingleSessionPerUser=0`): reconectar sin
  desconectar antes crea otra sesión.
- **Misma GPU**: el monitor del puesto y la sesión deben compartir adaptador.
- **Antitrampas**: la entrada inyectada lleva la marca `INJECTED`; algunos juegos
  con antitrampas pueden rechazarla.
- **Ratón/teclado del puesto en WinUSB**: mientras el puesto está encendido no
  funciona en la consola, y los LED del teclado (Bloq Mayús…) no se encienden.
  Si quedara así por un fallo: Administrador de dispositivos → "Dispositivo
  WinUsb" → Actualizar controlador → elegir "Dispositivo de entrada USB".
- **Contenido con DRM**: sale en negro en la captura (igual que en Sunshine).
- **Consola anfitriona**: recomendable un usuario técnico con autologon, sin
  bloqueo, sin UAC en escritorio seguro y sin apagado de pantalla.

## Pruebas de referencia

El harness con el que se validó todo está en `E:\Workspace\seat-tests` (fuera del
repo) y sirve como batería de regresión si una actualización de Windows cambia el
comportamiento de RDP.
