# Compilar RemoteIdd

```
Native\RemoteIdd\build.cmd [carpeta de salida]     (por defecto Native\RemoteIdd\bin)
```

Solo hace falta **Visual Studio / Build Tools con C++**. **No se instala el WDK**: el
script descarga una vez el paquete NuGet oficial `Microsoft.Windows.WDK.x64`
(10.0.26100.6584, ~110 MB) en `%LOCALAPPDATA%\OpenStreamMS\wdk-nuget` y compila contra él.
Para limpiarlo basta con borrar esa carpeta.

Qué hace, en orden (para en el primer fallo):
1. Compila y ejecuta `test\SharedFrameTest.exe` (contrato de `SharedFrame.h`).
2. Compila y ejecuta `RemoteIddPerf.exe` (rendimiento entre procesos en la GPU real; se
   salta con `set RIDD_NOPERF=1`).
3. Compila `RemoteIdd.dll` con `/W4 /WX` contra **IddCx 1.4** y **UMDF 2.33**: la base
   más baja con IDD remotos (Windows 10 1903), para no exigir un Windows más nuevo.
4. Sella `RemoteIdd.inf` con `stampinf` y lo valida con `infverif`.

**No firma ni instala nada.**

## Estado esperado al instalar (hoy)
- El INF instala y el WUDFHost carga `RemoteIdd.dll`.
- `EvtDeviceD0Entry` llega hasta `IddCxAdapterInitAsync`, que **devuelve error**: el
  adaptador no lo ha creado la pila de escritorio remoto. Es el comportamiento esperado.
  Se ve con DebugView (`RemoteIdd: IddCxAdapterInitAsync 0x...`).
- No aparece ningún monitor. **Correcto**: falta el proveedor de protocolo RDS (D1).

## Firma
Es el paso más invasivo y conviene evitarlo en el PC de trabajo:
- **Pruebas**: firma de test, que exige `bcdedit /set testsigning on` (cambio global del
  arranque, marca de agua en el escritorio). Hacerlo solo en una VM o en un equipo de
  pruebas, nunca en el PC de los puestos.
- **Uso real**: firma *attestation* en el Partner Center de Microsoft (certificado EV).
  No toca el arranque ni la configuración de Windows.

## Desinstalar sin dejar rastro
```
pnputil /enum-devices /class Display      (localizar Root\OsmsRemoteIdd\...)
pnputil /remove-device <instancia>
pnputil /enum-drivers                     (localizar el oemNN.inf de OpenStreamMS)
pnputil /delete-driver oemNN.inf /uninstall
```
El driver vive solo en el Driver Store y no escribe registro ni ficheros propios.

## Siguiente hito real
Proveedor de protocolo RDS (D1 en `TASKS.md`). Ver principios de mínima invasión en
`README.md`.
