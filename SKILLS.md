# Skills necesarias para desarrollar OpenStreamMS

## Lenguaje y plataforma

- C# moderno con nullable reference types, records, async/await y colecciones concurrentes.
- .NET 10 y ASP.NET Core minimal APIs.
- Windows Forms para el icono de bandeja.
- MSBuild, publicacion self-contained y empaquetado para Windows.

## Windows internals

- Servicios de Windows y `Microsoft.Extensions.Hosting.WindowsServices`.
- Service Control Manager, permisos elevados y ejecucion bajo `LocalSystem`.
- APIs WTS para enumerar, crear, desconectar y cerrar sesiones.
- `CreateProcessAsUser`, tokens de usuario, perfiles, variables de entorno y procesos por sesion.
- Registro de Windows para configuracion de servicios, instaladores y autoarranque.
- Firewall de Windows via `netsh`.
- COM/WASAPI para controlar sesiones de audio por proceso.

## Streaming y escritorio remoto

- Funcionamiento de Sunshine como host Moonlight/GameStream.
- Estructura de configuracion de Sunshine: `sunshine.conf`, `apps.json`, `sunshine_state.json`, certificados y rutas persistentes.
- Emparejamiento Moonlight, gestion de clientes y credenciales del panel Sunshine.
- FreeRDP y creacion de sesiones RDP loopback.
- RDP Wrapper y limitaciones de TermService en Windows cliente.
- Virtual display drivers y seleccion de output/captura.

## Backend y API

- Diseño de APIs REST con ASP.NET Core minimal APIs.
- Serializacion JSON con `System.Text.Json`.
- OpenAPI y Scalar para documentacion interactiva.
- Autenticacion web con cookies y Basic Auth local/proxy.
- Gestion de errores estructurados para UI y API.
- Tareas en segundo plano, monitorizacion periodica y seguridad de concurrencia.

## Frontend

- HTML, CSS y JavaScript sin framework.
- Formularios, modales, tablas/listas dinamicas y polling de estado.
- Internacionalizacion con catalogo JS (`i18n.js`).
- UX operativa para herramientas de administracion: estados claros, errores accionables y controles consistentes.
- Sincronizacion entre DTOs del backend y payloads del frontend.

## Persistencia y configuracion

- Manejo seguro de ficheros JSON y configuracion incremental.
- Compatibilidad hacia atras en modelos persistidos como `sessions.json`.
- Estrategias para no sobrescribir configuracion editada por herramientas externas.
- Gestion de secretos operativos: credenciales Windows, credenciales Sunshine y hashes de autenticacion.
- Rutas relativas/absolutas y separacion por instancia de sesion.

## Instalacion y distribucion

- Inno Setup para construir instaladores.
- Firma, permisos de instalacion, accesos directos y desinstalacion.
- Copia de dependencias externas como Sunshine y FreeRDP.
- Scripts PowerShell y batch de soporte.
- Diagnostico de instalaciones fallidas en equipos Windows reales.

## Testing y diagnostico

- Compilacion con `dotnet build`.
- Pruebas manuales de flujos Windows: instalar servicio, iniciar sesion, emparejar cliente, reiniciar, detener y desinstalar.
- Lectura de logs de aplicacion, logs por sesion y eventos de Windows.
- Reproduccion de fallos de Sunshine, FreeRDP, RDP Wrapper, firewall y credenciales.
- Validacion de compatibilidad con sesiones existentes y migraciones de datos.

## Seguridad

- Principio de minimo alcance en endpoints locales y proxy a Sunshine.
- Evitar exposicion innecesaria del panel Sunshine.
- Tratamiento cuidadoso de credenciales en logs y respuestas API.
- Comprension de riesgos de ejecutar procesos como `LocalSystem` y lanzar procesos en sesiones de usuario.
- Validacion de rutas y entradas antes de ejecutar procesos o tocar ficheros sensibles.

## Conocimiento del dominio

- Moonlight/Sunshine y flujo de emparejamiento.
- Diferencias entre captura WGC, DDX, NVFBC y codificadores NVENC, Quick Sync, AMD VCE y software.
- Restricciones de audio en sesiones RDP y redireccion de audio.
- Problemas habituales de perfiles compartidos en aplicaciones de juegos, launchers y navegadores.
- Comportamiento de Windows al mantener sesiones RDP desconectadas.

## Flujo recomendado de desarrollo

1. Revisar el modulo afectado y sus contratos API/UI antes de editar.
2. Mantener cambios acotados y compatibles con sesiones existentes.
3. Si se agrega una propiedad de sesion, actualizar modelo, DTOs, persistencia, UI, traducciones y documentacion.
4. Ejecutar `dotnet build` antes de cerrar el cambio.
5. Para cambios de ciclo de vida, probar manualmente inicio, parada, reinicio y autoarranque.
6. Para cambios de Sunshine, verificar que no se pierdan `apps.json`, `sunshine.conf` ni clientes emparejados en `sunshine_state.json`.

