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
python tools\exob-map.py project.exob --types NE,AE --csv map.csv     # HMI address map from a compiled .exob

.\rig.ps1 health                # whole chain in one read: HMI, telemetry, game, host stats
.\rig.ps1 tag fs.fuelLevel      # read a tag BY NAME: address, decode and scaling in one step
.\rig.ps1 config find fuel      # everything the config says about a tag or address
.\rig.ps1 config gaps           # addresses a block reserves but no point covers
.\rig.ps1 outline DeviceRunner  # types and members with line numbers - read a range, not 771 lines
.\rig.ps1 check                 # repository consistency, no build needed
.\rig.ps1 errors                # just the warnings and errors from the newest log
.\rig.ps1 probe                 # server data store: read scaling + write masking
dotnet run --project tools\simhub-catalog                             # dump SimHub properties (bridge must be STOPPED)
dotnet run --project tools\store-probe                                # bench + invariant check for ServerDataStore
```

`build.ps1` now stops a running `ModbusBridge` itself and says so at the end - the app holds an
open handle to its own exe, and the resulting link error names nothing useful. It does not
restart it; `.\rig.ps1 build` does both.

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
- **A client write must never reach a read-only register.** `ServerDataStore` overlays an incoming
  write onto the block image, and it once did so *before* checking writability - so a refused
  write still rewrote every read-only point it covered, and they reported the client's bytes
  until their tags next moved. Blocks now carry a writable-coverage mask (per *bit* for register
  areas, so a packed bit cannot clobber a read-only neighbour in the same word). If you touch
  `ApplyWrite`, run `dotnet run --project tools\store-probe`.
- **Anything per-request must not be done per-register.** `Refresh()` was called inside the
  address loop, making a read O(count x points): 1.9 ms for a 125-register read of a 317-point
  block. Only `holdLastValue` with no stale timeout hid it, and that is the one combination the
  live config uses - `DefaultConfig` ships `Failsafe`/2000 and did not. The probe prints a curve;
  read cost must stay flat as the map grows.
- **A tag's `Version` bumps on every accepted write, including a republish of an unchanged
  value.** The engine republishes `bridge.*` and the derived tags every housekeeping pass, so
  "has this changed?" cannot be answered by comparing versions. `TagRecorder.onChangeOnly` did
  exactly that and wrote a row per interval. Do not "fix" this in `TagEntry.Set`: an unchanged
  republish must still refresh the timestamp, or `SweepStale` demotes a live input that happens
  to sit at zero.
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

## Keep the documentation current in the same commit

Not as a chore - as the thing that stops the next session paying to rediscover what this one
already knows. Stale docs are worse than none, because they are believed.

Every one of these was found rotten and had to be re-derived from the code. They are quoted
verbatim, so they are fenced off from `tools/repo-check.py` - which would otherwise read the
quotations as fresh claims and fail on them:

<!-- repo-check: ignore-block -->

- `README.md` said keyboard output, shift layers, the network scanner, derived tags and CSV
  record/replay were "Not started". All five were built and shipping.
- `README.md` said GPU load "is not collected" in four separate places. `Inputs/GpuCounter.cs`
  had been collecting it through PDH for a day.
- `FINDINGS.md` section 15 said "the catalog is chunked, the schema is NOT" and quoted a ceiling
  of ~66 components. The schema had been chunked and the ceiling removed.
- Nine copy-pasteable commands across three files were silently corrupted by interpreted
  backslash escapes and could not have worked. Two more hid in `rig.ps1`'s own help text.

<!-- repo-check: end-ignore -->

So, when you finish a change:

1. **`HANDOFF.md`** - status table, the tools list, and the numbered gaps. If you closed a gap,
   delete it or say what remains unverified; do not leave it reading as open.
2. **`README.md`** - the Status table, the project-layout tree, and "Not yet built". A new file
   under `tools/` or a new `src/` subdirectory belongs in the tree.
3. **`FINDINGS.md`** - only for things that cost real time to learn, and correct any section your
   change made untrue rather than appending a contradiction.
4. **`CLAUDE.md`** - the command list and the traps table. A trap earns its place by having
   already cost hours.

Do it in the commit that makes the change true, not in a documentation pass afterwards - the pass
afterwards is the one that never happens.

**Windows paths in documentation are a live hazard.** A tool call writing `rig\config\bridge.json`
through a shell heredoc can have the `\b` interpreted as a backspace, and the damage is invisible in
a rendered view. After editing any doc containing a Windows path, check it:

```powershell
git diff | Select-String -Pattern "[\x00-\x08\x0b\x0c\x0e-\x1f]"   # must print nothing
```

## Working style that fits this project

- **A passing local build says nothing about the repository.** A `.gitignore` rule once matched
  a source directory and kept nine files out of the commit; every local build still passed and a
  fresh clone failed with 54 errors. Run `.\rig.ps1 verify-clone` before trusting a push.

- **Verify through a different path than the one that wrote the data.** vJoy is checked via winmm,
  the Modbus server via a real client socket, the HMI via `tshark`. Do this rather than trusting
  config to mean what it says.
- `tshark` + Npcap are installed, including loopback. To see what the HMI truly asks for:
  `tshark -i \Device\NPF_{YOUR-ADAPTER-GUID} -f "tcp port 502" -Y modbus`
- Put throwaway scripts in `tools/` instead of retyping them inline. That is where the repeatable
  savings are.
- Ask where a file is rather than searching the user's drive.
- Treat stated hardware counts (60 buttons, 4 axes) as current values, never design limits.
