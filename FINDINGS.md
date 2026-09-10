# Findings — read this before doing anything

Written for whoever picks this up next. Everything here was established by actually running things
on this machine, not inferred. It exists so you do not spend tokens rediscovering it.

Companion files: `HANDOFF.md` (what is done vs. not), `README.md` (how the thing works for a user).

---

## 1. Environment facts

| Fact | Value |
| --- | --- |
| Project root | `<repo>` (moved from C: - no hardcoded paths, it relocated cleanly) |
| Machine | the dev machine. This is now BOTH the dev machine and the target rig |
| .NET SDK | 8.0.425. `net8.0-windows`, WPF + WinForms both enabled in the app project |
| **git** | **INSTALLED** (2.55.0). Earlier notes saying otherwise are stale |
| Python | 3.14.6 present, used for the config generators in `tools/` |
| Shell | Windows PowerShell **5.1**, not PowerShell 7 |
| OS | Windows 11 Pro 26200 |
| vJoy | 2.2.2.0. Device 1 is configured for **60 buttons / 8 axes / 1 DISCRETE POV** |
| SimHub | installed; `SimHub.Plugins.dll` 1.0.9735.26972 (older than the 9748 earlier notes mention) |
| TIA Portal | V18 + Openness V18, STEP 7 Professional licensed. See `tools/tia/README.md` |
| PLC | ET 200SP, CPU 1512SP-1 PN, **192.0.2.10:502**, live |
| HMI | Weintek/Maple, **192.0.2.61**, polls the bridge's server continuously |

### PowerShell 5.1 traps that cost time

- No `&&` / `||` / ternary / `??`. Use `;` and `if ($?) { }`.
- `[UIntPtr]$someInt` **throws** ("Cannot convert ... to type System.UIntPtr"). Cast via
  `[UIntPtr]::new([uint64]$i)` or just declare the P/Invoke parameter as `uint`.
- `Get-Content -Raw` / `Set-Content` **guess the encoding and will corrupt non-ASCII**. A regex
  sweep over source files mangled `→` into `â†'` in one file. If you must rewrite files in bulk, use
  `[System.IO.File]::ReadAllText($p, $utf8)` / `WriteAllText` with an explicit
  `New-Object System.Text.UTF8Encoding($false)`.
- Log files are written UTF-8 by the app but `Get-Content` reads them as ANSI, so non-ASCII looks
  broken in the console. Source and log strings are deliberately **plain ASCII** now — keep them
  that way (no `…`, `—`, `→`, `±`).

---

## 2. vJoy — verified facts

Installed: **vJoy 2.2.2.0 x64**. `vJoyInterface.dll` at `C:\Program Files\vJoy\x64\`.
Driver and HID device both report OK. API version reports as `0x222`, DLL and driver versions match.

**Device 1 is currently configured for 8 buttons, 8 axes (X Y Z RX RY RZ Slider0 Slider1), 0 POVs.**
The user's rig needs 60 buttons / 4 axes. That is a **vJoyConf setting, not a code change** — nothing
in this codebase assumes a count. The feeder warns on the dashboard when a mapping exceeds what the
device provides. Do not "fix" this in code.

### Interop that works

`src/ModbusBridge.Core/Outputs/VJoy/VJoyInterop.cs`. All exports are `CallingConvention.Cdecl`.
`BOOL` returns must be `[return: MarshalAs(UnmanagedType.I1)]` — without it you get garbage truth
values.

The DLL is resolved at **runtime** via `NativeLibrary.SetDllImportResolver`, probing the standard
install paths. This is deliberate: the app must still start on a machine with no vJoy, reporting
"unavailable" rather than failing to launch. Do not convert this to a plain `[DllImport]` load.

`JOYSTICK_POSITION_V2` layout is in `VJoyInterop.cs` and is **confirmed correct** against the real
driver. Plain `[StructLayout(LayoutKind.Sequential)]`, no `Pack`. Field order matters absolutely —
a wrong layout does not error, it silently scrambles controls. Buttons are 4 × `uint` banks
(`lButtons`, `lButtonsEx1..3`) covering 1-32, 33-64, 65-96, 97-128. Hats are `uint` hundredths of a
degree with `0xFFFFFFFF` meaning centred.

Use `UpdateVJD` (one whole report per call), not the per-control `SetBtn`/`SetAxis` setters.
**Measured cost: 0.004 ms per full report** — a full report at 2 ms intervals is free.

### Verifying vJoy without a human looking at a screen

This is the useful trick. Feed vJoy, then read the virtual device back through the **legacy winmm
joystick API** (`joyGetNumDevs` / `joyGetDevCapsW` / `joyGetPosEx`). Completely separate code path,
no extra dependency, so a wrong struct layout is actually caught. See
`tests/ModbusBridge.SmokeTest/JoystickReader.cs`.

Two things that will trip you up:

1. **winmm reports the device name as `"Microsoft PC-joystick driver"`, not anything containing
   "vJoy".** The friendly name comes from an optional registry key that is not populated. String
   matching on the name does not work.
2. So the device is identified **by behaviour**: press a button through vJoy, read every live
   joystick, and see which one's `dwButtons` changed (`JoystickReader.IdentifyByProbe`). This also
   proves the write path reaches a real HID device.

winmm exposes axes and only the **first 32 buttons**. Buttons above 32 are checked by asserting the
report bits and that `UpdateVJD` accepted the frame.

winmm rescales axes to its own range (X reads 0-65535), so compare **proportionally**, not by raw
value. At 0/25/75/100% commanded the readback matched to within 0.1%.

---

## 3. SimHub — verified API surface

Installed at `C:\Program Files (x86)\SimHub` — a **32-bit** install.
`SimHub.Plugins.dll` version **1.0.9748.31019**, `GameReaderCommon.dll` targets **.NET Framework 4.8**.
So the plugin project is `net48` / AnyCPU. This is not negotiable.

### How to reflect SimHub's API again if you need more

Non-obvious, and it took several attempts:

- 64-bit PowerShell **fails**: SimHub bundles a 32-bit `vJoyInterfaceWrap.dll` that the assembly
  resolver trips over with `BadImageFormatException`.
- Run under **32-bit** PowerShell: `C:\Windows\SysWOW64\WindowsPowerShell\v1.0\powershell.exe`.
- `ReflectionOnlyLoadFrom` fails on dependencies. Use `Assembly.LoadFrom` with an
  `AppDomain.CurrentDomain.AssemblyResolve` handler pointing at the SimHub folder, after
  `Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase, System.Xaml`.
- `GetTypes()` throws `ReflectionTypeLoadException`; catch it and use `$_.Exception.Types` filtered
  for non-null. About 5005 types load that way.

### The API that matters (exact signatures, confirmed)

Interfaces:

```
SimHub.Plugins.IPlugin        : Init(PluginManager), End(PluginManager), PluginManager { get; set; }
SimHub.Plugins.IDataPlugin    : DataUpdate(PluginManager, ref GameData)
SimHub.Plugins.IWPFSettingsV2 : (marker; settings UI not implemented here)
```

Attributes: `PluginNameAttribute(string)`, `PluginAuthorAttribute(string)`,
`PluginDescriptionAttribute(string)`. Note there is **also** a misspelled legacy
`PluginDescritionAttribute` — use the correctly spelled one.

`PluginManager` members actually used:

```
List<string> GetAllPropertiesNames()                      <-- this is what drives the property browser
object       GetPropertyValue(string)
void         AddProperty(string, Type pluginType, Type valueType, string description)
void         SetPropertyValue(string, Type pluginType, object)
EventTrigger AddEvent(string, Type pluginType)            <-- returns a trigger
void         TriggerAction(string) / TriggerAction(string, bool)
void         AddInputMapping(string, Type, Action<PluginManager,string> onPress,
                             Action<PluginManager,string> onRelease, bool)
ControlMapperInterface GetControlMapperInterface()
```

`SimHub.Plugins.EventTrigger` has exactly one member: `void Trigger()`.

`SimHub.Plugins.ControlMapperInterface` (unused so far, but useful if you want the bridge to invoke
SimHub's own functions):

```
List<string> GetAvailableButtonRoles()
List<string> GetAvailableKeyboardSimulatedKeysRoles()
List<string> GetAvailableSimHubControlRoles()
bool StartRole(string) / StopRole(string) / StartStopRole(string, int)
```

Logging: `SimHub.Logging.Current.Info(string)` / `.Error(string)`.

### How "SimHub input triggers" is implemented

A bridge tag fed back to the plugin becomes **both**:
- a SimHub property `Bridge.<name>` (usable in dashboards and formulas), and
- a SimHub **event** raised on each rising edge via `AddEvent(...).Trigger()`.

A SimHub event can be bound to any SimHub action, which is what makes a PLC contact act as an input
inside SimHub. `AddInputMapping` was considered and not used — it is for the plugin to *receive*
input, which is the wrong direction.

### Threading

`GetAllPropertiesNames()` and property registration are done on **SimHub's own thread** inside
`DataUpdate`, never from the UDP receive thread. Inbound feedback is queued
(`ConcurrentQueue`) and drained at the top of `DataUpdate`. Keep it that way.

---

## 4. Windows timing — the thing that nearly went unnoticed

The first smoke-test run reported a configured **10 ms** poll group running at an actual **16.0 ms**,
and round-trip latency reading **0.0 ms**. Two separate causes:

1. `Environment.TickCount64` and `DateTime.UtcNow` advance in **~15.6 ms steps**. They cannot measure
   or schedule anything at 10 ms. Everything on the timing path now uses `Stopwatch.GetTimestamp()`
   via `Diagnostics/Clock.cs`. **Do not reintroduce `TickCount64` there.**
2. Windows' default timer granularity is ~15.6 ms, so every `Task.Delay` rounds up. The engine calls
   `timeBeginPeriod(1)` (winmm) for its lifetime — `Diagnostics/Clock.cs`, `TimerResolutionScope`,
   toggled by `general.highResolutionTimer`. `PreciseDelay.WaitAsync` sleeps coarsely then spins the
   last ~1.5 ms so wake-ups are not rounded up.

After both fixes: **10.0 ms actual against 10 ms configured**, sub-millisecond round trips. The smoke
test asserts the overshoot is under 4 ms so this cannot regress silently.

Read-group scheduling advances from the previous deadline, not from "now", so a slow poll does not
permanently drift the schedule; if it falls more than one interval behind it resynchronises rather
than firing a catch-up burst.

---

## 5. WPF gotchas hit in this codebase

- **`DataGridCheckBoxColumn` binds TwoWay by default.** Bound to a read-only property it throws
  `InvalidOperationException` once per realised row, as a modal dialog. Use `Mode=OneWay`. This
  actually shipped briefly and the user saw it.
- **An implicit `Style` with `TargetType="Window"` is keyed on `typeof(Window)` exactly and does NOT
  apply to derived window classes.** `MainWindow` and `AutoFillWindow` fell back to system near-white
  with grey "muted" text on it. Every window must carry
  `Style="{StaticResource AppWindow}"`. There is a comment in `Themes/Dark.xaml` saying so.
- `DataGridTextColumn` / `DataGridComboBoxColumn` have **no `ToolTip` property**. Setting one is a
  XAML compile error (MC3072). Put a comment above the column instead.
- You cannot set `Style="..."` as an attribute *and* declare `<TextBlock.Style>` as an element on the
  same control (MC3024). Pick one; use `BasedOn` inside the element form.
- WinForms + WPF in one project makes `Application`, `MessageBox`, `Brush`, `Brushes`, `Label`,
  `DataGrid`, `TabControl`, `TabItem`, `Binding` ambiguous. `GlobalUsings.cs` pins them to the WPF
  meaning. `TrayIcon.cs` deliberately uses the *System.Drawing* types (`Bitmap`, `Graphics`, `Icon`,
  `Pen`, `SolidBrush`, `Color`) which are **not** aliased — do not add aliases for those.
- WinForms emits `WFAC010` telling you to move DPI config out of the manifest. Suppressed via
  `NoWarn` because this is a WPF app that needs PerMonitorV2 declared in the manifest.

### How the UI is verified without touching the user's desktop

`ModbusBridge.exe --selftest` walks every tab (and every nested TabControl, so templates are
realised), routes WPF's `PresentationTraceSources.DataBindingSource` into the app log via
`Diagnostics/BindingErrorListener.cs`, and exits non-zero if any binding failed. It is wired into
`build.ps1`.

**Do not drive the mouse with `SetCursorPos` / `mouse_event`.** This is the user's live desktop, on a
multi-monitor setup, and doing so was called out. The self-test exists precisely so you do not need
to.

---

## 6. Architecture — decisions and why

**Everything is a tag.** `TagBus` is the only thing sources and sinks share. Sources publish named
tags carrying value + quality + timestamp; sinks are mapping tables. This is what makes "no fixed
assignments" true rather than aspirational. Do not add a direct path from a source to a sink.

**Change detection is by version counter, not events.** `TagEntry.Version` is a monotonic counter;
each sink polls at whatever rate it wants and only does work when the version moved. Lock-free, no
event storms, each consumer picks its own latency. Do not "improve" this into an event bus.

**Modbus is implemented directly**, not via NModbus/EasyModbus. The published exe carries no
third-party dependency and framing/errors/limits are all inspectable in one place. Supported both as
client and server: FC 1, 2, 3, 4, 5, 6, 15, 16, 22, 23, 43/14.

**Config reload is a full teardown and rebuild.** Keeps the hot path free of "did config change"
checks. Tags survive the swap, so an HMI reading a value keeps reading it.

**`ObservableCollection` in the config model** is deliberate — WPF `DataGrid` cannot add or remove
rows against a plain `List<T>`. System.Text.Json handles it fine.

**Write-loop suppression:** a write group never sends a value whose tag was last written by that same
device (`TagEntry.LastWriterId`). Without this, a shared register oscillates.

**Quality is not decoration.** `Never`/`Bad`/`Stale`/`Good` drives the server's stale/failsafe
behaviour and the vJoy release-on-bad-quality failsafe. An HMI showing a frozen number from a session
that ended is a real hazard; that is what `StaleBehavior.Failsafe` and `ModbusException` are for.

---

## 7. The SimHub wire protocol

`shared/TelemetryProtocol.cs` is **compiled into two projects** — the net8 bridge and the net48
plugin — via `<Compile Include>` links. That is what guarantees the two ends cannot drift.

**Constraints on that file:** plain C# 7 constructs and `BitConverter` only. No `Span`, no
`BinaryPrimitives`, no records, no `init`, no ranges/indices — none of those exist in net48 without
extra packages, and a shared file needing a NuGet reference on one side is a trap. It carries
`#nullable disable` for the same reason.

Little-endian throughout (native on both ends). UDP on loopback, 8 KB max datagram.

| Type | Direction | Purpose |
| --- | --- | --- |
| 1 Catalog | plugin → bridge | Every property SimHub offers, **chunked** (900+ properties do not fit one datagram) |
| 2 Schema | plugin → bridge | Ordered set the following data frames carry, identified by an FNV hash |
| 3 Data | plugin → bridge | One sample; carries a plugin-stamped timestamp |
| 4 Subscribe | bridge → plugin | What the bridge wants streamed |
| 5 InputState | bridge → plugin | Bridge tags going back into SimHub |
| 6 Hello | plugin → bridge | Liveness + current game |
| 7 RequestCatalog | bridge → plugin | Refresh the browser |

The bridge replies to whatever source endpoint the plugin sent from, so no port needs configuring on
the plugin side beyond the bridge's.

Data frames arriving for an unknown schema id are dropped; the plugin resends the schema every 200
frames and whenever the subscription changes, so a bridge restart recovers on its own.

**Latency is genuinely one-way**, not a round trip: the plugin stamps
`DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()` and both processes read the same system clock.

Set `SIO_UDP_CONNRESET` (`-1744830452`) via `IOControl` on **both** sockets. Without it, the other
end closing turns the next receive into an exception and kills the loop.

---

## 8. Bugs found by the tests — and what they teach

These all shipped briefly. They are listed because each represents a class of mistake.

1. **Same SimHub property subscribed twice silently dropped one.** The ingest server keyed
   subscriptions by property name in a plain `Dictionary`, so a second subscription (raw km/h plus
   scaled mph) overwrote the first. Now keyed to a **list**. Lesson: a config the UI lets you create
   twice must be handled twice.
2. **`ObjectDisposedException` taking the process down at shutdown.** The engine cancelled and
   immediately disposed the housekeeping `CancellationTokenSource` without awaiting the task holding
   its token. Always await the task, then dispose the source.
3. **Double-dispose in a test helper.** `FakeSimHubPlugin` was disposed deliberately mid-test and
   again by `using`; `CancellationTokenSource.Cancel()` on a disposed source throws. `Dispose` is now
   idempotent.
4. **The two WPF issues in section 5.**

---

## 9. Testing — how to prove things here

`.\build.ps1` runs everything. Two suites:

**`tests/ModbusBridge.SmokeTest`** — stands up a virtual PLC, a device runner, the HMI-facing server
and real Modbus clients in one process, then checks: polling, NO/NC inversion, scaling, serving, HMI
writes landing back on tags, 5 concurrent clients, exception responses, codec round-trips across
every word/byte-order combination, poll-interval timing, vJoy against the real driver with winmm
readback, and the whole SimHub path against a simulated plugin.

**`--selftest`** — the UI binding walker described in section 5.

Design principle worth keeping: **verify through a different path than the one that wrote the data.**
vJoy is checked through winmm, not by reading back the report struct. The Modbus server is checked
with a real client socket, not by calling the data store. The SimHub protocol is checked by a fake
plugin compiling the same shared file.

Anything that cannot be tested here skips with a `....` note rather than failing, so the suite stays
green on a machine without vJoy.

---

## 10. What is deliberately NOT built, and why

- **vJoy shift layers** — asked about, user selected only "momentary" as the default. Toggle and
  pulse were implemented anyway (cheap, and consistent with "everything configurable"). Shift layers
  are more work and were deferred, not rejected.
- **A settings UI inside SimHub** (`IWPFSettingsV2`) — the property browser lives in the bridge
  instead, which is what the user asked for ("let me browse"). The plugin reads only a small JSON
  file (port, intervals). Less to break.
- **Keyboard / macro output** — requested, not yet started. Build it with a dry-run mode so tests
  never actually type into the desktop.
- **Auto-configuring vJoy** via `vJoyConfig.exe` to get 60 buttons. It needs admin and changes the
  user's system. Flag it in the UI instead — which it does.
- **Installing the plugin into SimHub.** Writes to Program Files, needs elevation, needs SimHub
  closed. Confirm with the user first; `install-simhub-plugin.ps1` does it.

---

## 11. Working with this user

Observed over the session, offered so you calibrate rather than relearn:

- They watch closely and in real time, and will interject mid-task. Two of the bugs above were
  spotted by them before a test did.
- **"Everything must be configurable, no fixed assignments"** is the central requirement. They
  pushed back when a sample config looked like it might be a hard-coded assumption. Sample data is
  fine; make it obvious it is a template.
- They corrected a numeric detail with "don't get stuck on the exact number of buttons and axes" —
  treat stated counts (60 buttons, 4 axes, 1 PLC, 1 HMI) as *current values*, never as design limits.
- They care about latency and asked for a real-time ms readout unprompted.
- Terse, direct, occasionally blunt. Match with substance, not apology. Do not over-explain.
- **Do not take over their mouse or keyboard.** Their machine, their monitors.
- Their hardware answers so far: Siemens ET 200SP, ~10 ms polling target, telemetry to the HMI
  required and to the PLC's analog outputs desirable, run mode configurable, wants to browse SimHub
  properties themselves.

---

## 12. Siemens ET 200SP — what to expect (UNVERIFIED against their hardware)

From Siemens documentation for `MB_SERVER`, not from testing. **Confirm against the manual for their
firmware before relying on any of it.**

- S7 is big-endian throughout, so `wordOrder` and `byteOrder` both stay `HighFirst`, and a Siemens
  `REAL` maps directly onto `Float32`.
- Siemens docs number holding registers from 1, so `addressBase: 1` matches the manual. The Modicon
  `4xxxx` prefix is a notation convention, not a wire address — do not type `40001` unless the
  Modicon preset is selected.
- `MB_SERVER` maps FC3/6/16 (and FC4) to the DB passed in `MB_HOLD_REG`; bit functions map to the
  CPU's bit memory area.
- Analog channels use ±27648 counts for nominal full scale.
- **Each `MB_SERVER` instance serves exactly one TCP connection.** Multiple clients need multiple
  instances with distinct connection IDs in the PLC program. This is the single most likely thing to
  cause confusion on first hookup.
- `MB_SERVER` only processes a request when it is called, so the PLC scan time sets a floor under
  round-trip. If the bridge's poll-cycle readout looks fine but round trip is high, look at the PLC's
  OB cycle, not the network.

---

## 13. The register/scaling trap that cost the most time (2026-09-09)

**`ScalingConfig.Min` and `Max` are `double?`, and NULL DISABLES THE CLAMP.** Writing `0`/`0`
does not mean "no limits" - it clamps every engineering value to exactly zero. A generator that
emitted `min: 0, max: 0` silently zeroed 38 points across two whole register bands, and the
symptom ("all the values read 0") looks exactly like a dead data source. Leave them null.

Direction matters too: on a **server** point, `gain` converts raw -> engineering, so the register
stores `raw = engineering / gain`. A field documented as "raw / 10" needs `gain 0.1`. Getting the
sense backwards inflates by the square of the factor and saturates 32-bit fields to `0xFFFFFFFF`.

---

## 14. SimHub property names and units - measured, not assumed

- Properties are **fully qualified**: `DataCorePlugin.GameData.SpeedKmh`, not `SpeedKmh`.
  FS25 mod data is exposed under `DataCorePlugin.GameRawData.*` as ordinary named properties, so
  the FS25 block needs no plugin change - just subscriptions.
- `tools/simhub-catalog` dumps the live catalog to text. Use it instead of guessing; run it with
  the bridge STOPPED, since it binds the bridge's telemetry port.
- **Units are inconsistent between adjacent fields.** Measured against a running FS25:
  `dayTime` is SECONDS, `currentPhysicsTime` and `vehicleOperatingTime` are MILLISECONDS,
  `money` is whole currency units, `fuelLevel`/`fuelCapacity` are litres.
  Determine a unit by watching a value change over time, not by reading the field name.
- `FuelPercent` is **not a percentage for FS25** - it read 362 for a tank that was 95.6% full.
  Fuel % has to be derived from level/capacity.

---

## 15. The telemetry protocol has a hard subscription ceiling

`BuildSchema` wrote into a fixed `MaxDatagram` buffer **with no bounds check**. Subscribing 287
properties needed 16,678 bytes into 8,192, so the plugin threw
`ArgumentException` inside SimHub's `DataUpdate` on every frame. From the bridge side this is
invisible - telemetry simply never arrives. **The evidence is in SimHub's own log**
(`C:\Program Files (x86)\SimHub\Logs\SimHub.txt`); check it whenever frames stop.

Fixed by raising `MaxDatagram` to 60000 (loopback UDP allows ~65507 and the kernel reassembles)
and adding a bounds guard that truncates instead of throwing. Socket receive buffers on both ends
were raised to match, since a schema immediately followed by a data frame can overrun the default.

**Resolved 2026-09-10:** the schema is chunked now too, and `MaxDatagram` went from 8192 to 60000.
`BuildSchema` returns a list of datagrams and the ingest side reassembles by `(schemaId, chunk
index, chunk count)`, applying nothing until every chunk has arrived - a data frame decoded
against half a column list is worse than no frame. Wire version is 2, and version 1 plugins are
still accepted: the bridge logs which version the plugin speaks and keeps working.

The old ceiling of ~66 vehicle components is therefore gone, but **the installed plugin still
speaks version 1** until it is reinstalled, so subscriptions stay capped at one datagram in
practice. Reinstalling needs elevation and SimHub closed.

---

## 16. Reading the HMI's address map out of a compiled .exob

The Weintek `.exob` is opaque, but the object address is a **little-endian u16 at byte +31** of
each object record - identically for `NE` (fixed 235 bytes) and `AE` (fixed 114 bytes). Found by
scanning every byte position for the one whose values matched known pairs, then confirmed at
1517/1517 against `reference/HMI_OBJECT_MAP.csv`.

`tools/exob-map.py` is that decoder. It walks the `WINDOW` section (12-byte header, then 20-byte
`WI` window headers, then `type(2) + size(2) + payload` records, total length `size + 2`) and
pulls the address out of each record. Run against the reference project it reproduces the type
counts exactly - `NE=1516`, `AE=42`, `FK=1404` - and reports the same 1517 addressed objects:

    python tools/exob-map.py project.exob --types NE,AE --csv hmi-map.csv

It stops rather than guessing if the record chain loses sync, because a desynchronised walk
invents plausible-looking addresses.

That CSV is therefore **verified**, not merely generated. `reference/REGISTER_MAP.csv` is a
ChatGPT-written semantic document and is NOT verified - treat it as a specification, not truth.
Objects whose address is >= 8000 are bound to the HMI's own local LW registers, not to Modbus;
exclude them before comparing counts.

---

## 17. Bug classes that survived the first hunt (2026-09-10)

Six bugs were found in a second pass over code that had already been reviewed once. They are
recorded as *classes* rather than incidents, because the first hunt looked for wrong logic and
these were all something else.

**Mutation before validation.** `ServerDataStore.ApplyWrite` overlaid the client's data onto the
block image and only then decided which points were writable. A write that was refused with
exception 02 had already corrupted every read-only point it covered. The live HMI block is 317
read-only points in one span, so a single stray FC16 could blank the panel - and slow-moving
values would stay wrong while fast ones self-healed, which reads as an addressing fault.
Look for: any handler that writes to shared state before its guard clause.

**Right work, wrong loop.** `Refresh()` re-encodes every point in a block. It was called once per
*register* instead of once per *request*, so a read was O(count x points). Measured on a
317-point block, a 125-register read: 1.895 ms, against 0.045 ms after hoisting.
Look for: a per-collection operation inside a per-element loop. The tell is cost that tracks the
size of the map rather than the size of the request.

**The only configuration that hides it is the one you run.** The bug above is invisible under
`holdLastValue` with no stale timeout, because the version check short-circuits. That is what
`rig/config/bridge.json` uses. `DefaultConfig` ships `Failsafe`/2000, which does not - so every
fresh install had it and the live rig did not. Benchmark the shipped defaults, not your config.

**Cleanup on the path that cannot accumulate.** Incomplete telemetry schemas were evicted only
after a *successful* reassembly. UDP drops chunks; a schema missing one never completes, so the
eviction never ran on the only path that leaks. Look for: a bounds check inside the success
branch.

**A guard on one branch of two.** `KeyMode.Tap` checked `HasSeenInput` before the edge branch but
not before the repeat branch, and `NextRepeatTicks` starts at zero - so a tag already true at
startup fired a keystroke immediately, which is exactly what the guard existed to prevent.

**Trusting a length field over the bytes that arrived.** `ReadBitsAsync`/`ReadRegistersAsync`
checked the response's declared byte count but never against the actual PDU length, so a device
declaring 250 bytes and sending three would be indexed past the end.

### The one that must not be "fixed" at the source

`TagEntry.Version` bumps on *every* accepted write, including a republish of a value the tag
already held. `TagRecorder.onChangeOnly` compared versions and therefore wrote a row per interval
forever, because the engine republishes `bridge.*` and the derived tags every housekeeping pass.
Measured: 52 rows for a constant, now 1.

The obvious fix - make `Set` a no-op when the value is unchanged - is wrong. An unchanged
republish must still refresh `TimestampUtc`, or `SweepStale` demotes a perfectly live PLC input
that happens to sit at zero. The recorder formats the row and compares *that* instead, which is
also exactly the question it needs answered.

### How they were found

Not by reading. `tools/store-probe` sweeps map sizes and stale policies and prints a curve, which
is what made the O(count x points) cost obvious; the write corruption came from asserting an
invariant ("a refused write changes nothing") that no existing test stated. Both are now in the
smoke test, and the probe is kept because a benchmark that only ever runs at one size proves
nothing about scaling.

---

## 18. SimHub's ACTIVE game is not its RUNNING game

Two different facts, and conflating them produces a confident false statement.

- `DataCorePlugin.GameName` / `GameRunning` are separate properties. SimHub keeps a game
  **selected** - that is the active game - and it stays selected long after the game exits.
- The register map already separates them, on purpose:

      138  sim.gameRunning      1 only while a game process is live
      142  bridge.gameCode      which game SimHub has ACTIVE
      144  bridge.simhubGame    that game's name, still set after it exits

  `bridge.statusFlags` bit 2 is wired to `sim.gameRunning`, not to the game code.

Observed 2026-09-10 with SimHub open and no game running: register 138 = 0, register 142 = 1,
registers 144-159 = "FarmingSimulator25". Every one of those is correct.

**Do not "fix" this by blanking the name or the code when nothing is running.** It looks like a
stale value and is not; blanking it destroys the active/running distinction the map encodes and
loses real information. If you want to know whether a game is running, read register 138.

This was written after `tools/health.ps1` reported "game FS25 running" from the game code alone,
and that reading was repeated to the user three times while the machine had no such process. The
lesson is not about SimHub: a register whose name sounds like the question is not the same as a
register that answers it.

---

## 19. Why the documentation rule exists

`CLAUDE.md` states the rule in four lines because it is paid for on every turn of every session.
The argument for it lives here, where it is read once and on purpose.

Stale documentation is worse than none, because it gets believed and then re-derived at cost.
Each of these was found rotten and had to be reconstructed from the source:

<!-- repo-check: ignore-block -->

- `README.md` said keyboard output, shift layers, the network scanner, derived tags and CSV
  record/replay were "Not started". All five were built and shipping.
- `README.md` said GPU load "is not collected" in four separate places. `Inputs/GpuCounter.cs`
  had been collecting it through PDH for a day. Register 7 reading 0.1% settled it.
- Section 15 here said "the catalog is chunked, the schema is NOT" and quoted a ceiling of ~66
  components. The schema had been chunked and the ceiling removed.
- `CLAUDE.md` itself said "the plugin's schema is one datagram and is not chunked" - in the file
  loaded into every session, a day after chunking landed.
- `tools/make-hmi-map.py --help` cited the same dead 8 KB ceiling.
- Nine copy-pasteable commands across three files were silently corrupted by interpreted
  backslash escapes and could not have worked. Two more hid in `rig.ps1`'s own help text.

`tools/repo-check.py` now automates the checkable part of that list. Note what it did **not**
catch on its first outing: its schema pattern was written `"schema is NOT"`, case-sensitive, so it
read past `is not chunked` and printed "ok". A check that silently stops matching is worse than no
check, so every pattern now carries an example it must match and a counter-example it must not,
and `--self-test` asserts both before the real checks run.

The ordering rule matters as much as the content: update the docs **in the commit that makes the
change true**. A documentation pass afterwards is the one that never happens.

<!-- repo-check: end-ignore -->
