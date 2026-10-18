# Roadmap

Planned work for OpenStreamMS. Items move to the README once they ship.

## Planned

Grouped by priority, highest first.

### 1. Polish local seats

- **Hardware check of untested seat features.** Seat start on the lock screen, audio capture
  when the seat agent runs as SYSTEM, the console lock guard (<kbd>Win</kbd>+<kbd>L</kbd>), removing
  the shutdown right and disconnecting from inside a seat are implemented but not yet verified on
  real hardware. Add a checklist, or a self-test in the dashboard.
- **Ctrl+Alt+Del on password seats.** If Windows asks for Ctrl+Alt+Del before the password, the seat
  can't get past the lock screen, because that combination can't be injected as keys. The service can
  raise it with `SendSAS`.
- **Keyboard LEDs on seats.** Caps Lock, Num Lock and Scroll Lock LEDs stay off on keyboards captured
  with WinUSB. Send the keyboard its LED output report over the same WinUSB connection.
- **Hot-plug into a running seat.** A device plugged into one of a running seat's USB ports should be
  captured and routed to the seat automatically, without turning the seat off and on.

### 2. More useful seats

- **Assign a whole USB hub to a seat.** Everything plugged into that hub goes to the seat, with no
  per-device setup.
- **Bluetooth and legacy XInput gamepads.** Bluetooth gamepads have no USB port, so they can't be
  assigned today; assign them by Bluetooth address instead. Identify XInput pads that expose no HID
  interface (e.g. wired Xbox 360) in the seat editor.
- **Schedules and time limits.** Per seat or session: turn off at a set time (e.g. a kids' seat at
  21:00), daily time allowance, and turn off after a period without input.

### 3. Maintenance and trust

- **Diagnostics page.** Hardware errors from the Windows event log (WHEA machine checks, unexpected
  restarts), devices left on the WinUSB driver, and per-seat stats (audio underruns, dropped frames).
- **Update from the dashboard.** Notice when a newer GitHub release exists and install it in one click.
- **Automatic release notes.** Have the release workflow build the notes from the commits since the
  previous tag; GitHub's generated notes are empty when changes don't come from pull requests.
- **Automated tests.** Unit tests for the HID report descriptor parser and the seat rules (conflicts,
  password, auto-start, shutdown right), run in CI.

### 4. Website

- **Screenshots and a short clip of local seats in use** on the project site and the README.

### 5. Per-session LAN IP

**Goal:** optionally give a session its own IP on the local network (e.g. `192.168.1.201`) so that
every program in that session uses it. Classic LAN games (fixed ports, broadcast discovery) can then
run twice on the same PC — one copy per session or seat — and see each other and other PCs on the
network as separate hosts.

**Why it needs more than an extra IP:** Windows has a single network stack for the whole machine.
Programs usually bind to "any address" (`0.0.0.0`) and Windows picks the source IP from the routing
table, never by session. Two copies of a game then collide on the same port and announce the same IP.

**Design (research done, not started):**

1. **The address.** Two ways, chosen per installation:
   - *Secondary IP on the existing network adapter* (default): added with `SkipAsSource` so the rest
     of the PC keeps using its main IP. Windows only, applied instantly. Requires the adapter to have
     a static IP, and the session IPs must be outside the router's DHCP pool.
   - *Bridged virtual adapter*: a Hyper-V external switch with one host virtual NIC per session, each
     with its own MAC and a DHCP lease from the router. Heavier: it enables Hyper-V and changes the
     networking of the whole PC.
2. **Making programs use it.** A user-mode DLL (same mechanism as the Steam isolation hook; no
   drivers) is loaded into the session's Explorer and passed on to every child process:
   - `bind`, `connect`, `sendto`, `WSAConnect` and `ConnectEx` bind wildcard sockets to the session IP.
   - `gethostname`, `getaddrinfo` and `GetAdaptersAddresses` report only the session IP, so games
     advertise the right address.
   - Sunshine and OpenStreamMS processes are excluded.
3. **Dashboard.** A per-session setting "Network: own IP [x.x.x.x] / PC's IP" and a per-session list of
   excluded games. The service adds and removes the IP when the session starts and stops.

**Verified so far:** a UDP socket bound to a specific IP still receives broadcasts sent to
`255.255.255.255` and to the subnet broadcast from that adapter, including ones sent from the same PC,
so LAN game discovery keeps working.

**Next steps:**

- Prototype outside OpenStreamMS: add a temporary secondary IP, run two copies of a LAN game each bound
  to its own IP, and check they find each other. Confirm that a broadcast sent from one secondary IP
  reaches a socket bound to another IP on the same adapter.
- Integrate the setting, the IP lifecycle and the hook into sessions and seats.

**Known limits:**

- Games with kernel anti-cheat (Easy Anti-Cheat, BattlEye, Vanguard) may refuse to start or flag an
  injected DLL; they go on the exclusion list and use the PC's IP.
- Programs not started from the session's desktop (elevated through UAC, scheduled tasks, COM
  servers) keep the PC's IP.
- IPv6 is not covered for the redirected sockets; both 32- and 64-bit builds of the DLL are needed.
- Most modern online games and Steam networking don't need this; it targets LAN games.

**Ruled out:** WFP redirection (needs a kernel callout driver), Winsock LSPs (removed from current
Windows) and network compartments (undocumented, container-only).
