<img width="1100" height="306" alt="image" src="https://github.com/user-attachments/assets/41a93d90-00d3-4eb4-ba67-767be534ae00" />


# OpenStreamMS

Windows service that automates the setup of a game streaming environment using [Sunshine](https://github.com/ClassicOldSong/Sunshine/) (Moonlight/GameStream host).

Manages the full lifecycle of isolated RDP streaming sessions — creation, monitoring, auto-restart, and teardown — from a web dashboard or REST API. With extra monitors, keyboards, mice and gamepads, the same sessions become **local seats**: several people using one PC at the same time, each with their own screen, input and sound.

---
<img width="1101" height="195" alt="image" src="https://github.com/user-attachments/assets/d749b03f-c2a4-482b-aca6-04525fb0391c" />

## Features

- **Isolated RDP sessions** — each session runs under its own Windows user account with a dedicated Sunshine instance, fully separated from your local desktop
- **Local desktop session** — the PC's own physical desktop can be added as one more session: Sunshine is launched directly in the PC's local session (no RDP), and stopping it never signs anyone out
- **Independent stream profile** — launches Explorer, Sunshine, and Sunshine apps with a per-session profile sandbox (`USERPROFILE`, `APPDATA`, `LOCALAPPDATA`, temp, and XDG paths) so local and stream sessions do not share common profile lock files
- **Web dashboard + REST API** — manage sessions from any browser; full OpenAPI docs at `/scalar/v1`
- **System tray icon** — start/stop sessions and open the web interface from the Windows notification area
- **Auto-start** — sessions flagged as enabled are relaunched automatically when the service restarts
- **5-second monitoring** — Sunshine is restarted automatically if it crashes
- **Virtual display support (VDD)** — patches Sunshine's `apps.json` to keep a virtual display active even when the RDP client disconnects
- **Local seats** — give a session a physical monitor, keyboard, mouse, gamepad and audio output so someone can use it at the PC itself, alongside the console and other seats (see [Local seats](#local-seats))
- **Isolated users** — a session can run under its own auto-managed local user (`osms-…`), optionally ephemeral (wiped on stop); isolated sessions get a *Steam (isolated)* shortcut and handle `steam://` links so each one can sign in to its own Steam account
- **Moonlight, WebRTC or both** — with the [sunshine-webrtc](https://github.com/mlopezsegura/Sunshine-Web-RTC) build, each session serves Moonlight apps, Samsung TVs running Moonlight WebRTC, or both, switchable live from the dashboard or from Sunshine's own panel; OpenStreamMS opens only the ports of the active protocols and refuses sessions whose ports collide

---

## Installation

### Option A — Installer (recommended)

1. Download `OpenStreamMS-Setup-x.x.x.exe` from [Releases](https://github.com/mlopezsegura/OpenStreamMS/releases)
2. Run it as Administrator and follow the wizard
3. The installer registers the Windows service, adds the tray icon to Windows startup, and optionally starts everything immediately

To uninstall, use **Apps & Features** → *OpenStreamMS* → Uninstall. It asks two questions:

- **Delete all data?** Yes removes sessions, seats and settings, the isolated `osms-…` users with their profiles (including games and settings stored in them), stream profiles, `%ProgramData%\OpenStreamMS` and temporary files. No keeps sessions and settings for a reinstall.
- **Also uninstall RDP Wrapper, ViGEmBus and HidHide?** Answer No if other applications use them.

Silent uninstall: `unins000.exe /VERYSILENT [/PURGEDATA] [/REMOVEDEPS]`.

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
- Switch each session between **Moonlight**, **WebRTC** and **both** from its card
- View real-time per-session logs
- Create **local seats** in the *Seats* tab, test their monitor and audio output, and see which session each seat uses
- Access the interactive API docs at `/scalar/v1`

---

## Streaming protocols and ports

Sessions run the Sunshine in `Sunshine\` (or the one set in `SunshineExePath`). With the
[sunshine-webrtc](https://github.com/mlopezsegura/Sunshine-Web-RTC) build, a session can serve:

| Protocol | Clients | Ports opened in the firewall |
|---|---|---|
| **Moonlight** | Moonlight apps | TCP+UDP `port-5 .. port+21` (HTTPS, HTTP, video/control/audio, RTSP) |
| **WebRTC** | Samsung TVs with Moonlight WebRTC | TCP `WebRtcPort`, UDP 8000 (TV discovery) and UDP `WebRtcMediaPortMin..Max` |
| **Both** (default for new sessions) | Both | All of the above |

The switch on each session card (and `PUT /api/sessions/{id}/protocol`) changes it live: OpenStreamMS
writes `stream_protocol` to the instance's `sunshine.conf`, opens or closes the ports and restarts only
Sunshine, keeping the RDP session. Sunshine's own panel has the same switch on its **Network** tab;
when Sunshine restarts, OpenStreamMS adopts the value saved there, so the last change wins. WebRTC
ports are managed by OpenStreamMS and rewritten on every start.

New sessions get free ports automatically (`GET /api/sessions/ports/suggest`: Moonlight base in steps
of 100 from 47989, WebRTC from 8000, media in blocks of 20 from 40000). Creating or editing a session
whose ports overlap another session's is refused, and so is starting one whose ports are in use by a
running session. Ports of a protocol that is off do not count. UDP 8000 is shared: every instance
answers a TV's discovery broadcast with its own name and port. Sessions created before this feature
stay **Moonlight** until switched.

The bundled `Sunshine\` is a sunshine-webrtc build. To use another Sunshine, replace the contents of
`Sunshine\` (or point `SunshineExePath` at one). Existing sessions pick up the new binaries on their next start; their
`config\` (settings, paired Moonlight clients and TVs) is kept. A Sunshine without WebRTC support
ignores the protocol and the dashboard warns on sessions set to WebRTC or both. Paired TVs
(`webrtc_tv_clients.json`) are backed up with `sunshine_state.json`.

---

## Local seats

A **seat** is a set of physical devices — a monitor, an audio output, and USB keyboards, mice and
gamepads — linked to a session. While the seat is on, the session is shown on that monitor and
controlled only by those devices, as if it were a separate computer. Seats are managed from the
**Seats** tab of the dashboard.

| | What happens |
|---|---|
| **Monitor** | Added to the desktop only while the seat is on (and placed so the console mouse can't wander into it). The session is captured inside its own RDP session and presented full-screen in ~2 ms, uncompressed, up to 125 Hz. The session is reconnected at the seat's resolution and refresh rate (fixed per seat or taken from the monitor) and restored when the seat turns off. |
| **Keyboard & mouse** | Switched to Microsoft's inbox **WinUSB** driver while the seat is on and read directly, so the console never sees them and every mouse moves independently. No third-party drivers. They return to their normal driver when the seat turns off, and on the next service start if it crashed. |
| **Gamepads** | Assigned by USB port and hidden from every other session with HidHide while the seat is on. |
| **Audio** | The session's audio plays on the seat's output with ~70 ms of latency and a jitter buffer. |
| **Identify** | In the seat editor, **Test** shows the OpenStreamMS logo on a monitor (turning it on for a few seconds if needed) or plays a chime on an audio output, and the keyboard, mouse or gamepad you press lights up in the device list. |

**Exclusive and shared seats.** An *exclusive* seat pauses Moonlight/WebRTC for its session while it
is on. A *shared* seat keeps streaming, so the same desktop can be used at the desk and on a TV.

**Rules that keep a shared PC safe:**

- An exclusive seat whose session uses a regular Windows account starts on the **lock screen**, so
  the person at the seat must enter that account's password. Isolated/ephemeral users, shared seats
  and the console's own account start unlocked.
- A seat using the **console's own account** never auto-starts at boot; it only turns on when started
  from the dashboard.
- **Only the console can shut down or restart the PC.** Users of exclusive seats lose the shutdown
  right and the power options; remote sessions and shared seats keep them.
- While any seat is on, the console **can't be locked** (<kbd>Win</kbd>+<kbd>L</kbd>), because that
  would black out every seat monitor.
- **Disconnect** inside a seat turns the seat off until it is started again from OpenStreamMS, and the
  session goes straight back to Moonlight.

**Requirements and limits.** Each seat monitor must be on the same GPU as the sessions. On Windows
Pro the monitor is added to and removed from the desktop by OpenStreamMS, so it shows the console's
desktop when no seat uses it. A seat's keyboard and mouse don't work on the console while the seat is
on, and keyboard LEDs (Caps Lock…) stay off. Input is injected, so some anti-cheat systems may reject
it. DRM-protected video is black, as in Sunshine. A host-only console (a technical user with
autologon) is recommended.

Design notes and measurements (Spanish): [docs/puestos-locales.md](docs/puestos-locales.md).

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

Internal (launched by the service in the right Windows session):
OpenStreamMS.exe --seat-agent <pipe> Seat capture/input agent inside the seat's session
OpenStreamMS.exe --seat-host <pipe>  Seat presenter on the console
OpenStreamMS.exe --display <cmd>     Monitor topology changes on the console
OpenStreamMS.exe --identify <cmd>    Test a monitor, an audio output or input devices
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
├── Seats/
│   ├── SeatManager.cs       — Seats: inventory, validation, start/stop orchestration
│   ├── SeatAgent.cs         — In-session capture (DDA), audio loopback and input injection
│   ├── SeatHost.cs          — Console-side presenter, audio output and input routing
│   ├── SeatUsbCapture.cs    — Switches seat keyboards/mice to WinUSB and reads them
│   ├── SeatHid.cs           — HID report descriptor parser → mouse events and scancodes
│   ├── SeatIdentify.cs      — Monitor logo, audio chime and pressed-device highlight
│   ├── DisplayTopology.cs   — Turns seat monitors on/off (CCD)
│   └── ConsoleLockGuard.cs  — Keeps the console from locking while seats are on
└── Api/
    ├── SeatEndpoints.cs         — REST endpoints (/api/seats/*)
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
| `GET` | `/api/seats` | List seats and their state |
| `GET` | `/api/seats/inventory` | Free monitors, audio outputs and USB input devices |
| `POST` | `/api/seats` | Create a seat |
| `PUT` | `/api/seats/{id}` | Edit a stopped seat |
| `PATCH` | `/api/seats/{id}/autostart` | Toggle seat auto-start |
| `DELETE` | `/api/seats/{id}` | Delete a seat |
| `POST` | `/api/seats/{id}/start` | Turn a seat on |
| `POST` | `/api/seats/{id}/stop` | Turn a seat off |
| `POST` | `/api/seats/identify/monitor` | Show the logo on a monitor (`{ "id": "<device path>" }`) |
| `POST` | `/api/seats/identify/audio` | Play a chime on an output (`{ "id": "<endpoint id>" }`) |
| `GET` | `/api/seats/identify/input` | USB ports of the devices just pressed |

Interactive docs: `http://localhost:5000/scalar/v1`

---

## Roadmap

Planned features, such as a per-session LAN IP for running LAN games in several sessions at once, are
tracked in [ROADMAP.md](ROADMAP.md).

---

## License

OpenStreamMS is released under the [MIT License](LICENSE).

The bundled Sunshine (GPL-3.0) and FreeRDP (Apache-2.0) binaries keep their own licenses — see [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
