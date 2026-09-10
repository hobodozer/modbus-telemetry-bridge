# TIA Portal Openness tools

Two small tools for reading the PLC project through the TIA Portal Openness API, so the
bridge's address map can be checked against what the PLC program actually does rather
than inferred from Siemens documentation.

```powershell
.\build-tia-tools.ps1        # compiles both into .\bin
.\bin\TiaInspect.exe         # hardware, IP addresses, block list, tag tables
.\bin\TiaExport.exe <outdir> # block source as XML  (needs STEP 7 Professional)
```

Start TIA Portal and open the project first. Both tools **attach** to the running
instance rather than starting their own, which avoids a second licence checkout and a
fight over the project lock. TIA may raise a confirmation dialog the first time an
external application attaches - it needs a click, or the attach hangs.

Neither tool writes to the project. `TiaInspect` closes a project only if it opened one
itself; if you already had one open it is left exactly as found.

---

## Verified environment (2026-09-09, the dev machine)

| Fact | Value |
| --- | --- |
| TIA Portal | V18, `C:\Program Files\Siemens\Automation\Portal V18` |
| Openness API | V18, `PublicAPI\V18\Siemens.Engineering.dll`, AssemblyVersion 18.0.0.0 |
| Public key token | `d29ec89bac048f84` |
| Account | `<DOMAIN>\<user>` is in the local `Siemens TIA Openness` group - required, and already done |
| Compiler used | `C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe` (C# 5 dialect) |

---

## Two traps that cost real time

### 1. Do not write the AssemblyResolve handler in PowerShell

`Siemens.Engineering.dll` pulls in siblings that are not next to the caller, so an
`AppDomain.AssemblyResolve` handler is mandatory. Writing that handler as a PowerShell
scriptblock **fails with a StackOverflowException**, and it fails late - the assembly
loads, `TiaPortal.GetProcesses()` works and prints its result, and the process then dies
inside `.Attach()`.

The reason: Openness resolves dependencies on background threads, and a PowerShell
scriptblock cannot safely be invoked from an arbitrary thread. Guarding against
re-entrancy does not fix it; the problem is the thread, not the recursion.

That is why these tools are compiled C#. A C# handler is thread-safe and works. It still
needs both guards:

- return an assembly that is **already loaded** instead of loading a second copy under
  the same identity - that alone causes a resolve loop, and
- refuse to re-enter for a name already in flight.

Register the handler in `Main`, and keep the code that touches Openness types in a
separate `[MethodImpl(MethodImplOptions.NoInlining)]` method, so the JIT cannot pull
those types in before the handler exists.

### 2. Export needs a STEP 7 Professional licence; enumeration does not

`TiaInspect` works unlicensed. `TiaExport` does not:

```
Error when calling method 'Export' of type 'Siemens.Engineering.SW.Blocks.GlobalDB'.
Necessary licence 'STEP 7 Professional' is missing.
```

**This was a licence-checkout problem, not a missing product, and a reboot fixed it.**
STEP 7 Professional V18 Update 5 is installed - the export's own `InstalledProducts`
header confirms it. Do not go looking for an Automation License Manager install or
licence-key folders on this machine; there are none, and their absence is not the
diagnosis. If `Export` reports the licence missing, restart TIA Portal, and reboot if
that is not enough.

While it is in that state you can still read hardware configuration, IP addresses, block
names/numbers/languages, process-image addresses and tag tables - everything except block
source and DB layouts.

---

## What the project looks like (read 2026-09-09)

Project: `PLC_Modbus_Repaired.ap18` (2.7.1 output folder).

**Hardware** - ET 200SP station, CPU 1512SP-1 PN `6ES7 512-1DK01-0AB0` firmware V2.9,
PROFINET interface X1 at **192.0.2.10**.

| Module | Qty | Channels |
| --- | --- | --- |
| DI 16x24VDC ST | 3 | 48 |
| DI 4x120..230VAC ST | 3 | 12 |
| **DI total** | | **60** |
| AI 4xI 2-/4-wire ST | 1 | **4** |
| DQ 16x24VDC/0.5A ST | 2 | 32 |
| RQ 4x120VDC/230VAC/5A NO ST | 3 | 12 |

The 60 digital inputs and 4 analog inputs are where "60 buttons / 4 axes" comes from -
it is the physical module complement, one contact per vJoy button and one AI per axis.
Treat it as the current hardware, not a design limit (see FINDINGS section 11).

**Program** - four blocks, empty default tag table:

```
FB_Generic_Modbus      FB1   SCL
DB_Generic_Modbus      DB2   instance DB
DB_Generic_Modbus_HR   DB1   global DB - the holding-register area
Main                   OB1   SCL
```

---

## The Modbus address map (resolved 2026-09-09, from the exported blocks)

`FB_Generic_Modbus` wraps the standard Siemens **`MB_SERVER` (version 5.3)** instruction -
an earlier guess in this file that it was a hand-rolled SCL server was wrong. The
`MB_SERVER` limits in `FINDINGS.md` section 12 therefore **do** apply, including one TCP
connection per instance. The DB1 block comment says so independently: "Single TCP server,
port 502."

`DB_Generic_Modbus_HR` (DB1) is a single member: `Regs : Array[0..255] of Word`.

Configuration set by the FB, read out of the exported SCL:

```
Connection.ID            := 1
Connection.ConnectionType := B#16#0B     (TCP)
Connection.ActiveEstablished := FALSE    (passive - it is a server)
Connection.RemoteAddress  := 0.0.0.0     (accepts any client address)
Connection.RemotePort     := 0           (any source port)
Connection.LocalPort      := 502

Server.HR_Start_Offset    := 0           holding regs start at DB1.Regs[0]
Server.QB_Start           := 0           coil write window starts at QB0
Server.QB_Count           := 16#20       32 bytes -> 256 coils
Server.QB_Read_Start      := 0
Server.QB_Read_Count      := 16#20
Server.IB_Read_Start      := 0           discrete-input window starts at IB0
Server.IB_Read_Count      := 16#40       64 bytes -> 512 bits / 32 input registers
```

### Process-image addresses (from the hardware configuration)

Lengths below are in **bits**, as Openness reports them.

| Module | Area | Bytes | Modbus |
| --- | --- | --- | --- |
| DI 4x120..230VAC ST_2 | Input | IB1 | discrete in 8-11 |
| DI 16x24VDC ST_2 | Input | IB2-3 | discrete in 16-31 |
| DI 16x24VDC ST_3 | Input | IB4-5 | discrete in 32-47 |
| AI 4xI 2-/4-wire ST_1 | Input | IB6-13 | **input regs 3,4,5,6** |
| DI 4x120..230VAC ST_3 | Input | IB14 | discrete in 112-115 |
| DI 16x24VDC ST_1 | Input | IB15-16 | discrete in 120-135 |
| DI 4x120..230VAC ST_1 | Input | IB17 | discrete in 136-139 |
| DQ 16x24VDC/0.5A ST_1 | Output | QB0-1 | coils 0-15 |
| RQ 4x...NO ST_1 | Output | QB2 | coils 16-19 |
| DQ 16x24VDC/0.5A ST_2 | Output | QB3-4 | coils 24-39 |
| RQ 4x...NO ST_2 | Output | QB5 | coils 40-43 |
| RQ 4x...NO ST_3 | Output | QB6 | coils 48-51 |

Sixty digital inputs and four analog inputs, matching the module count. **IB0 is unused**,
so discrete inputs 0-7 always read zero - the inputs do not start at Modbus address 0.

Function-code mapping, which is stock `MB_SERVER` behaviour and NOT all one DB:

| FC | Area |
| --- | --- |
| 1, 5, 15 (coils) | Q process image from QB0 |
| 2 (discrete inputs) | I process image from IB0 |
| 3, 6, 16 (holding regs) | `DB_Generic_Modbus_HR`.Regs[0..255] |
| 4 (input registers) | I process image as words from IB0 |

That is why a live probe sees FC3 return zeros while FC4 returns data at the same
offsets: they are different memory areas, not the same DB. FC4 register 3 is IW6, the
first analog channel - which is exactly where the four open-circuit `0x8000` readings
showed up.

### Corrections to earlier probe conclusions

- **FC2 is supported.** `IB_Read_Count` is 16#40, so discrete inputs are served. An
  earlier probe got no reply because each probe opened its own TCP connection and
  `MB_SERVER` serves exactly one at a time - connection churn, not a missing function
  code. Retest on a single persistent connection.
- **Unit id being ignored is normal** for `MB_SERVER`; it is not evidence of a custom
  implementation.
- Reading FC4 register 7 (IW14) returned `0x0027`: IB14 = 0 (that AC input module all
  off) and IB15 = 0x27, i.e. four contacts closed on `DI 16x24VDC ST_1`. Live wiring.

### Watch out

The DB1 comment states "**No communication-loss watchdog**" on the PLC side. The bridge's
own watchdog (see the README's Watchdog section) is therefore the only thing that will
notice a dropped link - there is nothing in the PLC program that will fail safe on its
own.
