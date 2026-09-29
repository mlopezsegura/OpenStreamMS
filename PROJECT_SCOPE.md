# OpenStreamMS - Descripcion y alcance

## Descripcion

OpenStreamMS es un servicio de Windows para crear y gestionar sesiones de streaming de juegos basadas en Sunshine y Moonlight. El sistema automatiza la creacion de sesiones RDP aisladas, lanza una instancia dedicada de Sunshine dentro de cada sesion y ofrece una interfaz web y una API REST para administrar el ciclo de vida completo.

El objetivo principal es permitir varias sesiones de streaming independientes sin interferir con el escritorio local del usuario. Cada sesion puede tener su propia configuracion de Sunshine, perfil de ejecucion, puerto de streaming, clientes Moonlight emparejados, logs y estado operativo.

## Objetivos del proyecto

- Ejecutar OpenStreamMS como servicio de Windows gestionado por el Service Control Manager.
- Crear, iniciar, detener, reiniciar y eliminar sesiones de streaming desde una interfaz web.
- Lanzar sesiones RDP con FreeRDP y controlar procesos dentro de la sesion de usuario correspondiente.
- Ejecutar Sunshine por sesion con configuracion y estado persistentes.
- Conservar configuraciones modificadas desde el panel de Sunshine cuando no sean claves gestionadas por OpenStreamMS.
- Gestionar emparejamiento de clientes Moonlight mediante proxy autenticado hacia Sunshine.
- Aislar perfiles de stream para reducir conflictos con aplicaciones abiertas en la sesion local.
- Exponer una API REST documentada con OpenAPI/Scalar.
- Ofrecer instalador, servicio de Windows e icono de bandeja para operacion diaria.

## Alcance funcional

### Gestion de sesiones

OpenStreamMS mantiene sesiones persistidas en `sessions.json`. Cada sesion incluye usuario Windows, dominio, resolucion RDP, ruta de Sunshine, puerto base de streaming, flags de autoarranque, VDD, perfil aislado, credenciales internas del panel Sunshine y estado operativo.

El servicio permite:

- Crear sesiones nuevas previa validacion de credenciales Windows.
- Iniciar sesiones de forma asincrona.
- Detener sesiones y cerrar los procesos asociados.
- Reiniciar sesiones completas cuando se detectan fallos.
- Habilitar autoarranque por sesion.
- Consultar logs por sesion.

### Integracion con Sunshine

Cada sesion usa una instancia propia de Sunshine ubicada bajo `sessions/<sessionId>/Sunshine`. OpenStreamMS gestiona las claves necesarias de `sunshine.conf`, como puertos, rutas de estado, certificados, logs y ficheros de apps, pero conserva el resto de configuracion guardada por el usuario.

El estado de Sunshine se guarda en `sunshine_state.json`, incluyendo credenciales y clientes emparejados. OpenStreamMS debe conservar los dispositivos Moonlight emparejados y solo modificar credenciales cuando corresponda.

La rotacion de credenciales del panel Sunshine es configurable por sesion. Si esta activada, OpenStreamMS puede generar credenciales nuevas al arrancar o reparar una desincronizacion del proxy. Si esta desactivada, las credenciales permanecen estables.

### Perfil de stream independiente

Cuando se activa el perfil independiente, OpenStreamMS prepara rutas separadas para `USERPROFILE`, `APPDATA`, `LOCALAPPDATA`, temporales y variables XDG. Sunshine y las aplicaciones lanzadas desde Moonlight heredan ese entorno para reducir conflictos de perfiles, locks y sesiones simultaneas.

### Interfaz web y API

La interfaz web vive en `Core/wwwroot` y permite operar sesiones, ver logs, configurar opciones avanzadas de Sunshine, gestionar clientes emparejados, instalar o revisar dependencias como RDP Wrapper y ViGEmBus, y abrir el panel de Sunshine.

La API se expone desde ASP.NET Core en el puerto configurado por `service.config.json`. Incluye endpoints para sesiones, autenticacion, proxy a Sunshine, RDP Wrapper y ViGEmBus.

### Instalacion y operacion

El proyecto incluye:

- CLI principal en `Program.cs`.
- Servicio Windows con `--run`.
- Instalacion y desinstalacion con `--install` y `--uninstall`.
- Icono de bandeja con `--tray`.
- Instalador Inno Setup en `Core/Installer`.

## Fuera de alcance

- Reemplazar Sunshine, Moonlight, FreeRDP, RDP Wrapper o ViGEmBus.
- Implementar un servidor de streaming propio.
- Gestionar licencias o politicas de Windows RDP fuera de las integraciones existentes.
- Garantizar aislamiento total de aplicaciones que usen recursos globales del sistema, mutexes globales, claves HKCU compartidas o locks propios fuera del perfil.
- Exponer el panel web de Sunshine directamente a la red como superficie publica principal.
- Proveer soporte multiplataforma; el proyecto esta orientado a Windows.

## Componentes principales

- `Program.cs`: entrada CLI, configuracion del host web, servicio Windows, instalacion, desinstalacion y tray.
- `Core/Api`: endpoints REST, autenticacion, proxy Sunshine y modelo de sesiones.
- `Core/Helpers`: integracion con WTS, RDP, perfiles de stream, procesos en sesion y configuracion del servicio.
- `Services/OpenStream`: background service y monitorizacion periodica.
- `Services/Sunshine`: gestion, configuracion y deteccion de fallos de Sunshine.
- `Services/Session`: gestion de RDP Wrapper y audio por sesion.
- `Services/VigEmBus`: gestion del driver de gamepad virtual.
- `Services/TrayApp`: icono de bandeja.
- `Services/Logger`: logging global y por sesion con niveles.
- `Core/wwwroot`: dashboard web, estilos, traducciones e interacciones cliente.

## Restricciones tecnicas

- Requiere Windows 10/11 x64.
- Requiere .NET 10 para compilar.
- Usa APIs Windows como WTS, CreateProcessAsUser, ServiceController, registro y COM/WASAPI.
- Requiere permisos elevados para instalacion, servicio, firewall, RDP Wrapper y drivers.
- Sunshine y FreeRDP se tratan como dependencias externas copiadas o instaladas junto al proyecto.
- Algunas operaciones dependen del estado real del sistema operativo, por lo que no todas son testeables con unit tests puros.

## Criterios de calidad

- No sobrescribir configuracion persistente de Sunshine salvo claves gestionadas explicitamente por OpenStreamMS.
- Mantener compatibilidad hacia atras con `sessions.json` existente mediante valores por defecto.
- Conservar logs suficientes para diagnosticar inicios, reinicios, fallos de Sunshine, errores RDP y operaciones de dependencias.
- Evitar bloquear el servicio por errores de logging o por fallos recuperables de configuracion.
- Compilar sin warnings y validar cambios con `dotnet build`.
- Mantener cambios de UI, API y persistencia sincronizados cuando se agregue una propiedad de sesion.

