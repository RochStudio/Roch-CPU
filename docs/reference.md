> Detailed reference retained from the previous README. Historical version notes and screenshots may describe older builds; see the [current README](../README.md) for installation and source version.

<img src="../assets/mark.png" alt="Roch" width="96">

# Roch CPU

CPU and memory tuning for **Intel LGA1700** (12th/13th/14th Gen on any 600/700-series board) and
**AMD Ryzen** (Zen 2–Zen 5, AM4/AM5). One dark window, typeable values, a log — the third Roch
Studio tool, next to [Roch GPU](https://github.com/RochStudio/Roch-GPU) and
[Roch Viewer](https://github.com/RochStudio/Roch-Viewer).

It began as a re-creation of MSI Dragon Power that does not need an MSI board. Almost everything
it does goes through interfaces every LGA1700 CPU and every Intel 600/700-series chipset expose
the same way, so it does not care who made the board.

<p align="center">
<img src="../screenshot.png" alt="Roch CPU on an Intel LGA1700 board" height="600">
&nbsp;&nbsp;
<img src="../screenshot-amd.png" alt="Roch CPU on an AMD AM5 board" height="600">
</p>

<p align="center"><sub>The same window on Intel and on AMD. The rows follow the CPU: OC mailbox
voltages and board rails on one, the SMU's limits and Curve Optimizer on the other.</sub></p>

> Writing ratios and voltages can crash the machine, corrupt work in progress, and in the extreme
> damage hardware. Nothing here persists across a reboot, but a bad value is live the moment you
> press Apply.

## What's new in 1.0.1

* **Base clock and CPU VDD2 are settable** on MSI 600/700-series boards, through the clock
  generator and regulator on the EC's I²C bus, measured after every step.
* Apply and Reset run off the UI thread, so the window stays live while a change is measured.
* Voltage rows re-read as the window refreshes; a bad read at launch no longer sticks.
* The title bar's close and minimise buttons are visible again, on a grey bar.
* `--help` lists the diagnostics. The EC probes that could hang the board are gone.

## What it drives

### Intel

| Control | Path | Works on |
|---|---|---|
| CPU ratio (all-core and the per-active-core-count table) | MSR 0x1AD / 0x1AE | unlocked multiplier (Z-series + K CPU). Elsewhere *OC Lock* rejects the write and the window says so |
| E-core ratio | MSR 0x650 | same |
| Ring ratio | OC mailbox + MSR 0x620 (the MSR alone is ignored on Alder/Raptor Lake) | same |
| Core / E-core L2 / Ring / SA / GT voltage, offset or override | Intel OC mailbox (MSR 0x150) over the CPU's SVID path | every board, **if the VRM follows SVID** — see below. On Raptor Lake SA is domain 4 and E-core L2 domain 5, both measured; published tables often say 3 |
| PL1 / PL2 power limits | MSR 0x610 | every board unless locked in BIOS |
| Base clock | the board's clock generator, over the EC's I²C mailbox | MSI 600/700-series with a Nuvoton EC. Read-only elsewhere |
| CPU VDD2 | the board's regulator, over the same mailbox | same boards; clamped to 1.100–1.450 V |
| DDR5 VDD / VDDQ / VPP per DIMM | the PMIC on each module over the PCH SMBus | any board leaving the SMBus visible. Vendor-locked (*secure mode*) PMICs read but refuse writes |
| Measured Vcore, VDD2 and AUX | Super I/O over LPC (Nuvoton NCT6683/6686/6687, NCT679x, ITE IT86xx/87xx) | boards with one of those chips; elsewhere the rail check is skipped and the tool still runs |

Everything above except the rail measurement is a CPU or chipset feature, so it behaves the same on
ASUS, ASRock, Gigabyte and MSI. The Super I/O is the one board-specific piece, and only the Nuvoton
EC path (MSI) has been exercised on real hardware — the banked Nuvoton and ITE paths are written
from the documented register layouts and are untested. An unrecognised chip is not an error: the
tool runs and says in the log what it found.

### AMD

Every row is a message to the SMU — the same mailbox Ryzen Master, ZenStates and the BIOS use, so
it does not care who made the board.

| Control | Path |
|---|---|
| PPT / TDC / EDC | RSMU `SetPPTLimit` / `SetTDCVDDLimit` / `SetEDCVDDLimit`, MP1 fallback. PBO must be on in the BIOS or the SMU rejects it |
| Thermal limit (Tctl max) | RSMU `SetTctlMax` |
| PBO scalar | `SetPBOScalar` / `GetPBOScalar`, 1x–10x |
| Curve Optimizer, all cores and per core | `SetAllDldoPsmMargin` / `SetDldoPsmMargin`, read back per core. Cores are numbered as the SMU numbers them, from the fuse map |
| FMax | `GetBoostLimitFrequency` / `SetBoostLimitFrequencyAllCores`, Zen 4+ |
| DDR5 VDD / VDDQ / VPP | the DIMM PMIC over the FCH SMBus, same as Intel |

Supported: Matisse / Vermeer / Raphael / Granite Ridge and their Threadripper and EPYC siblings,
Zen / Zen+ with fewer rows, and the Renoir-to-Strix APUs (untested). An unknown generation is
reported in the header and nothing is written until the SMU answers a test message.

The mailbox addresses, message numbers, Curve Optimizer core addressing and fuse map come from
Ivan Rusanov's **[ZenStates-Core](https://github.com/irusanov/ZenStates-Core)**, the library
behind ZenStates and SMUDebugTool; without his years of SMU work none of this would exist.
Nothing was copied — see [the notices](../THIRD-PARTY-NOTICES.md).

**PPT / TDC / EDC read-back needs [PawnIO](https://pawnio.eu).** No SMU message reports the limit
in force; every tool that shows it reads the power table the SMU publishes in DRAM, which needs a
driver that can map arbitrary physical memory. The bundled WinRing0 cannot. With PawnIO installed
Roch CPU loads the signed `RyzenSMU` module and shows the real numbers; without it those rows
start as *Auto* and show what you last wrote. Roch CPU never installs PawnIO itself.

## If a voltage change does nothing

A CPU voltage can be set from either end of the SVID link: ask the CPU to request a different
voltage (the OC mailbox — portable, what Roch CPU does), or tell the VRM what to output (always
works, but needs that board's specific controller, bus, address and encoding).

With the BIOS **CPU Core Voltage Mode** on `Override`, MSI boards pin the VRM output and the CPU's
request goes nowhere — so a vendor tool writing the VRM still works and any CPU-side tool does
nothing. **The fix:** set the mode to `Adaptive` with **CPU Core Voltage** on `Auto`, then reboot;
the mode only takes effect on the next boot. An explicit value left in that field pins the VRM the
same way even when the mode says Adaptive.

Roch CPU does not pretend otherwise. Applying a core voltage compares the VID the CPU now requests
against the rail the Super I/O measures: if the request moved and the rail did not, the row is
marked **board ignored it**, the apply counts as failed, and the log says why. It also warns in the
header when it finds the BIOS in Override mode. `--vcore-test` settles it in one command.

## Base clock and CPU VDD2

Neither comes from the CPU: one is a synthesiser on the board, the other a voltage regulator, and
both sit on an I²C bus the embedded controller owns rather than on the PCH SMBus. Roch CPU reaches
them through the mailbox that EC exposes. The register layout came from watching the vendor tool's
driver; **the encodings did not** — those were established by measuring how the board responded,
and reading the base clock's off the trace gives the wrong answer entirely. See
`Hardware/EcMailbox.cs`, `EcClockGen.cs`, `BclkController.cs` and `Vdd2Rail.cs`, which carry the
detail, and [the notices](../THIRD-PARTY-NOTICES.md).

A wrong value here does not produce a wrong reading, it stops the machine. So nothing is computed
from an assumed zero point, the target is approached in small steps, and **every step is measured**
— base clock against the ACPI timer, which does not move with it, VDD2 at the board. Anything that
lands off target puts back the value found at start-up and stops.

**There is no ceiling on base clock.** Where a board gives up depends on the memory, the cache
ratio and how far the CPU is already pushed, and only the person at the machine knows it — so this
does not pretend to. Base clock scales the memory controller, the ring and the PCIe/DMI reference
together, so a little goes a long way and past a point the machine simply stops; above 105 MHz the
window asks for confirmation, which stops a typo rather than a decision. The one real limit is that
a reading the meter cannot confirm is treated as a fault and rolled back, because past that point
there is no way to know a write landed. VDD2 is clamped to **1.100–1.450 V**, where the raw byte
reaches past 3 V and would take the memory controller with it.

Measuring matters as much as setting: the TSC keeps reporting the boot-time base clock for ever,
and APERF/MPERF cancels out. Only counting unhalted core cycles against the ACPI timer sees a
change. `--bclk-test` prints all three side by side.

## Requirements

* Windows 10/11 x64, run **as administrator** (the manifest asks for elevation).
* [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0), or build
  self-contained.
* `WinRing0x64.sys` beside the executable; the build copies it.
  * Windows **Memory Integrity** or the vulnerable-driver blocklist may refuse to load it. The
    window then warns and every row reads N/A.
  * Defender sometimes flags it as a *HackTool*. It is a plain MSR / port-I/O driver; the bundled
    file's SHA-256 is in [the notices](../THIRD-PARTY-NOTICES.md).
  * The service is created on demand and removed on exit. A leftover from a killed instance is
    adopted and cleaned up by the next one.

## Building

```bash
build.cmd                   # dist\Roch CPU.exe, framework-dependent
build.cmd --self-contained  # no .NET runtime needed
```

Or `dotnet publish src/RochPower -c Release -o dist`.

## Using it

* Every row is a name, the allowed range, and a typeable value. Type and press **Apply** (or
  Enter). The window sizes itself so nothing scrolls, and controls this system does not have are
  hidden rather than greyed out.
* **0** restores what the row held when Roch CPU *started* — so if you raise a value, close the
  window and reopen it, that becomes the new starting point. Reboot to get back to BIOS values. A
  voltage override goes back to *Auto*. On AMD, PPT / TDC / EDC cannot be read at start-up, so 0
  writes the CPU's stock limit.
* **Revert** re-reads the hardware into the fields; **Reset** writes the start-up values back.
* **Per-Core Ratio Table** (Intel) and **Curve Optimizer** (AMD) open the per-core dialogs.
* **Auto ratio step** (Intel) raises the ratio every *n* seconds until a write is rejected or you
  press Stop. Run a stress test beside it.
* **Log** explains every failure: a locked MSR, a rejected mailbox value, a PMIC in secure mode, an
  SMBus hidden by the BIOS, a driver Windows would not load.

## Safety

* Voltages above the usual daily-use range ask for confirmation. Nothing stops you confirming.
* DDR5 PMIC register scales differ between kits — overclocking kits use 10 mV steps where JEDEC
  says 5 mV, which would double a voltage written with the wrong scale. Roch CPU calibrates every
  rail against the PMIC's own ADC at start-up, enables writing only where the scale matches, and
  verifies each write by reading back **and** re-measuring. A vendor-locked PMIC stays read-only.
* Base clock has no ceiling and VDD2 is clamped; both are approached in steps and measured after
  every one, with the start-up value put back if a step lands off target — see above.
* Nothing set here persists: a reboot returns to BIOS values.

## Diagnostics

`--help` lists them all. The useful ones: `--probe` writes a report of every register the tool
relies on (attach it when reporting a board that misbehaves), `--vcore-test` says plainly whether
the board follows the CPU, and `--sio-dump` / `--smbus-scan` / `--clkgen-dump` are the read-only
probes for porting to unfamiliar hardware.

## Layout

```
src/RochPower/Hardware   WinRing0 client, Intel MSR/CPU + OC mailbox, AMD CPU + SMU mailbox,
                         PCH/FCH SMBus, DDR5 PMIC, SMBIOS, BCLK meter, Super I/O,
                         EC I²C mailbox + clock generator + VDD2 regulator
src/RochPower/Core       settings model, hardware model (picks the Intel or AMD rows)
src/RochPower/UI         theme, main window, per-core and Curve Optimizer dialogs, log window
drivers/                 WinRing0x64.sys; pawnio/RyzenSMU.bin (optional)
```

`IKernelDriver` is the only thing the rest of the code talks to, so another ring-0 backend can be
dropped in without touching the UI.
