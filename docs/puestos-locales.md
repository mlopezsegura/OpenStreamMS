# Puestos locales (multiseat tipo ASTER)

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
| Teclado/ratón | Raw Input en la consola + inyección en la sesión | Sin drivers ni software extra (HidHide no permite leer HID de teclado/ratón; un hook no sabe de qué dispositivo viene cada evento) |
| Mandos | HidHide: jail a la sesión del puesto | Ya lo usaba OpenStreamMS para la consola |
| Audio | Loopback de "Audio remoto" → salida del puesto | ~70 ms con búferes de 10 ms |
| Consola | Solo anfitriona (usuario técnico, autologon) | Los periféricos de los puestos también llegan a la consola; nadie debe usarla |

## Componentes

```
Servicio (SYSTEM, sesión 0)
 ├─ SeatManager ............ puestos, inventario, encendido/apagado          [hecho]
 ├─ StreamSessionService ... ReconnectRdp, PauseSunshine/ResumeSunshine       [hecho]
 ├─ GamepadSessionIsolation  mandos → sesión del puesto (por puerto USB)      [hecho]
 └─ broker de handles ...... duplica texturas agente → presentador            [pendiente]

Consola (app de bandeja)
 └─ SeatHost ............... presentador, audio, Raw Input                    [pendiente]

Sesión del puesto
 └─ SeatAgent .............. captura, cursor, audio, inyección                [pendiente]
```

Lo pendiente está descrito con clases y funciones en
[`SEAT-AGENT-TODO.md`](SEAT-AGENT-TODO.md).

## Archivos

| Archivo | Qué hace |
|---|---|
| `Services/Seats/Seat.cs` | Modelo (`Seat`, `SeatMonitor`, `SeatDevice`) y persistencia en `seats.json` |
| `Services/Seats/SeatManager.cs` | Orquestación; marca con `TODO` dónde se enchufa el agente |
| `Services/Seats/DisplayTopology.cs` | Enumerar monitores, activarlos y sacarlos del escritorio |
| `Services/Seats/SeatAudio.cs` | Salidas de audio disponibles |
| `Services/Seats/SeatIpc.cs` | Tuberías (ACL SYSTEM + usuario) y formato de mensajes |
| `Core/Helpers/DeviceTopology.cs` | Dispositivos de entrada por puerto USB |
| `Core/Api/SeatEndpoints.cs` | API REST |
| `Core/wwwroot/seats.js` | Pestaña "Puestos" del dashboard |

## API

| Método | Ruta | Descripción |
|---|---|---|
| GET | `/api/seats` | Lista de puestos con su estado |
| GET | `/api/seats/inventory` | Monitores, salidas de audio y dispositivos USB libres |
| POST | `/api/seats` | Crear puesto |
| PUT | `/api/seats/{id}` | Editar (puesto detenido) |
| DELETE | `/api/seats/{id}` | Borrar |
| POST | `/api/seats/{id}/start` | Encender |
| POST | `/api/seats/{id}/stop` | Apagar |

## Encendido de un puesto

1. Arranca la sesión asignada si no lo está.
2. Si es exclusivo, pausa Sunshine (deja de servirse por Moonlight/WebRTC).
3. Reconecta la sesión RDP a la resolución/Hz del monitor del puesto.
4. Activa el monitor (lo añade al escritorio).
5. Los mandos del puesto pasan a su sesión (HidHide).
6. *(pendiente)* Lanza el agente en la sesión y el presentador en la consola.

Al apagar: se para el agente *(pendiente)*, se saca el monitor del escritorio,
se reanuda Sunshine y los mandos vuelven a la consola.

## Requisitos y trampas conocidas

- **Usuarios nuevos**: la pantalla de privacidad de primer uso deja el escritorio
  estático. Completarla una vez o usar la directiva `DisablePrivacyExperience`.
- **Varias sesiones por usuario** (`fSingleSessionPerUser=0`): reconectar sin
  desconectar antes crea otra sesión.
- **Misma GPU**: el monitor del puesto y la sesión deben compartir adaptador.
- **Antitrampas**: la entrada inyectada lleva la marca `INJECTED`; algunos juegos
  con antitrampas pueden rechazarla.
- **Contenido con DRM**: sale en negro en la captura (igual que en Sunshine).
- **Consola anfitriona**: recomendable un usuario técnico con autologon, sin
  bloqueo, sin UAC en escritorio seguro y sin apagado de pantalla.

## Pruebas de referencia

El harness con el que se validó todo está en `E:\Workspace\seat-tests` (fuera del
repo) y sirve como batería de regresión si una actualización de Windows cambia el
comportamiento de RDP.
