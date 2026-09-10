# Modbus Telemetry Bridge

Windows/WPF bridge: Modbus TCP **client** (polls PLCs) and **server** (serves HMIs) with a tag bus
between, plus vJoy output and SimHub telemetry ingest. Everything is configuration.

This file is deliberately short - it is in context on every turn. It holds rules; the reasoning
behind them is in `FINDINGS.md`, read on demand. `HANDOFF.md` has current status and open gaps.

| | |
| --- | --- |
| PLC | ET 200SP, **192.0.2.10:502**, 60 DI + 4 AI |
| HMI | Weintek, **192.0.2.61**, polls our server at ~100 req/s |
| vJoy | device 1, 60 buttons / 8 axes / **1 discrete POV** (not continuous) |
| SimHub | installed, plugin loaded (still wire v1 until reinstalled) |
| Live config + logs | `rig/` (gitignored) |

## Commands

Prefer these to improvising - each replaces work that was being redone by hand.

```powershell
.\rig.ps1 health                 # whole chain: server, HMI, telemetry, game, host stats
.\rig.ps1 tag fs.fuelLevel       # a tag BY NAME - resolves address, decodes, applies scaling
.\rig.ps1 config find fuel       # what the config says about a tag or address (also gaps/blocks/tags)
.\rig.ps1 outline DeviceRunner   # types + members with line numbers - then read a range, not 771 lines
.\rig.ps1 errors                 # warnings and errors only, from the newest log
.\rig.ps1 check                  # static repo consistency, ~1 s, no build
.\rig.ps1 portable               # build a pristine copy: no .git, no rig/, no build output
.\rig.ps1 probe                  # server data store: read scaling + write masking
.\rig.ps1 build | start | stop | restart | log | read | map | capture | scan
.\build.ps1                      # check + build + smoke test + probe + UI self-test

python tools\config-query.py <cfg> <cmd>      # same as 'rig config', any config file
python tools\code-map.py --grep <member>      # where is this declared?
python tools\exob-map.py project.exob --types NE,AE --csv map.csv
python tools\make-hmi-map.py rig\config\bridge.json --components 16
dotnet run --project tools\simhub-catalog     # SimHub properties (bridge must be STOPPED)
```

Tool output is quiet by default; pass `--verbose` to the smoke test or `tools\store-probe` when
something failed. `build.ps1` stops a running `ModbusBridge` itself (it locks its own exe) and does
not restart it - `.\rig.ps1 build` does both.

`/bughunt`, `/ship` and `/doccheck` in `.claude/commands/` encode orderings already got wrong here.

## Traps

- **`ScalingConfig.Min`/`Max` are nullable; null disables the clamp.** `0`/`0` pins every value to
  zero, which looks exactly like a dead data source. Zeroed 38 points once.
- **Scaling direction:** on a server point `gain` is raw -> engineering, so the register holds
  `raw = engineering / gain`. "raw / 10" means `gain 0.1`.
- **Never claim a program is running without `Get-Process`.** Reading it off a held value produced
  a confident false claim three times in one session. Separately: SimHub's **active** game is not
  its **running** game - `sim.gameRunning` (138) answers that; `bridge.gameCode` (142) and
  `bridge.simhubGame` (144) stay set after the game exits. Do not blank them. FINDINGS 18.
- **A tag's `Version` bumps on every accepted write, including an unchanged republish.** The engine
  republishes `bridge.*` and derived tags every housekeeping pass, so versions cannot answer "has
  this changed?". Do not fix that in `TagEntry.Set` - an unchanged republish must still refresh the
  timestamp or `SweepStale` demotes a live input sitting at zero.
- **Two fixed `ServerDataStore` bugs that must not return:** a client write must not reach a
  read-only register, and nothing per-request may be done per-register. Run `.\rig.ps1 probe`
  after touching the read or write path. FINDINGS 17.
- **The PLC's `MB_SERVER` serves exactly ONE TCP connection.** While the bridge polls, anything
  else aimed at 192.0.2.10 gets "connection refused". Read the bridge's server instead.
- **SimHub property names are fully qualified** (`DataCorePlugin.GameData.SpeedKmh`); FS25 mod data
  is under `GameRawData.*`. Units differ between adjacent fields - `dayTime` is seconds,
  `currentPhysicsTime` is milliseconds. Measure by watching a value change. When frames stop, check
  `C:\Program Files (x86)\SimHub\Logs\SimHub.txt`.
- **The smoke test's 10 ms timing check is unreliable and has been wrong twice** - timer resolution
  is per-process and the test process does not get what the windowed app gets. Measure on the wire
  instead; do not revert a change because that check failed. FINDINGS 9.
- **PowerShell:** use `pwsh` (7.x). The default `powershell` is 5.1, no `&&`, and `RD` aliases
  `Remove-Item`. WinForms and WPF are both referenced, so `Color`, `Control`, `ToolTip` and
  `MenuItem` are ambiguous - fully qualify `System.Windows.*`.

## Rules

- **Update `HANDOFF.md`, `README.md`, `FINDINGS.md` and `CLAUDE.md` in the commit that makes the
  change true.** Not in a pass afterwards - that pass never happens. Closed a gap? Delete it or say
  what is still unverified. New file under `tools/`? It belongs in the README tree, and in
  `ModbusBridge.sln` if it is a project. FINDINGS 19 is the argument for this, with the receipts.
- **Use the Write/Edit tools for any file containing a backslash.** Heredocs interpret `\b` and
  `\r`, the damage is non-printing, and it has corrupted eleven commands here. Escaping harder does
  not work; building the backslash as `chr(92)` does. Verify with `.\rig.ps1 check` - do not
  hand-roll the regex, the obvious one omits `\x0d`.
- **This must compile on any Windows machine, and on Linux barring a rewrite.** Only the WPF
  app may be Windows-pinned - everything else is plain `net8.0`, with the P/Invoke guarded at
  runtime. `git`, `python`, `pwsh`, SimHub and vJoy are all optional to a build. Windows ships
  PowerShell 5.1 and not `pwsh`, so scripts must parse under 5.1.
- **A passing local build says nothing about anyone else's machine.** Every portability breach
  here was found by a stranger's build failing, never by testing. `.\rig.ps1 check` covers the
  static rules; `.\rig.ps1 portable` builds a copy with no `.git`, no `rig/` and no build output;
  `.\rig.ps1 verify-clone` clones `origin`, so run that one **after** the push.
- **Verify through a different path than the one that wrote the data.** vJoy via winmm, the server
  via a real client socket, the HMI via `tshark` (installed, loopback too). Do not trust config to
  mean what it says.
- Ask where a file is rather than searching the user's drive.
- Treat stated hardware counts (60 buttons, 4 axes) as current values, never design limits.
