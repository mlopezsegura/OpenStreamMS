# Mosaico (hasta cuatro sesiones en un stream)

Un **mosaico** es una sesión que se está retransmitiendo por Moonlight o WebRTC (la
**anfitriona**) a la que se unen en caliente hasta otras tres. El cliente ve las sesiones en una
cuadrícula de 2×2, como la pantalla dividida de los puestos locales, pero en el stream y no en un
monitor del PC.

## Cómo se usa

1. Conecta un cliente a la sesión anfitriona (Moonlight o una TV por WebRTC).
2. En el panel, en el menú **⋯** de otra sesión, elige **Unir al stream de…** y la anfitriona.
   La sesión se arranca si estaba parada.
3. La disposición se adapta a cuántas haya: 1 a pantalla entera, 2 en mitades (izquierda/derecha),
   3 o 4 en cuartos (con 3, el cuarto de abajo a la derecha queda en negro). Con dos, el menú de la
   anfitriona permite **Poner una encima de otra** (arriba/abajo) y volver con **Poner una al lado
   de otra**; la elección se recuerda si entran más sesiones y vuelven a quedar dos.
4. Para sacar una sesión: **Salir del mosaico** en su menú. En la anfitriona, **Deshacer el
   mosaico** saca a todas.

La tarjeta de cada sesión del mosaico lleva una etiqueta con un mapa 2×2 de su parte; al pasar el
ratón por encima se ve qué mando va a cada sesión.

## Decisiones de diseño

| Tema | Decisión | Motivo |
|---|---|---|
| Composición | En el Sunshine de la anfitriona (`capture = remoteidd`): abre los fotogramas RemoteIdd de cada sesión y los copia en un lienzo (`CopySubresourceRegion`, sin shaders) | Sin ventana presentadora ni recursión: la anfitriona es un cuartil más. Solo se copia la parte que cambió |
| Control | `osms-mosaic.txt` junto al `sunshine.conf` de la anfitriona; Sunshine lo relee cada 250 ms | Unir o sacar una sesión no corta el stream: Sunshine reinicia la captura |
| Lienzo | La resolución RDP de la anfitriona (`canvas W H`); el codificador lo escala al cliente como cualquier pantalla | `/api/streams` no da la resolución del cliente |
| Resolución de cada sesión | Se reconecta la RDP al tamaño de su parte (RemoteIdd acepta cualquier modo con `OsmsSeatWidth/Height`) | Sin escalado: cada sesión dibuja a su tamaño real |
| Sunshine de las invitadas | En pausa (`PauseSunshine`) | Nadie más lee su slot RemoteIdd; no se sirven a la vez por separado |
| Mandos | El mando N que crea el Sunshine de la anfitriona va a la sesión N (orden de llegada; 0 = anfitriona). Si no hay sesión N, se queda en la anfitriona. Si el mosaico cambia, se reasignan | Moonlight numera los mandos por orden de conexión |
| Teclado, ratón y audio | Solo los de la anfitriona; el ratón absoluto cae en su parte (fuera de ella se queda en el borde) | Primera versión |
| Fin del mosaico | Al sacar a la anfitriona, al pararse, o tras 60 s sin ningún cliente | Un cliente que se reconecta enseguida no lo pierde |

## Fichero de control

`sessions\<id>\Sunshine\config\osms-mosaic.txt`, lo escribe el servicio:

```
# OpenStreamMS mosaic: <Windows session id> <part>
canvas 1920 1080
7 TopLeft
9 TopRight
12 BottomLeft
```

Las partes son las de `SeatScreenArea`; Sunshine (`parse_mosaic_layout`, `mosaic_rect`) y el
servicio (`MosaicLayout`) reparten el lienzo igual que `SeatMonitor.Region`: con un tamaño impar,
la mitad izquierda o de arriba se queda el lado menor. Al arrancar, el servicio borra los ficheros
que hubieran quedado de una ejecución anterior.

## API

| Método | Ruta | Descripción |
|---|---|---|
| GET | `/api/sessions/{id}/mosaic` | Miembros por orden de llegada con su parte |
| POST | `/api/sessions/{id}/mosaic/{guestId}` | Une `guestId` al stream de `{id}` |
| DELETE | `/api/sessions/{id}/mosaic/{memberId}` | Saca a `memberId`; si es la anfitriona, deshace el mosaico |
| PUT | `/api/sessions/{id}/mosaic/layout` | Con dos sesiones, `{ "stacked": true }` una encima de otra; `false`, una al lado de otra |

`GET /api/sessions` incluye en cada sesión `mosaicHost`, `mosaicIndex` y, en la anfitriona,
`mosaicCount` y `mosaicStacked`.

## Requisitos y límites

- Todas las sesiones con captura **RemoteIdd** y en la misma GPU. Sin HDR.
- La anfitriona necesita un cliente conectado para empezar; una invitada no puede tener uno propio.
- Una sesión no puede estar a la vez en un mosaico y en un puesto local encendido.
- Al unir o sacar una sesión, las que cambian de tamaño se reconectan (~1-2 s cada una).
- Si el servicio se reinicia con un mosaico activo, el mosaico se pierde y las sesiones se quedan
  con el tamaño de su parte hasta su siguiente reconexión o reinicio.

## Archivos

| Archivo | Qué hace |
|---|---|
| `Services/Mosaic/MosaicService.cs` | Unir, sacar, recolocar y vigilar los mosaicos |
| `Services/Mosaic/MosaicLayout.cs` | Partes por número de sesiones, tamaños y contenido de `osms-mosaic.txt` |
| `Core/Api/MosaicEndpoints.cs` | API REST |
| `Services/Gamepad/GamepadSessionIsolation.cs` | Mando N de la anfitriona → sesión N |
| `Native/RemoteIdd/FrameConsumer.*` | `ReadyEvent()` y `LatestFrameId()` para esperar a varias sesiones |
| Sunshine-Web-RTC `src/platform/windows/display_remoteidd.cpp` | Lectura del fichero y lienzo del mosaico |
