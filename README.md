<img width="1100" height="306" alt="image" src="https://github.com/user-attachments/assets/41a93d90-00d3-4eb4-ba67-767be534ae00" />


# OpenStreamMS

Windows service that automates the setup of a game streaming environment using [Sunshine](https://github.com/ClassicOldSong/Sunshine/) (Moonlight/GameStream host).

Manages the full lifecycle of isolated RDP streaming sessions — creation, monitoring, auto-restart, and teardown — from a web dashboard or REST API.

---
<img width="1101" height="195" alt="image" src="https://github.com/user-attachments/assets/d749b03f-c2a4-482b-aca6-04525fb0391c" />

## Features

- **Isolated RDP sessions** — each session runs under its own Windows user account with a dedicated Sunshine instance, fully separated from your local desktop
- **Independent stream profile** — launches Explorer, Sunshine, and Sunshine apps with a per-session profile sandbox (`USERPROFILE`, `APPDATA`, `LOCALAPPDATA`, temp, and XDG paths) so local and stream sessions do not share common profile lock files
- **Web dashboard + REST API** — manage sessions from any browser; full OpenAPI docs at `/scalar/v1`
- **System tray icon** — start/stop sessions and open the web interface from the Windows notification area
- **Auto-start** — sessions flagged as enabled are relaunched automatically when the service restarts
- **5-second monitoring** — Sunshine is restarted automatically if it crashes
- **Virtual display support (VDD)** — patches Sunshine's `apps.json` to keep a virtual display active even when the RDP client disconnects

---

## Installation

### Option A — Installer (recommended)

1. Download `OpenStreamMS-Setup-x.x.x.exe` from [Releases](https://github.com/mlopezsegura/OpenStreamMS/releases)
2. Run it as Administrator and follow the wizard
3. The installer registers the Windows service, adds the tray icon to Windows startup, and optionally starts everything immediately

To uninstall, use **Apps & Features** → *OpenStreamMS* → Uninstall.

### Option B — Manual (from source)

**Requirements**

- Windows 10/11 (x64)
- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [Sunshine](https://github.com/ClassicOldSong/Sunshine/) installed
- Remote Desktop enabled (*System Properties → Remote → Allow remote connections*)
- *(Optional)* [SudoVDA](https://github.com/itsmikethetech/VirtualDisplay) for virtual display support

**Build & install**

```bat
dotnet publish -c Release -r win-x64 --self-contained -o publish\
cd publish
OpenStreamMS.exe --install
```

**Uninstall**

```bat
OpenStreamMS.exe --uninstall
```

---

## Configuration

Edit `service.config.json` in the install directory (created automatically on first run):

```json
{
  "StreamUser": {
    "Username": "streamuser",
    "Domain":   ".",
    "Password": "YourPassword"
  },
  "SunshineExePath": "C:\\Program Files\\Sunshine\\Sunshine.exe",
  "RdpBackground":   true,
  "VddEnabled":      false,
  "ApiPort":         5000,
  "AdminUsername":   "",
  "AdminPasswordHash": "",
  "AuthSecret":      ""
}
```

| Field | Default | Description |
|-------|---------|-------------|
| `StreamUser.Username` | `streamuser` | Local Windows account for the streaming session |
| `StreamUser.Domain` | `.` | Domain (`.` = local machine) |
| `StreamUser.Password` | — | Password for the stream user |
| `SunshineExePath` | `Sunshine\sunshine.exe` | Path to `Sunshine.exe` |
| `RdpBackground` | `true` | Hide the FreeRDP window (set `false` for debugging) |
| `VddEnabled` | `false` | Enable virtual display patch in Sunshine's `apps.json` |
| `ApiPort` | `5000` | Port for the web dashboard and REST API |
| `AdminUsername` | — | Web UI admin username (configured on first run) |

---

## Web Dashboard

Open `http://localhost:5000` in any browser.

On first access you will be prompted to create admin credentials. After that, the dashboard lets you:

- Create, start, stop, and delete streaming sessions
- Enable auto-start per session
- Enable **independent stream profile** (runs stream apps with their own profile paths)
- View real-time per-session logs
- Access the interactive API docs at `/scalar/v1`

---

## System Tray

The tray icon (`OpenStreamMS.exe --tray`) runs in the user's desktop session and provides quick access without opening a browser:

- **Double-click** → opens the web dashboard
- **Right-click** → context menu with per-session Start/Stop buttons and *Open web interface*

The tray icon is registered at Windows startup automatically during installation.

---

## Independent Stream Profile

When **independent stream profile** is enabled for a session, OpenStreamMS launches Explorer, Sunshine, and Sunshine apps with a separate profile root:

```
C:\Users\<user>                    ← your normal local profile
C:\Users\<user>\_OpenStreamMS_Stream ← stream session profile sandbox
```

The sandbox sets `USERPROFILE`, `HOME`, `APPDATA`, `LOCALAPPDATA`, `LOCALAPPDATALOW`, `TEMP`, `TMP`, and XDG paths for the stream process tree. Sunshine also receives the same values in `apps.json`, so commands launched from Moonlight inherit the isolated profile even after Sunshine restarts.

This is native Windows process/profile isolation, not Windows Sandbox. Windows Sandbox is a separate VM and cannot be attached transparently to the existing RDP/Sunshine session without a different streaming architecture.

Applications that use machine-global mutexes, HKCU registry keys, or their own installation directory for locks may still require a separate Windows user or a separate app installation.

---

## CLI Reference

```
OpenStreamMS.exe --run               Run the service (called by Windows SCM)
OpenStreamMS.exe --install           Install and register the Windows service
OpenStreamMS.exe --install --silent  Install without prompts (used by installer)
OpenStreamMS.exe --uninstall         Stop and remove the Windows service
OpenStreamMS.exe --tray              Show system tray icon
OpenStreamMS.exe --help              Show help
```

---

## Building the Installer

Requires [Inno Setup 6](https://jrsoftware.org/isinfo.php).

```bat
cd Installer
build-installer.bat
```

The resulting `OpenStreamMS-Setup-x.x.x.exe` is placed in `dist\`.

---

## Architecture

```
OpenStreamMS.exe  (Windows Service, SYSTEM)
├── Program.cs              — CLI entry point (--run / --install / --uninstall / --tray)
├── OpenstreamService.cs    — BackgroundService; owns the 5-second monitoring loop
├── ServiceConfig.cs        — Loads/saves service.config.json
├── RdpSessionCreator.cs    — Creates the loopback RDP session via WTS API + FreeRDP
├── SunshineManager.cs      — Launches and monitors Sunshine.exe inside the RDP session
├── SunshineConfigurator.cs — Patches apps.json to enable virtual-display support
├── ProcessInSession.cs     — CreateProcessAsUser wrapper; supports custom env overrides
├── StreamProfileSetup.cs   — Builds the stream profile sandbox; restarts Explorer with new env
├── TrayApp.cs              — WinForms ApplicationContext for the system tray icon
├── WTSSessionManager.cs    — WTS session enumeration helper
├── Logger.cs               — Per-session timestamped logger
└── Api/
    ├── SessionEndpoints.cs      — REST endpoints (/api/sessions/*)
    ├── StreamSessionService.cs  — Session CRUD + lifecycle (start/stop/monitor)
    ├── StreamSession.cs         — Session data model + state machine
    ├── AuthService.cs           — Cookie + HTTP Basic Auth
    └── CredentialTester.cs      — Windows credential validation (LogonUser)
```

---

## REST API

| Method | Path | Description |
|--------|------|-------------|
| `GET` | `/api/sessions` | List all sessions |
| `POST` | `/api/sessions` | Create a session |
| `GET` | `/api/sessions/{id}` | Get session details |
| `DELETE` | `/api/sessions/{id}` | Delete a stopped session |
| `POST` | `/api/sessions/{id}/start` | Start a session |
| `POST` | `/api/sessions/{id}/stop` | Stop a session (force-closes the Windows session) |
| `PATCH` | `/api/sessions/{id}/enabled` | Toggle auto-start |
| `GET` | `/api/sessions/{id}/logs` | Fetch session logs |
| `POST` | `/api/sessions/test-credentials` | Validate Windows credentials |

Interactive docs: `http://localhost:5000/scalar/v1`

---

## License

OpenStreamMS is released under the [MIT License](LICENSE).

The bundled Sunshine (GPL-3.0) and FreeRDP (Apache-2.0) binaries keep their own licenses — see [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
