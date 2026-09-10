# Modbus Telemetry Bridge

Windows/WPF bridge: Modbus TCP **client** (polls PLCs) and **server** (serves HMIs) with a tag bus
between, plus vJoy output and SimHub telemetry ingest. Everything is configuration - no fixed
assignments.

`FINDINGS.md` holds the hard-won detail; `HANDOFF.md` holds current status and known gaps. Read
those before deep work. This file is only what you need in the first minute.

## This machine is the live rig - things are plugged in

| | |
| --- | --- |
| PLC | ET 200SP, **192.0.2.10:502**, 60 DI + 4 AI |
| HMI | Weintek, **192.0.2.61**, polls our server at ~100 req/s |
| vJoy | device 1, 60 buttons / 8 axes / **1 discrete POV** (not continuous) |
| SimHub | installed, plugin loaded, FS25 |
| Live config + logs | `rig/` (gitignored) |

## Commands

```powershell
.\build.ps1                                   # build + smoke test + UI self-test. Run before claiming done.
Start-Process .\src\ModbusBridge.App\bin\Release\net8.0-windows\ModbusBridge.exe -ArgumentList '--data','"<repo>\rig"'

pwsh -File .\tools\modbus-read.ps1 -Address 200 -Count 67 -NonZero    # read any register
pwsh -File .\tools\modbus-read.ps1 -Address 144 -Count 16 -Type string
python tools\make-hmi-map.py rig\config\bridge.json --components 16   # regenerate the HMI map
dotnet run --project tools\simhub-catalog                             # dump SimHub properties (bridge must be STOPPED)
```

The app locks its own exe - **stop `ModbusBridge` before building** or the copy step fails.

## Traps that have already cost hours

- **`ScalingConfig.Min`/`Max` are nullable; null disables the clamp.** Writing `0`/`0` pins every
  value to zero, which looks exactly like a dead data source. This zeroed 38 points once.
- **Scaling direction:** on a server point `gain` is raw -> engineering, so the register stores
  `raw = engineering / gain`. "raw / 10" means `gain 0.1`.
- **SimHub property names are fully qualified** (`DataCorePlugin.GameData.SpeedKmh`). FS25 mod data
  is under `DataCorePlugin.GameRawData.*`. Units differ between adjacent fields - `dayTime` is
  seconds, `currentPhysicsTime` is milliseconds. Measure by watching a value change; do not assume.
- **The plugin's schema is one datagram and is not chunked.** Too many subscriptions used to throw
  inside SimHub's `DataUpdate`, killing telemetry invisibly. Evidence lives in
  `C:\Program Files (x86)\SimHub\Logs\SimHub.txt` - check it whenever frames stop.
- **The PLC's `MB_SERVER` serves exactly ONE TCP connection.** While the bridge polls, anything else
  aimed at 192.0.2.10 gets "connection refused". Read the bridge's server instead.
- **The smoke test's 10 ms timing check is unreliable and has been wrong twice.** Timer resolution
  is per-process since Windows 10 2004, and the background test process does not get what the
  windowed app gets, so it reports ~15.6 ms cycles while the real bridge holds 10.3 ms. **Measure
  timing on the wire**, not from that check:
  `tshark -i <nic> -f "host 192.0.2.10 and tcp port 502" -a duration:6 -T fields -e ip.src`
  (2 read groups at 10 ms = ~194 requests/s). Do not revert a change because that check fails.
- **PowerShell:** `pwsh` (7.x) is installed - use it. The default `powershell` is 5.1 with no `&&`,
  and `RD` is an alias for `Remove-Item`, so do not name a function that.
- WinForms + WPF are both referenced: `Color`, `Control`, `ToolTip`, `MenuItem` are ambiguous.
  Fully qualify `System.Windows.*` or use the aliases in `GlobalUsings.cs`.

## Working style that fits this project

- **A passing local build says nothing about the repository.** A `.gitignore` rule once matched
  a source directory and kept nine files out of the commit; every local build still passed and a
  fresh clone failed with 54 errors. Run `.ig.ps1 verify-clone` before trusting a push.

- **Verify through a different path than the one that wrote the data.** vJoy is checked via winmm,
  the Modbus server via a real client socket, the HMI via `tshark`. Do this rather than trusting
  config to mean what it says.
- `tshark` + Npcap are installed, including loopback. To see what the HMI truly asks for:
  `tshark -i \Device\NPF_{YOUR-ADAPTER-GUID} -f "tcp port 502" -Y modbus`
- Put throwaway scripts in `tools/` instead of retyping them inline. That is where the repeatable
  savings are.
- Ask where a file is rather than searching the user's drive.
- Treat stated hardware counts (60 buttons, 4 axes) as current values, never design limits.
