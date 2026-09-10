# Handoff - state as of 2026-09-10

Read `FINDINGS.md` first - especially sections 13-16, which are new and cover the traps that
cost the most time in the session that produced this. This file is only the status summary; `README.md` is user docs.

## The rig, as it actually runs now

    FS25 -> SimHub -> plugin --UDP--> BRIDGE -> Modbus server :502 -> Weintek HMI (192.0.2.61)
                                        |
            ET 200SP 192.0.2.10 ------+--> vJoy device 1 (60 buttons)
                     (60 DI, 4 AI)

Everything above was verified live on 2026-09-10. Run it with:

    .\build.ps1
    src\ModbusBridge.App\bin\Release\net8.0-windows\ModbusBridge.exe --data "<repo>\rig"

`rig\` holds the live config and logs. `build.ps1` was last run **all green**, including the
new hat checks, after every change below.

## Verified working

| Area | Evidence |
| --- | --- |
| PLC read path | 60 DI + 4 AI polled at 10.0 ms from the real ET 200SP |
| vJoy | 60 buttons fed at 2 ms; axes present but **deliberately disabled by the user** |
| Discrete hats from 4 contacts | new; verified through winmm readback on the real driver |
| SimHub link | plugin loads in SimHub, 287-property schema, frames every ~5-9 ms |
| HMI server | 4x-1..4x-2000 served, no exception 02, HMI polling continuously |
| Host stats | CPU/RAM/disk/network/processes live in registers 0-14 with no game running |
| FS25 telemetry | scalars, 32-bit times, money, fuel litres all decode correctly in-game |

## Built in the 2026-09-09/10 session

- `src/ModbusBridge.Core/Inputs/PcStatsCollector.cs` - host stats, BCL + kernel32 only, no NuGet.
- `BridgeEngine.PublishBridgeStatus()` - protocol version, connected clients, telemetry age, game code.
- Four-contact and **discrete** vJoy hats (`VJoyPovSource`, `VJoyPovKind`, `SetDiscretePov`).
- vJoy **shift layers** - a modifier contact remaps buttons while held.
- vJoy **per-game profiles**, composing with layers; also gate axes and hats.
- **Derived tags**, a **network scanner**, **CSV record/replay** and **keyboard output**.
  Keyboard output ships with `dryRun` on; it has never been run for real, only against a
  recording sink.
  The device here has 0 continuous + 1 discrete POV, so hats could not have worked before.
- Learn dialog (vJoy tab) and output test panel (Devices tab).
- Theme fixes: `ComboBox` retemplated, styles for `ToolTip`/`ComboBoxItem`/`ContextMenu`/`MenuItem`.
- `--selftest` now also renders controls offscreen and checks painted contrast, so a template that
  ignores its `Background` fails the build.
- `tools/make-hmi-map.py` generates the whole HMI register map (316 points, 287 subscriptions).
- `tools/simhub-catalog` dumps the live SimHub property catalog to text.
- `tools/tia/` - TIA Openness inspector/exporter; see its README.

## Known gaps - none of these are mysteries, they are unfinished work

1. **Derived tags are configurable now.** `derived` in the config computes a tag from an
   expression over other tags. Fuel percent, the schema versions and the status-flag bitmask all
   come from there; the hard-coded stopgap in the engine is gone.
2. Registers 1, 141, 200, 201, 240, 241 are mapped but nothing computes them; they read 0.
3. Register 7 (GPU %) - the collector does not gather it. Needs PDH `GPU Engine` counters.
4. Components are mapped to 16 of the map's 100. The schema is chunked now, so the old ceiling is
   gone - raise it with `python tools\make-hmi-map.py rig\config\bridge.json --components 100`.
   **The installed plugin still speaks wire version 1**, so until it is reinstalled (needs
   elevation and SimHub closed) subscriptions stay capped at one datagram. The bridge logs a
   warning saying so and keeps working meanwhile.
5. The learn dialog and output panel have **no automated coverage of their behaviour** - the
   self-test walks tabs, not dialogs. Two bugs in the learn dialog were found by the user, not tests.
6. FS25 `playTime` units are unconfirmed (tag read 167; could be seconds or minutes).

## Things that will bite you

- `ScalingConfig.Min`/`Max` are nullable and **null disables the clamp**; `0`/`0` zeroes everything.
- Duplicate SimHub subscriptions are **rejected by config validation** even though FINDINGS section 8
  says they are supported and the smoke test exercises them. The smoke test builds config in code and
  bypasses validation, so the two disagree. Put the second scaling on a server point instead.
- String points need `length` (registers), not `size`; `Size` is computed from it.
- The PLC's `MB_SERVER` serves **exactly one TCP connection**. While the bridge is polling, nothing
  else can reach 192.0.2.10 - probes get "connection refused".
- The bridge's server binds `0.0.0.0:502` with no allow-list.
