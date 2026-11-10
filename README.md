<div align="center">

<img width="1100" alt="OpenStreamMS" src="https://github.com/user-attachments/assets/41a93d90-00d3-4eb4-ba67-767be534ae00" />

### One Windows PC. Many players. Each with their own desktop.

<sub><b>OpenStreamMS</b> = OpenStream <b>M</b>ulti-<b>S</b>eat</sub>

Isolated streaming sessions for **Moonlight** and **WebRTC TVs**, and **local seats** that turn one PC into several —
managed from a web dashboard, a tray icon or a REST API.

[![Latest release](https://img.shields.io/github/v/release/mlopezsegura/OpenStreamMS?style=flat-square&color=7c5cff)](https://github.com/mlopezsegura/OpenStreamMS/releases/latest)
[![Downloads](https://img.shields.io/github/downloads/mlopezsegura/OpenStreamMS/total?style=flat-square&color=2dd4bf)](https://github.com/mlopezsegura/OpenStreamMS/releases)
[![Windows 10/11](https://img.shields.io/badge/Windows-10%20%7C%2011-0078d4?style=flat-square&logo=windows)](#installation)
[![.NET 10](https://img.shields.io/badge/.NET-10-512bd4?style=flat-square&logo=dotnet)](https://dotnet.microsoft.com/download)
[![License: MIT](https://img.shields.io/badge/license-MIT-green?style=flat-square)](LICENSE)

**[⬇ Download](https://github.com/mlopezsegura/OpenStreamMS/releases/latest)** ·
[Quick start](#-quick-start) ·
[Features](#-features) ·
[Local seats](#local-seats) ·
[Docs](#-documentation)

</div>

---

## ✨ Why OpenStreamMS

<table>
<tr>
<td width="33%" valign="top">

### 🎮 Streaming sessions
Every session is its own Windows desktop (RDP) with its own Sunshine. Stream games to several people at once while you keep using the PC.

</td>
<td width="33%" valign="top">

### 🖥️ Local seats
Plug in another monitor, keyboard, mouse, gamepad and headphones: a session shows up right there, as if it were a second computer.

</td>
<td width="33%" valign="top">

### 📺 Moonlight + WebRTC
Each session serves Moonlight apps, Samsung TVs running Moonlight WebRTC, or both. Ports and firewall are handled for you.

</td>
</tr>
</table>

<table>
<tr>
<td><img alt="Sessions" src="https://github.com/user-attachments/assets/6c246ab5-262f-4558-8bd4-87cfb124b335" /></td>
<td><img alt="Local seats" src="https://github.com/user-attachments/assets/054e9138-a14f-4003-b719-3bd862ebf6fa" /></td>
<td><img alt="Seat editor" src="https://github.com/user-attachments/assets/c2000a0b-fc0c-442a-8b14-c922313270d4" /></td>
</tr>
<tr align="center">
<td><sub>Sessions</sub></td>
<td><sub>Local seats</sub></td>
<td><sub>Seat editor</sub></td>
</tr>
</table>

---

## 🚀 Quick start

1. **Download** `OpenStreamMS-Setup-x.x.x.exe` from [Releases](https://github.com/mlopezsegura/OpenStreamMS/releases/latest).
2. **Install** it as Administrator. It sets up the service, the tray icon and everything it needs (TermWrap, ViGEmBus, HidHide).
3. **Open OpenStreamMS** from the Start menu, create your admin account and follow the built-in guide to your first session.

Then add the PC in Moonlight (or on your TV) — **Connect** in the dashboard shows the name, addresses and ports to use.

---

## 🧩 Features

**Sessions**
- 🔒 **Isolated RDP sessions** — each one under its own Windows account and Sunshine instance, separate from your desktop
- 👤 **Isolated users** — auto-managed `osms-…` users, optionally ephemeral (wiped on stop); kept users preserve Steam logins, saves and settings
- 🖥️ **Local desktop session** — stream the PC's own desktop too (no RDP); stopping it never signs anyone out
- 🧪 **Independent stream profile** — per-session `USERPROFILE`/`APPDATA`/temp sandbox so local and stream apps don't share lock files
- ♨️ **Steam next to your own** — Steam on the console and in a session at the same time, even with the same Windows user

**Streaming**
- 📺 **Moonlight, WebRTC or both** per session, with automatic free ports and collision checks
- ⚡ **Low latency to TVs** — zero playout delay: 1080p60 from 54 → 38 ms screen to screen
- 🎞️ **A true 60 fps** in RDP sessions (instead of 58.8 Hz)
- 🧬 **RemoteIdd display** *(experimental)* — our own IddCx driver hands frames to Sunshine as a GPU texture, no screen capture

**Local seats**
- 🖱️ Monitor, keyboard, mouse, gamepads, audio and microphone per seat — drag and drop in the dashboard
- ➗ **Split screen** — up to four seats on one monitor (halves or quarters)
- 🛡️ Safe by default: lock screen for regular accounts, only the console can shut down the PC

**Management**
- 🌐 **Web dashboard** (any browser, phones included) + **REST API** with OpenAPI docs at `/scalar/v1`
- 🔔 **Tray icon** with per-session controls and pairing notifications for Moonlight and TVs
- ♻️ **Auto-start and self-healing** — sessions come back after a restart; Sunshine is relaunched if it crashes
- 🎨 Themes (Dark, Light, Synthwave, Retro, Matrix…) and **22 languages**
- 🧹 **Clean shutdown** — seats sign out with a real logoff, so Windows always shuts down

---

## 📊 How it compares

<img width="1600" alt="OpenStreamMS comparison" src="https://github.com/user-attachments/assets/6bec8234-b742-4b35-b4a9-1769a642041d" />

---

## 📚 Documentation

<details id="installation">
<summary><b>Installation</b> — installer, uninstall, build from source</summary>

### Option A — Installer (recommended)

1. Download `OpenStreamMS-Setup-x.x.x.exe` from [Releases](https://github.com/mlopezsegura/OpenStreamMS/releases)
2. Run it as Administrator and follow the wizard
3. The installer registers the Windows service, adds the tray icon to Windows startup, and optionally starts everything immediately

To uninstall, use **Apps & Features** → *OpenStreamMS* → Uninstall. It asks two questions:

- **Delete all data?** Yes removes sessions, seats and settings, the isolated `osms-…` users with their profiles (including games and settings stored in them), stream profiles, `%ProgramData%\OpenStreamMS` and temporary files. No keeps sessions and settings for a reinstall.
- **Also uninstall TermWrap, ViGEmBus and HidHide?** Answer No if other applications use them.

Silent uninstall: `unins000.exe /VERYSILENT [/PURGEDATA] [/REMOVEDEPS]`.

### Option B — Manual (from source)

**Requirements**

- Windows 10/11 (x64)
- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- Remote Desktop enabled (*System Properties → Remote → Allow remote connections*)
- *(Optional)* [SudoVDA](https://github.com/SudoMaker/SudoVDA) for virtual display support

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

### Building the installer

Requires [Inno Setup 6](https://jrsoftware.org/isinfo.php).

```bat
cd Core\Installer
build-installer.bat
```

The resulting `OpenStreamMS-Setup-x.x.x.exe` is placed in `Core\dist\`.

</details>

<details>
<summary><b>Configuration</b> — <code>service.config.json</code></summary>

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
  "LogLevel":        "Error",
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
| `LogLevel` | `Error` | Minimum log level for the service, tray icon, seats and Sunshine: `Info`, `Warning`, `Error` or `Off`. Also in the dashboard under **More → Settings** |

</details>

<details>
<summary><b>Web dashboard</b> — what you can do from it</summary>

Open **OpenStreamMS** from the Start menu or the desktop: the dashboard opens in its own window
(WebView2; without it, in the default browser). It is also at `http://localhost:5000` in any
browser, phones included.

On first access you will be prompted to create admin credentials, and a getting-started guide
shows what is missing (components, first session, how to connect). After that, the dashboard lets you:

- Create, edit, start, stop, and delete streaming sessions from one window (rarely used options under *Advanced*)
- Follow each session's start step by step; errors come with **View log** and **Retry**
- **Connect** shows the PC's name, LAN addresses and ports to add it in Moonlight or on a TV
- See how many Moonlight clients and WebRTC TVs are connected to each session
- Enable auto-start and the **independent stream profile** per session
- Choose **Moonlight**, **WebRTC** or **both** in each session's settings
- View real-time per-session logs and set the global log level in **Settings**
- Pick a theme (System, Dark, Light, Synthwave, Retro, Matrix) and the visual effects: *Automatic* reduces them while someone is streaming or a seat is on, so the GPU stays with the encoder
- Create **local seats** in the *Seats* tab, test their monitor, audio output and microphone, and see which session each seat uses
- Access the interactive API docs at `/scalar/v1`

</details>

<details>
<summary><b>Streaming protocols and ports</b> — Moonlight, WebRTC, firewall</summary>

Sessions run the Sunshine in `Sunshine\` (or the one set in `SunshineExePath`). With the
[sunshine-webrtc](https://github.com/mlopezsegura/Sunshine-Web-RTC) build, a session can serve:

| Protocol | Clients | Ports opened in the firewall |
|---|---|---|
| **Moonlight** | Moonlight apps | TCP+UDP `port-5 .. port+21` (HTTPS, HTTP, video/control/audio, RTSP) |
| **WebRTC** | Samsung TVs with Moonlight WebRTC | TCP `WebRtcPort`, UDP 8000 (TV discovery) and UDP `WebRtcMediaPortMin..Max` |
| **Both** (default for new sessions) | Both | All of the above |

`PUT /api/sessions/{id}/protocol` changes it live: OpenStreamMS
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

The bundled build streams to TVs with zero playout delay: each frame is shown the moment it is
decoded, with no jitter buffer, and the picture no longer waits for the audio. Screen to screen,
1080p60 went from 54 to 38 ms and 2160p60 from 73 to about 50 ms in Sunshine-Web-RTC's latency test.

**RemoteIdd display (experimental).** An IddCx display driver of our own for RDP sessions, built on
documented Windows APIs only: Sunshine (`capture = remoteidd`) takes each frame from a shared GPU
texture instead of capturing the screen. Optional in the installer (test certificate, takes effect
after a restart); used automatically when installed. See [Native/RemoteIdd](Native/RemoteIdd/README.md).

</details>

<details id="local-seats">
<summary><b>Local seats</b> — devices, split screen, safety rules, limits</summary>

A **seat** is a set of physical devices — a monitor, an audio output, and USB keyboards, mice and
gamepads — linked to a session. While the seat is on, the session is shown on that monitor and
controlled only by those devices, as if it were a separate computer. Seats are managed from the
**Seats** tab of the dashboard, which draws every monitor with its seats and lets you assign keyboards, mice,
gamepads and audio outputs by dragging them onto a seat.

| | What happens |
|---|---|
| **Monitor** | Added to the desktop only while the seat is on (and placed so the console mouse can't wander into it). The session is captured inside its own RDP session and presented full-screen in ~2 ms, uncompressed, up to 125 Hz. The session is reconnected at the seat's resolution and refresh rate (fixed per seat or taken from the monitor) and restored when the seat turns off. If the monitor changes mode or size, the seat is readjusted to it. |
| **Keyboard & mouse** | Switched to Microsoft's inbox **WinUSB** driver while the seat is on and read directly, so the console never sees them and every mouse moves independently. No third-party drivers. They return to their normal driver when the seat turns off, and on the next service start if it crashed. |
| **Gamepads** | Assigned by USB port and hidden from every other session with HidHide while the seat is on. |
| **Audio** | The session's audio plays on the seat's output with ~70 ms of latency and a jitter buffer. |
| **Microphone** | Optional. The session reconnects with RDP microphone redirection to that device, which shows up inside the session as *Remote Audio*. A microphone belongs to one seat only. The bundled FreeRDP carries a patch so each seat opens its own microphone (`FreeRDP/patches`). |
| **Identify** | In the seat editor, **Test** shows the OpenStreamMS logo on a monitor (turning it on for a few seconds if needed), plays a chime on an audio output or shows a microphone's level, and the keyboard, mouse or gamepad you press lights up in the device list. |

**Exclusive and shared seats.** An *exclusive* seat pauses Moonlight/WebRTC for its session while it
is on. A *shared* seat keeps streaming, so the same desktop can be used at the desk and on a TV.

**Split screen.** Up to four seats can share one monitor, which works as a 2×2 grid: each seat takes the whole screen, a half (vertical: left/right; horizontal: top/bottom) or a quarter, as long as their parts don't overlap. The session is set to the size of its part (e.g. 960×1080 for a vertical half of a 1920×1080 monitor, 960×540 for a quarter), and the monitor stays on the desktop while any part is in use. Each half has its own keyboard, mouse, gamepads and audio output, like any seat. Two windows share the monitor, so Windows composes them instead of flipping each one directly: expect about one extra frame of latency compared with a full-screen seat. The **main monitor** can be split too: a seat takes one half and the console keeps the other with a real desktop of that size (taskbar, Start and notifications included). With [SudoVDA](https://github.com/SudoMaker/SudoVDA) installed, the console moves to a virtual display of that half that is shown on the monitor like a seat; without it, only maximized windows respect the half. The console takes the largest free part; if what's left is L-shaped, the rest is blacked out. When seats fill the whole monitor, the console stays behind as host only. A seat can never take the whole main monitor.

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

</details>

<details>
<summary><b>Isolated users and Steam</b></summary>

- A session can run under its own auto-managed local user (`osms-…`, shown on the session card), optionally ephemeral (wiped on stop). Isolated sessions get a *Steam (isolated)* shortcut and handle `steam://` links, so each one can sign in to its own Steam account.
- Deleting an isolated session asks whether to keep its Windows user, which holds Steam's saved login, saves and game settings; a new session can reuse a kept user. Cards warn when Steam has no saved login, and **System → Dedicated users without a session** lists kept users and leftover `osms-*` profiles with their size, to delete them one by one.
- Steam can run on the console and in a stream session or seat at the same time, even with the same Windows user (per-session hook and browser cache).

</details>

<details>
<summary><b>Independent stream profile</b></summary>

When **independent stream profile** is enabled for a session, OpenStreamMS launches Explorer, Sunshine, and Sunshine apps with a separate profile root:

```
C:\Users\<user>                    ← your normal local profile
C:\Users\<user>\_OpenStreamMS_Stream ← stream session profile sandbox
```

The sandbox sets `USERPROFILE`, `HOME`, `APPDATA`, `LOCALAPPDATA`, `LOCALAPPDATALOW`, `TEMP`, `TMP`, and XDG paths for the stream process tree. Sunshine also receives the same values in `apps.json`, so commands launched from Moonlight inherit the isolated profile even after Sunshine restarts.

This is native Windows process/profile isolation, not Windows Sandbox. Windows Sandbox is a separate VM and cannot be attached transparently to the existing RDP/Sunshine session without a different streaming architecture.

Applications that use machine-global mutexes, HKCU registry keys, or their own installation directory for locks may still require a separate Windows user or a separate app installation.

</details>

<details>
<summary><b>System tray</b></summary>

The tray icon (`OpenStreamMS.exe --tray`) runs in the user's desktop session and provides quick access without opening a browser:

- **Double-click** → opens the dashboard
- **Right-click** → sessions and local seats with a coloured state dot and an on/off action each, and *Open web interface*
- **Notifications** → a Windows notification when Moonlight or a WebRTC TV waits for a PIN; clicking it opens that session's pairing dialog

The menu follows the Windows language (all 22 supported languages: English, Spanish, French, Portuguese, German, Russian, Ukrainian, Bulgarian, Czech, Hungarian, Italian, Japanese, Korean, Polish, Swedish, Turkish, Vietnamese, Chinese Simplified, Chinese Traditional, plus regional variants). The tray icon is registered at Windows startup automatically during installation.

</details>

<details>
<summary><b>CLI reference</b></summary>

```
OpenStreamMS.exe --run               Run the service (called by Windows SCM)
OpenStreamMS.exe --install           Install and register the Windows service
OpenStreamMS.exe --install --silent  Install without prompts (used by installer)
OpenStreamMS.exe --uninstall         Stop and remove the Windows service
OpenStreamMS.exe --tray              Show system tray icon
OpenStreamMS.exe --open              Open the dashboard in its own window (Start menu shortcut)

Internal (launched by the service in the right Windows session):
OpenStreamMS.exe --seat-agent <pipe> Seat capture/input agent inside the seat's session
OpenStreamMS.exe --seat-host <pipe>  Seat presenter on the console
OpenStreamMS.exe --display <cmd>     Monitor topology changes on the console
OpenStreamMS.exe --identify <cmd>    Test a monitor, an audio output or input devices
OpenStreamMS.exe --help              Show help
```

</details>

<details>
<summary><b>Architecture</b></summary>

```
OpenStreamMS.exe  (Windows Service, SYSTEM)
├── Program.cs              — CLI entry point (--run / --install / --uninstall / --tray)
├── OpenstreamService.cs    — BackgroundService; owns the 5-second monitoring loop
├── ServiceConfig.cs        — Loads/saves service.config.json
├── RdpSessionCreator.cs    — Creates the loopback RDP session via WTS API + FreeRDP
├── SunshineManager.cs      — Launches and monitors Sunshine.exe inside the RDP session
├── SunshineConfigurator.cs — Writes each instance's sunshine.conf and apps.json
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

</details>

---

## 📄 License

OpenStreamMS is released under the [MIT License](LICENSE).
The bundled Sunshine (GPL-3.0) and FreeRDP (Apache-2.0) binaries keep their own licenses — see [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).

"MS" stands for *Multi-Session*. OpenStreamMS is an independent project, not affiliated with or endorsed by Microsoft. Windows is a trademark of Microsoft Corporation.
