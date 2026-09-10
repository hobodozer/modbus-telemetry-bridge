# Modbus Telemetry Bridge

A configurable, portable Windows bridge between Modbus TCP hardware and PC/game telemetry.

It is simultaneously a **Modbus TCP client** (polling your PLCs) and a **Modbus TCP server**
(serving your HMIs), with an in-memory tag database in the middle. Nothing is hard-wired: every
address, data type, scaling factor, poll rate and direction is configuration.

```
PLC #1 (Modbus server) ─┐                          ┌─► Modbus TCP SERVER  (N clients)
PLC #2 (Modbus server) ─┼─► Modbus CLIENT ──┐      │     ├─ HMI #1  (unit id 1, map A)
PLC #n ...             ─┘   (poll / write)  │      │     └─ HMI #2  (unit id 2, map B)
                                            ▼      │
SimHub telemetry ───────────────────────► TAG BUS ┼─► vJoy feeder (buttons/axes/hats)
Host statistics ─────────────────────────►        └─► GUI monitor, force/override, logging
```

Every source publishes named tags carrying a value, a quality and a timestamp. Every sink is just a
mapping table pointing at those tags. The same telemetry tag can drive a PLC analog output, three
HMIs and a vJoy axis at once, and an HMI's on-screen button becomes an input everywhere else.

---

## Status

| Phase | Scope | State |
| --- | --- | --- |
| 1 | Tag bus, Modbus client + multi-client server, config, GUI, simulator | **Complete** |
| 2a | vJoy feeder - buttons, axes, hats | **Complete**, verified against the real driver |
| 2b | SimHub plugin and telemetry ingest | **Complete**, verified running inside SimHub |
| 2c | Keyboard / macro output, vJoy shift layers | Not started |
| 3 | Host statistics (CPU/RAM/disk/network) | **Complete**; GPU not collected |
| 3 | Network scanner, derived-tag expressions, CSV record/replay | Not started |

Everything marked complete is covered by the automated suites described under **Testing**, and the
whole chain has been run against real hardware: a Siemens ET 200SP polled over Modbus TCP, a Weintek
HMI reading the server, a vJoy device fed from PLC contacts, and SimHub streaming Farming Simulator
25 telemetry - all at once.

**Verified on the wire, not just in tests:** a 10 ms poll interval measured with a packet capture
(~194 requests/second for two read groups), the HMI's requests answered with zero exception
responses, and register contents decoded back independently.

**Known gaps** are listed plainly in `HANDOFF.md` rather than glossed here. The notable ones: a few
status registers have no source and read zero, GPU utilisation is not collected, and the
vehicle-component array is mapped for 16 of the 100 slots the HMI reserves.

---

## Quick start

```powershell
.\build.ps1 -Publish
```

That builds, runs both test suites and drops a portable, self-contained `publish\ModbusBridge.exe`
(~68 MB, no .NET install needed on the target machine). Copy the folder anywhere.

On first run it writes `config\bridge.json` beside the executable and starts the engine. If the
install folder is read-only (e.g. Program Files) it falls back to `%LOCALAPPDATA%\ModbusBridge`.

### Try it with no hardware

1. Open the **Simulation** tab, tick **Enable simulation**, **Virtual PLC** and
   **Generate telemetry waveforms**.
2. On **Devices**, point the sample device at `127.0.0.1` port `15020` and tick **Enabled**.
3. **Apply and restart**.

The virtual PLC is a real Modbus TCP slave with animated inputs, so the whole read path, the tag
bus, the HMI-facing server and the latency readout all exercise for real.

### Command line

| Argument | Meaning |
| --- | --- |
| `--config <path>` | Use a specific config file. |
| `--data <dir>` | Put `config\` and `logs\` in this directory instead of beside the exe. |
| `--selftest` | Walk every UI tab, report WPF binding errors, exit non-zero on failure. |

---

## The latency readout

The dashboard shows, per device, in real time:

| Metric | What it means |
| --- | --- |
| **poll cycle** | Wall-clock gap between consecutive reads of a group. **This is your input lag** — how long a contact can sit unnoticed. Green when it tracks the configured interval, red when it drifts. |
| **round trip** | Time on the wire for the last request. |
| **poll rate** | Completed group reads per second. |
| avg / min / max / jitter | Round-trip statistics; **Reset** clears the high-water marks. |
| worst cycle | Longest cycle since reset — catches occasional stalls a rolling average hides. |

Expand **Per-group timing** for a per-read-group breakdown, so you can see which group is costing
the time. The **Tags** tab shows each tag's **Age** — milliseconds since it last changed — which is
the far end of the same chain.

### About 10 ms polling on Windows

Windows' default timer granularity is ~15.6 ms, so a naive 10 ms poll loop actually runs at 16 ms.
The bridge raises the multimedia timer resolution to 1 ms while running and uses the high-resolution
stopwatch counter for both scheduling and measurement, which holds a configured 10 ms interval to
within a few tenths of a millisecond. If you would rather not pay the small idle-power cost, set
`general.highResolutionTimer` to `false` — expect 16 ms cycles if you do.

---

## Addressing

Modbus wire addresses are always 0-based; vendor documentation frequently is not. `addressBase` is
subtracted from every address you type to get the wire address:

| Style | Setting | Effect |
| --- | --- | --- |
| Wire / 0-based | `addressBase: 0` | Coil `0` is the first coil. |
| 1-based | `addressBase: 1` | Coil `1` is the first coil. **Siemens `MB_SERVER` docs use this.** |
| Modicon `4xxxx` | `addressBaseByArea` preset | `00001` coils, `10001` discrete, `30001` input regs, `40001` holding regs. |

Use the **Presets…** button on the Devices tab to switch. Per-area bases exist because Modicon
notation gives each area a different offset.

**Word and byte order** are separate settings, per device and overridable per point:

- `wordOrder` — which register holds the high word of a 32/64-bit value. Siemens S7 is `HighFirst`.
- `byteOrder` — byte order inside each 16-bit register. `HighFirst` is the Modbus standard;
  `Swapped` covers devices that do it backwards.

If a float reads as garbage, it is almost always one of these two.

---

## Siemens ET 200SP notes

For an ET 200SP CPU (1510SP / 1512SP) running the `MB_SERVER` instruction:

- **Byte order**: S7 is big-endian throughout, so leave both `wordOrder` and `byteOrder` at
  `HighFirst`. A Siemens `REAL` maps directly onto `Float32`.
- **Address base**: Siemens documentation numbers holding registers from 1, so `addressBase: 1`
  matches what the manual shows. (The Modicon `4xxxx` prefix is a notation convention, not part of
  the wire address — do not type `40001` unless you also select the Modicon preset.)
- **Register area**: `MB_SERVER` maps FC3/FC6/FC16 (and FC4) to the DB you pass in `MB_HOLD_REG`.
  Bit functions (FC1/FC2/FC5/FC15) map to the CPU's bit memory area.
- **Analog scaling**: Siemens analog channels use ±27648 counts for nominal full scale. The sample
  config scales with `gain: 1/27648` on input and `27648/range` on output — adjust to your module's
  actual range.
- **One connection per `MB_SERVER` instance.** This is the big one: a single `MB_SERVER` call serves
  exactly one TCP connection. If you want the PLC to accept connections from both this bridge and
  something else, you need multiple `MB_SERVER` instances with distinct connection IDs in the PLC
  program. The bridge opens one connection per configured device.
- **Cycle time**: `MB_SERVER` only processes a request when it is called, so the PLC's own scan time
  sets a floor under the round trip. If the poll-cycle readout is fine but round trip is high, look
  at the PLC's OB cycle rather than the network.

> These notes started as Siemens documentation and have since been confirmed against a real
> ET 200SP (CPU 1512SP-1 PN, firmware V2.9) running `MB_SERVER` 5.3. The single-connection limit in
> particular is real: while the bridge is polling, anything else aimed at the PLC is refused
> outright. Confirm against the manual for your own firmware before committing a design.

**A detail worth knowing:** `MB_SERVER` maps the function codes to *different memory areas*, not
all to one data block. FC 3/6/16 reach the DB passed as `MB_HOLD_REG`, FC 1/5/15 reach the output
process image, and FC 2 and FC 4 both read the *input* process image - FC 4 as words. So a read of
holding register 0 and input register 0 legitimately return different values, and the analog module
appears in the discrete-input bit space too. Whatever byte the modules start at is what the Modbus
address maps to, so an unused first input byte means the inputs do not begin at address 0.

---

## Configuration reference

`config\bridge.json` is plain JSON and hot-reloads: save it in a text editor and the bridge picks it
up (invalid files are rejected with a log entry rather than applied). Every save from the GUI writes
a timestamped copy into `config\backups\`, keeping the 20 most recent.

### Points

A point binds a tag to an address. The fields that matter most:

| Field | Meaning |
| --- | --- |
| `tag` | Tag name in the bus. Created automatically. Device `tagPrefix` is prepended. |
| `offset` | Register or bit offset **relative to the group/block start**. |
| `dataType` | `Bool`, `Int16`, `UInt16`, `Int32`, `UInt32`, `Int64`, `UInt64`, `Float32`, `Float64`, `String`. |
| `bitIndex` | `0`–`15` for a Bool packed inside a register; `-1` otherwise. |
| `invert` | **Normally-closed toggle.** On a Bool, a closed contact reads as 0 and an open contact as 1. On a numeric, negates after scaling. Applies symmetrically on writes. |
| `access` | Server-side: `Read`, `Write`, or `ReadWrite`. A `ReadWrite` coil is how an HMI button becomes an input. |
| `scale` | `gain`, `offset`, `min`, `max`, `deadband`. Engineering value = `raw * gain + offset`, clamped. |
| `failsafeValue` | Served when the tag is bad/stale and the block uses `Failsafe`. |

Use **Auto-fill…** to generate a numbered run of points in one step — `{n}` in the tag pattern
becomes the index. It handles bit-packing (16 Bools per register) and multi-register strides for
you, and warns if the run would overflow the block.

### Read groups (PLC → PC)

A contiguous span polled on its own schedule. Requests are split automatically to respect the
device's per-request limits. A group that returns a Modbus exception logs once and backs off to 1 s
rather than hammering the device, and its tags go to `Bad` quality.

### Write groups (PC → PLC)

| `mode` | Behaviour |
| --- | --- |
| `OnChange` | Writes when a mapped tag moves beyond its deadband. `periodMs` is the minimum gap between writes. |
| `Periodic` | Writes every `periodMs` regardless. |
| `OnChangeAndPeriodic` | Both — on change, with a periodic refresh as a keepalive. |

Only the changed span is written unless `alwaysWriteWholeBlock` is set. The first write after
connecting always sends the whole block so the PLC starts from a known state. A tag last written by
the same device is never echoed back to it, which prevents write loops on shared registers.

### Server maps

One map per unit id. Two HMIs hitting the same listener with different unit ids see entirely
different register maps. `acceptAnyUnitId` makes a map the fallback for unmapped unit ids.

Each block chooses what happens when a tag goes bad or stale:

| `staleBehavior` | Result |
| --- | --- |
| `HoldLastValue` | Keep serving the last known value. |
| `Failsafe` | Serve each point's `failsafeValue`. |
| `ModbusException` | Return exception 4 for any read touching the block, so the HMI knows the data is dead rather than showing a frozen number. |

### Watchdog

Optional per device. The bridge increments a counter into the PLC every `intervalMs`. If the PLC
mirrors it back on `readAddress` and that echo stops changing for `timeoutMs`, the link is flagged
unhealthy and `healthTag` goes false — which you can map straight into an HMI status lamp.

### Security

Per listener: `bindAddress` (pin to one NIC), `allowedClients` (IP or CIDR allow-list, empty means
anyone), `readOnly` (reject every write function code), `maxClients`, and `idleTimeoutSec`.

---

## vJoy output

Any tag can drive a virtual joystick control — a PLC contact, an HMI soft button, or a telemetry
value. Counts are entirely configuration; nothing in the code assumes a number of buttons or axes.

Set it up on the **vJoy** tab: tick **Enable vJoy output**, add a device, then map controls.
**Auto-fill…** generates a whole numbered run at once (`plc1.di.btn{n}` → buttons 1..60).

### Buttons

| Mode | Behaviour |
| --- | --- |
| `Momentary` | Held for exactly as long as the tag is true. The default. |
| `Toggle` | Each rising edge flips the button and it stays flipped — for a physical latching switch that a game should see as a press. |
| `Pulse` | A rising edge emits a fixed-length press (`pulseMs`) however long the contact is held — for games that ignore very long presses. |

`threshold` lets an analog value drive a button (default 0.5). `NC` inverts the sense, the same
normally-open / normally-closed idea as on a Modbus point.

### Axes

`inputMin`/`inputMax` are the tag values that map to the ends of travel — **swap them to reverse an
axis**. Then `deadzone` (fraction of travel around centre, re-stretched so full deflection is still
reachable), `curve` (exponent; above 1 gives finer control near centre), `deadband` (ignore movement
smaller than this, to reject a noisy analog input) and `invert`.

### Hats

Two ways to drive one, because an arcade hat is not wired like a gamepad's:

| `source` | Meaning |
| --- | --- |
| `Angle` | One tag holds the hat angle in degrees; a negative value centres it. |
| `Contacts` | Four separate tags - up, right, down, left - as a real stick is actually wired. Opposing contacts cancel to centre, the way a physical gate makes them. |

`kind` must match the hardware. vJoy exposes **continuous** and **discrete** hats as separate pools,
and a device configured for one has none of the other, so a mismatch silently does nothing - the
dashboard warns when it happens. Continuous hats get eight-way resolution including diagonals;
discrete hats have only four positions, so diagonals round to the nearest of N/E/S/W.

### Shift layers

One physical button sending different vJoy buttons depending on a modifier, so a 60-button panel
can reach far more than 60 functions.

Define a layer with a `name` and a `modifierTag` - any tag, so the shift can be a PLC contact, an
HMI soft button, or even a telemetry condition. Then give a button mapping a `layer` matching that
name.

Mappings with no `layer` are the base layer, and they keep working under a shift **unless that
layer redefines the same tag**. So a panel is mapped once and a layer only lists what changes,
rather than being re-declared in full.

When several modifiers are held at once, the highest `priority` wins, so overlapping layers resolve
predictably instead of by declaration order. Switching layers while a button is still held releases
the outgoing button rather than leaving it stuck - the case worth testing, and the smoke test does.

### Per-game profiles

The same panel meaning different things depending on what is running, without remapping by hand.

```jsonc
"profileTag": "bridge.simhubGame",     // what profiles are matched against
"profiles": [
  { "name": "farm", "games": ["FarmingSimulator*"] },
  { "name": "race", "games": ["*Racing*", "iRacing"] }
]
```

Then give a mapping a `profile`. Mappings with no profile are always active, so the common parts of
a panel are declared once and a profile only lists what differs. The first profile whose pattern
matches wins, so order them most specific first.

`profileTag` is just a tag, so the selector does not have to be the game. Point it at a PLC input
and a physical rotary switch picks the profile instead.

Profiles apply to axes and hats as well as buttons. An axis outside the active profile is **centred
rather than frozen** - a throttle stuck at its last value is worse than one that returns to neutral.
Switching profile while a button is held releases the outgoing button rather than leaving it stuck,
the same as with shift layers, and the smoke test covers it.

Profiles and layers compose: a mapping can carry both, so a shift layer can be scoped to one game.

### Safety

`releaseOnBadQuality` (on by default) releases every control and centres every axis if all the
driving tags go bad — so a PLC dropping off cannot leave a throttle pinned or a button stuck down.
The feeder also clears everything when the engine stops or the app exits.

### Latency

`updateIntervalMs` (default 5 ms) adds to the PLC poll interval to give total contact-to-game
latency. A full report costs about **4 microseconds**, so this is cheap to lower — the smoke test
runs it at 2 ms. The dashboard shows the achieved feed interval and its worst case.

### If a mapping does not work

The dashboard shows warnings for mappings that point at controls the device does not have — for
example a button 60 mapping on a device configured for 8 buttons, or two tags fighting over one
control. **Button and axis counts are set in vJoyConf, not here**: if you need 60 buttons and 4
axes, configure the vJoy device for that and restart the bridge.

---

## Wiring a panel without decoding addresses

A 60-button panel is tedious to map by hand, and Modbus offers no discovery - a client is told the
address map by configuration and can never ask for one. So the bridge identifies inputs by watching
which tag moves.

**vJoy tab -> Learn...** Press a control on the panel. The dialog shows which tag just changed, you
give it a name, and choose what it drives: a vJoy button (momentary, toggle or pulse), one direction
of a hat, an axis, or nothing at all if you only want it named. Renaming carries every reference
with it - vJoy mappings, HMI server blocks, telemetry feedback - so a relearned button keeps its
number and nothing silently unhooks.

It watches only points the bridge actually polls. Without that filter streaming telemetry would win
every race, since SimHub moves hundreds of values a second. Analog inputs are excluded by default
for the same reason and have their own movement threshold when you enable them.

Addresses then appear in exactly one place - the read group - and never downstream. Name a contact
once and refer to it by name everywhere else.

## Testing outputs

**Devices tab -> Test outputs...** lists every point in every write group with On, Off and Pulse
buttons, plus a **Sweep** that walks them one at a time so you can watch or listen for which relay
is which.

It works by forcing the tag the write group already sends, rather than opening its own connection -
which matters, because a Siemens `MB_SERVER` instance serves exactly one TCP connection and the
engine owns it. Everything is released when the window closes; leaving an output forced with nothing
on screen explaining why would be a genuine hazard.

## Host statistics

Independent of any game, so an HMI shows something with nothing running. Enable `pcStats` and the
bridge publishes CPU load, memory used/total/percent, system-drive usage, network throughput,
process count, logical CPU count and its own uptime as ordinary tags - map them like any others.

Everything comes from the BCL or two kernel32 calls, so the published executable keeps its
no-third-party-dependency property. GPU load is the exception and is not collected: it needs
performance counters, which would mean a package.

## Keyboard output

For games that ignore joystick input for certain functions. Any tag can press a key.

```jsonc
"keyboard": {
  "enabled": true,
  "dryRun": true,                 // logs keystrokes instead of sending them - see below
  "mappings": [
    { "tag": "plc1.di.lights",  "keys": "l",            "mode": "hold" },
    { "tag": "plc1.di.hazards", "keys": "ctrl+shift+h", "mode": "tap"  },
    { "tag": "plc1.di.startup", "keys": "e, wait 500, y", "mode": "macro" }
  ]
}
```

| `mode` | Behaviour |
| --- | --- |
| `hold` | Key held for exactly as long as the tag is true. One key, not a sequence. |
| `tap` | Press and release on each rising edge. `repeatMs` repeats while held. |
| `macro` | Runs a sequence once per rising edge. `wait <ms>` pauses between steps. |

Keys are written the way you would say them: `f1`, `ctrl+shift+p`, `left`, `numpad5`, `escape`.
A mapping that will not parse is reported and skipped rather than stopping the others.

> **`dryRun` defaults to true, deliberately.** This types into whichever window has focus, which
> during setup is your configuration UI rather than the game. Leave it on until the mappings read
> correctly in the log, then turn it off with the game focused.

Keystrokes are sent as **scan codes**, not virtual keys, because many games read the keyboard
through DirectInput and ignore virtual-key-only injection entirely.

Anything still held is released when the engine stops, and a key held on a tag that goes bad is
released too - a stuck key outlives the process that pressed it.

## Recording and replay

Captures tags to CSV and plays them back, so an HMI screen, a vJoy mapping or a register map can be
exercised with no game and no PLC attached.

```jsonc
"recording": {
  "enabled": true,
  "directory": "recordings",     // relative to the data directory
  "tags": ["sim.*", "plc1.di.*"],
  "intervalMs": 100,
  "onChangeOnly": false,         // skip rows where nothing moved
  "maxRows": 0                   // 0 records until the engine stops
}
```

Columns are fixed when recording starts, so a tag created later is not added mid-file - a CSV whose
column count changes partway is painful for everything that reads it. The timestamp is elapsed
milliseconds rather than wall clock, so a capture made on one machine replays correctly on another.

```jsonc
"replay": {
  "enabled": true,
  "path": "recordings/session.csv",
  "speed": 1.0,                  // 2 is twice as fast, 0.5 half
  "loop": true,
  "tagPrefix": "back."           // replay into a separate namespace
}
```

Replay is a source like any other and publishes under its own writer id, so the tag monitor shows
where a value came from. Give it a `tagPrefix` to play a capture back alongside live data instead
of fighting it - useful for comparing a recorded session against what is happening now.

## Finding devices

Modbus has no discovery: a client is told its address map by configuration and can never ask for
one. The most that is possible is to find what listens and confirm it speaks the protocol, which
is what the scanner does - strictly read-only, it never issues a write function code.

```powershell
.\rig.ps1 scan                     # every local subnet
.\rig.ps1 scan 192.0.2.0/24        # a specific range
.\rig.ps1 scan 192.0.2.0/24 150    # with a shorter per-host timeout
```

A /24 takes under two seconds. For each responder it reports whether the reply was actually Modbus
(a protocol-identifier check, so a web server on 502 is not mistaken for one), which unit ids
answer, which of the four areas return data rather than an exception, and the device identification
string if FC 43/14 is implemented - many devices do not implement it, so its absence proves nothing.

Add `--units` to sweep unit ids 1-247 on each responder, which is much slower but finds gateways
presenting several devices behind one address.

> A Siemens `MB_SERVER` instance serves exactly one TCP connection. While the bridge is polling a
> PLC, that PLC will not answer a scan at all - stop the bridge first, or it looks absent.

## Command-line tools

```powershell
.
ig.ps1 status                  # what is running, plus the last few log lines
.
ig.ps1 build                   # stop the app, build, restart it
.
ig.ps1 test                    # full build + smoke test + UI self-test
.
ig.ps1 read 200 67             # read holding registers, non-zero only
.
ig.ps1 read 144 16 string      # decode a text field
.
ig.ps1 capture 8               # what a connected HMI actually polls, via tshark
```

`tools/modbus-read.ps1` is the underlying client and takes `-Target`, `-Area`, `-Type` and
`-WriteValue` for one-off pokes at any device. `tools/make-hmi-map.py` generates a server map and
its matching SimHub subscriptions from a spec, which is how the 2000-register HMI map is
maintained. `tools/simhub-catalog` dumps SimHub's live property list so subscriptions can be
written from fact rather than guesswork, and `tools/tia/` reads a Siemens TIA Portal project
through the Openness API to get hardware and addressing straight from the engineering data.

## SimHub telemetry

Game telemetry reaches the bridge through a small SimHub plugin that talks to it over loopback UDP.

The plugin holds **no property list of its own**. It advertises everything SimHub currently offers,
and the bridge replies with the subset it wants streamed. So the property browser lives in the
bridge's UI, the list always reflects the game that is actually loaded, and changing what you stream
never means touching SimHub.

### Installing the plugin

```powershell
.\install-simhub-plugin.ps1
```

Close SimHub first — the DLL cannot be replaced while it is loaded. SimHub normally lives under
Program Files, so this usually needs an elevated prompt; the script says so plainly rather than
half-installing. It also writes the plugin's settings to `%LOCALAPPDATA%\ModbusBridge\`, which never
needs elevation.

On the next start SimHub asks you to authorise the new plugin — say yes, enable **Modbus Telemetry
Bridge** in its plugin list, and restart SimHub once more.

### Using it

1. On the bridge's **SimHub** tab, tick **Enable SimHub telemetry** and check the listen port
   matches the plugin's (15600 by default). **Apply and restart**.
2. Click **Browse SimHub properties…**. Search, multi-select, **Add selected**.
3. **Apply and restart** to begin streaming.

Leave a subscription's `Tag` empty and it is derived from the property name under the configured
prefix (`SpeedKmh` → `sim.SpeedKmh`). Subscribe to the same property twice with different scaling to
get it in two units at once — for instance `sim.SpeedKmh` raw and `sim.speedMph` with `gain 0.621371`
— which saves doing unit maths in the PLC or the HMI.

### Feedback: PLC contacts inside SimHub

The **Feedback** tab sends bridge tags the other way. Each one appears in SimHub as a property
`Bridge.<name>`, usable in dashboards and formulas, and raises a SimHub **event** on every rising
edge — and a SimHub event can be bound to any SimHub action. That is how a PLC contact becomes an
input SimHub itself can act on.

### When the game closes

If no frame arrives within `timeoutMs`, telemetry tags are marked bad and the `connectedTag` goes
false. Combined with a block's `Failsafe` stale behaviour, an HMI shows a defined value instead of
the last number from a session that ended ten minutes ago.

### Latency

The dashboard shows frame rate, arrival interval, worst interval, and a genuine **one-way** latency
— the plugin stamps each frame with the system clock and both processes read the same clock, so it
is not a round trip. SimHub's own data rate is the ceiling; `minIntervalMs` in the plugin settings
is only a floor.

---

## Project layout

```
src/ModbusBridge.Core/        Engine, protocol, config - no UI dependencies
  Config/                     Configuration model, validation, load/save/hot-reload
  Data/                       Enums, scaling, register <-> value codec
  Tags/                       Tag bus
  Modbus/                     Modbus TCP client and multi-client server (no third-party stack)
  Engine/                     Device runner (poll/write/watchdog), server data store, BridgeEngine
  Simulation/                 Virtual PLC and waveform generator
  Diagnostics/                Logging, high-resolution clock
  Outputs/VJoy/               vJoy interop, device wrapper, feeder
  Inputs/                     SimHub telemetry ingest
src/ModbusBridge.App/         WPF desktop app + tray icon
plugin/                       SimHub plugin (net48, outside the solution)
shared/TelemetryProtocol.cs   Wire format, compiled into BOTH the bridge and the plugin
tests/ModbusBridge.SmokeTest/ End-to-end test with no PLC hardware
tools/modbus-read.ps1         Read or write any Modbus TCP device from the command line
tools/make-hmi-map.py         Generate a server register map and its SimHub subscriptions
tools/simhub-catalog/         Dump the SimHub property catalogue to text
tools/tia/                    Read a Siemens TIA Portal project through the Openness API
build.ps1                     Build, test, publish
rig.ps1                       Task runner: status, build, test, read, capture, log
install-simhub-plugin.ps1     Build and install the SimHub plugin
```

The SimHub plugin is deliberately **outside** the solution: it targets .NET Framework 4.8 and
references assemblies out of a SimHub install, so it must not break the main build on a machine
without SimHub. `build.ps1` builds it only when it finds SimHub.

The Modbus protocol is implemented directly rather than via a library, so the published exe carries
no third-party dependencies and the framing, error handling and per-request limits are all
inspectable in one place.

Supported function codes: 1, 2, 3, 4, 5, 6, 15, 16, 22 (mask write), 23 (read/write multiple) and
43/14 (device identification), both as client and as server.

---

## Testing

```powershell
dotnet run --project tests\ModbusBridge.SmokeTest    # engine end-to-end, no hardware
.\build.ps1                                          # both suites
```

The smoke test stands up a virtual PLC, a device runner, the HMI-facing server and real Modbus
clients, then checks the whole chain: polling, NO/NC inversion, scaling, serving, HMI writes landing
back on tags, multiple concurrent clients, exception responses, codec round-trips across every
word/byte-order combination, and that a 10 ms poll interval is actually held.

> The poll-interval check is the one assertion not to trust blindly. Timer resolution has been
> per-process since Windows 10 2004, and the background test process does not get what the windowed
> app gets, so it can report ~15.6 ms cycles while the running bridge holds 10.3 ms. Measure timing
> with a packet capture before concluding anything has regressed.

**The vJoy checks drive the real driver and read the result back through a completely separate code
path** — Windows' own `winmm` joystick API. That matters: if the `JOYSTICK_POSITION_V2` struct
layout were wrong, writing would still "succeed" and only an independent readback would notice. The
device under test is identified by pressing a button and seeing which joystick moves, because
winmm's product string is a generic "Microsoft PC-joystick driver" rather than anything naming vJoy.

Covered: button bitfield layout including the >32-button bank boundary, no cross-talk between
buttons, axis scaling at 0/25/75/100% of travel, momentary vs toggle vs pulse timing, NO/NC
inversion, the release-on-bad-quality failsafe, and `UpdateVJD` throughput. If vJoy is not installed
or every device is busy, these checks skip with a note rather than failing.

The UI self-test (`--selftest`) walks every tab and fails the build on any WPF binding error.

> **The smoke test needs the hardware to itself.** It acquires vJoy device 1 and binds loopback
> ports, so it fails while a bridge is already running - which looks like a broken test rather
> than a busy device. Stop the bridge first.

To check that what is *committed* builds, rather than what happens to be on disk:

```powershell
.\rig.ps1 verify-clone
```

That clones from the remote into a temporary directory and builds it. An unanchored `.gitignore`
rule once kept an entire source directory out of the repository while every local build still
passed, so it is worth running before trusting a push.

---

## Not yet built

- **GPU utilisation** - the host statistics collector uses only the BCL and two kernel32 calls to
  keep the published executable dependency-free, and GPU load needs performance counters.
- Run-as-service.
