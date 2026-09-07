<img src="assets/mark.png" alt="Roch" width="96">

# Roch CPU

Board-independent CPU and memory tuning for **Intel LGA1700** (12th / 13th / 14th Gen Core on
any Z690 / Z790 / B660 / B760 / H670 / H770 board) and **AMD Ryzen** (Zen 2 to Zen 5 on AM4 /
AM5: PPT / TDC / EDC, thermal limit, PBO scalar, Curve Optimizer, FMax, DDR5 voltages). The
third Roch Studio tool, next to
[Roch GPU](https://github.com/RochStudio/Roch-GPU) and
[Roch Viewer](https://github.com/RochStudio/Roch-Viewer), and built in the same shape:
one dark window, typeable values, a log.

It started as a re-creation of MSI Dragon Power that does not need an MSI board. Almost
everything it does uses interfaces every LGA1700 CPU and every Intel 600/700-series chipset
expose the same way, so it does not care who made the board. The two exceptions are base clock
and CPU VDD2: those live on hardware the board's embedded controller owns, they are reached
through that controller's mailbox, and so far that mailbox is only mapped on MSI's Nuvoton EC
parts. Everything else works with or without it.

<p>
<img src="screenshot.png" alt="Roch CPU on Intel" width="430">
<img src="screenshot-amd.png" alt="Roch CPU on AMD" width="430">
</p>

## What's new in 1.0.1

* **Base clock is settable** on MSI 600/700-series boards, through the clock generator on the EC's
  I2C bus, capped at 102.5 MHz and measured after every step.
* **CPU VDD2 is settable** on the same boards, through the board regulator, clamped to 1.100–1.450 V
  and measured at the rail after every step.
* Apply and Reset run off the UI thread, so the window stays live while a change is measured.
* The mailbox voltage rows are re-read as the window refreshes, instead of once at start-up — a
  single bad read at launch used to sit on screen for the life of the process.
* The title bar's close and minimise buttons are visible again (they were being drawn with no
  glyph at all), on a grey bar.
* `--help` lists the diagnostic switches. The EC probes that could hang the board are gone, along
  with the base-clock diagnostics written against a reading of the EC that turned out to be wrong.

> Writing ratios and voltages can crash the machine, corrupt work in progress, and in the
> extreme damage hardware. Nothing here persists across a reboot, but a bad value applied
> to a DIMM or a core is live the moment you press Apply.

## What it drives

| Control | Path | Works on |
|---|---|---|
| CPU ratio (all-core, plus the per-active-core-count table) | MSR 0x1AD / 0x1AE | boards with an unlocked multiplier (Z-series + K CPU). Non-Z boards set *OC Lock* and the writes are rejected; the window says so. |
| E-core ratio | MSR 0x650 | same |
| Ring ratio | OC mailbox ring domain + MSR 0x620 (the MSR alone is ignored on Alder/Raptor Lake) | same |
| Core / E-core L2 / Ring / SA / GT voltage, offset or static override | Intel OC mailbox (MSR 0x150) through the CPU's own SVID path | every board unless the BIOS disables the mailbox or sets *Undervolt Protection*. The VRM must be following SVID: with the BIOS core voltage in **Override** mode, MSI boards fix the VRM output and a VID change from here goes nowhere; use **Adaptive** or **Auto** in the BIOS (see below) |
| PL1 / PL2 package power limits | MSR 0x610 | every board unless locked in BIOS |
| DDR5 VDD / VDDQ / VPP per DIMM | the PMIC on each module over the PCH SMBus | any board whose BIOS leaves the SMBus visible; vendor-locked (*secure mode*) PMICs read but refuse writes |
| Base clock | the board's clock generator, over the mailbox in the embedded controller; measured by counting core cycles against the ACPI timer | MSI 600/700-series boards with a Nuvoton EC. Elsewhere it is read-only. Capped at 102.5 MHz (see below) |
| Temperature, VID, clock, package power, DRAM rail power | MSRs / PMIC | every board |
| Measured Vcore, used to check that a core-voltage apply actually reached the rail | Super I/O over LPC: Nuvoton NCT6683/6686/6687, Nuvoton NCT679x, or ITE IT86xx/87xx | boards carrying one of those chips; on anything else the check is skipped and the tool still runs |
| CPU VDD2, the memory controller's supply | the board's regulator, over the same EC mailbox; measured at the Super I/O | MSI 600/700-series boards with a Nuvoton EC. Clamped to 1.100-1.450 V |
| CPU AUX, live value | Nuvoton NCT6687D over LPC | read-only; only on the MSI EC parts where the channel mapping is verified (see below) |

Base clock and CPU VDD2 do not come from the CPU at all: one is a synthesiser on the board,
the other a voltage regulator, and both sit on an I2C bus the embedded controller owns rather
than on the PCH SMBus. Roch CPU reaches them through the mailbox that EC exposes — see
[Base clock and CPU VDD2](#base-clock-and-cpu-vdd2). The other board rails Dragon Power sets
(CPU 1.05, CPU AUX, PCH 0.82) are almost certainly on the same bus, but which device and
register carry them has not been established, so they stay read-only: this does not write a
regulator it has not verified.

### Two ways to set a CPU voltage, and why only one of them is portable

A CPU voltage can be changed from either end of the SVID link:

* **Ask the CPU** to request a different voltage, through the OC mailbox. That is what Roch
  CPU does, and it needs no knowledge of the board. It only works if the VRM is actually
  following the CPU's request.
* **Tell the VRM** what to output, by writing the regulator's own set-point. That always
  works, but it requires knowing which controller the board uses, on which bus, at which
  address, with which encoding. Get any of those wrong and you can put an arbitrary voltage
  into the CPU.

Dragon Power takes the second route. Its SMBus engine ships two extra bus masters beside the
Intel one (every call has `n_` and `b_` variants) and takes a mutex named
`Access_SMBUS.HTP.Renesas.Method`, which names the VRM controller family it drives. So Dragon
Power is not doing something Roch CPU does badly, it is doing a different, board-specific thing.

That private path was chased on a Z790MPOWER, and the first attempt got it wrong in an
instructive way. The regulator does not answer on the PCH SMBus — dumping all 256 registers of
every device that acknowledges, before and after a Vcore change, shows movement only in the DDR5
PMIC's own sensor bytes — but the Nuvoton EC's register `0x470` *did* hold the new set-point every
time, as a signed millivolt byte for Vcore and as `(mV - 1100) / 10` for VDD2. One slot holding two
different encodings looked like a shared command parameter, and writing it was accepted, read back,
and did nothing. That was recorded as a dead end.

It was not a mirror. `0x470` is the **write-data register of an I2C mailbox** the EC exposes, which
is why the value appears there and why writing it alone does nothing: no target and no doorbell.
Finding that out needed the vendor tool's own driver: its exported entry points were forwarded
through a logging proxy and its port-level traffic recorded while it changed things. The bus is
real, it is reachable, and Roch CPU now uses it — see below.

Vcore is the one rail on it that Roch CPU still will not write. It does not need to: with the BIOS
in Adaptive mode the OC mailbox controls it exactly, and the CPU-side path is portable to boards
this has never seen. Writing a VR controller directly means knowing its register map and encoding
for that specific board, and a wrong register there puts an arbitrary voltage into the CPU with no
read-back that would catch it first.

That matters when the BIOS core voltage mode is **Override**: MSI then pins the VRM output at
the BIOS value and the CPU's VID request is ignored, so Dragon Power still works and any
CPU-side tool does nothing. Measured on a Z790MPOWER in Override mode, all four request paths
moved the VID (one by 125 mV) while the rail sat at 1.284 V +/- 1 mV. Switching the BIOS to
Adaptive fixes it completely — see below.

**The fix, confirmed on the bench:** set **CPU Core Voltage Mode** to `Adaptive Mode` and leave
**CPU Core Voltage** itself on `Auto`, then reboot — the mode only takes effect on the next boot.
On a Z790MPOWER that turns every path from dead to exact: an override moved the VID +100 mV and
the measured rail +98 mV, and the offset paths tracked to the millivolt. The VID-to-rail gap
falls from 55 mV (pinned) to 14 mV (ordinary load-line droop). An explicit value left in the
CPU Core Voltage field pins the VRM even with the mode set to Adaptive, which looks identical
to the Override case.

Until then Roch CPU will not pretend otherwise. Applying a core voltage measures the VID the
CPU now requests against the rail the Super I/O actually reports: if the request moved and the
rail did not, the row is marked **board ignored it**, the apply counts as failed, and the log
says why. It also reads the mailbox at start-up and warns in the header when the BIOS left the
core voltage in Override mode.

## Base clock and CPU VDD2

Both go through one interface: an **I2C mailbox in the embedded controller**. Several things worth
setting on these boards sit on a bus the EC owns rather than on the PCH SMBus, so no amount of work
on the SMBus controller reaches them. The EC will run a single transaction on that bus for you and
hand back the result, and that is the only route to those devices from software.

The register layout and handshake were obtained by watching the vendor tool's kernel driver, not by
reading its code (see the notices file). What was taken is an interface description — addresses and
a handshake, the same class of fact as a datasheet. **Neither encoding came from that trace**; both
were established by measuring how the board responded, and in the base clock's case reading it off
the trace gives the wrong answer entirely.

**Base clock.** The clock generator is a fractional-N PLL at I2C address `0xD2`: a fixed oscillator
of about 10002 MHz divided by `integer + fraction/2^28`. Both fields run *backwards* against
frequency, which is the trap — the vendor tool's recorded ramp steps its fraction down, and that
reads like an ordinary upward frequency ramp until it is anchored against a real measurement. It is
a divider. Measured across eleven points from 100.0 to 103.1 MHz the relation holds to a thousandth
of a MHz, and the oscillator is re-measured on each board rather than assumed.

Measuring the result matters as much as setting it, because **the obvious ways of reading base clock
are blind to a change**. The TSC runs from a fixed crystal and keeps reporting the boot-time value
for ever; APERF/MPERF cancels out, because MPERF scales with base clock exactly as APERF does. Only
counting unhalted core cycles against the ACPI timer, and dividing by the live multiplier, sees it.
`--bclk-test` prints all three side by side.

**CPU VDD2** is one byte on a regulator at `0x20`, a step count worth about 12 mV, measured against
the Super I/O's own reading of the rail. That reading lags a change by several hundred milliseconds,
which is worth knowing: sampling too early returns the old value and the reading after that shows
the *previous* step, which looks exactly like a step that did nothing.

**What keeps this safe.** A wrong value here does not produce a wrong reading, it stops the machine
— or, on a regulator whose byte reaches past 3 V, takes the memory controller with it. So:

* Base clock is capped at **102.5 MHz** and VDD2 clamped to **1.100–1.450 V**, checked against what
  was *measured* afterwards and not only against what was asked for.
* Nothing computes an absolute value from an assumed zero point. The current setting is read, the
  current result measured, and the target approached in small steps.
* Every step is measured — base clock against the ACPI timer, which does not move with it; VDD2 at
  the board. Anything that lands off target puts back the value found at start-up and stops.
* VDD2 may not move more than twenty steps from where it started, whatever the arithmetic says.

Both reset on reboot. Note that the value **0** restores puts back what the row held when Roch CPU
*started*, so if you raise the base clock, close the window and reopen it, that raised value is the
new starting point — reboot to get back to the BIOS setting.

## AMD Ryzen

On an AMD CPU the window swaps the Intel rows for the ones the SMU (the System Management
Unit, the firmware that runs Precision Boost) understands. Every one of them is a message to
the SMU mailbox, which is what Ryzen Master, ZenStates and the BIOS itself use, so it does not
care who made the board.

| Control | Path | Notes |
|---|---|---|
| PPT, TDC, EDC | RSMU `SetPPTLimit` / `SetTDCVDDLimit` / `SetEDCVDDLimit`, MP1 fallback; read back from the SMU power table when [PawnIO](https://pawnio.eu) is installed | PBO must be enabled in the BIOS or the SMU rejects the write (the log says *rejected: prerequisite not met*). Without PawnIO the rows start as *Auto*; see below. |
| Thermal limit (Tctl max) | RSMU `SetTctlMax` | the temperature the boost algorithm holds the CPU to |
| PBO scalar | RSMU `SetPBOScalar`, read back with `GetPBOScalar` | 1x to 10x |
| Curve Optimizer, all cores | RSMU / MP1 `SetAllDldoPsmMargin` | -30..+30 before Zen 4, -50..+50 from Zen 4 |
| Curve Optimizer, per core | `SetDldoPsmMargin` with the core's CCD / core address, read back with `GetDldoPsmMargin` | the **Curve Optimizer** button; cores are numbered the way the SMU (and the BIOS) number them, from the fuse map, so a 6-core CCD skips its two disabled positions |
| FMax | RSMU `GetBoostLimitFrequency` / `SetBoostLimitFrequencyAllCores` | the all-core boost ceiling; Zen 4 and later |
| DDR5 VDD / VDDQ / VPP per DIMM | the PMIC on each module over the FCH SMBus | same as Intel, see below |
| Temperature, VID, clocks, package power, SoC-side voltages, FCLK / UCLK / MCLK | SMN thermal block, SVI3 telemetry, HW P-state MSR, RAPL MSRs, SMU power table | shown by `--probe` only. The SoC rails and the memory clocks are BIOS settings the SMU has no message to change, so they are not offered as rows. The Super I/O rails shown on Intel are hidden on AMD, where the board's channel map is not known. |
| Base clock | measured by counting core cycles against the ACPI timer, using the P0 multiplier | read-only: the setting path is the MSI EC mailbox, which is an Intel-board feature here |

The SMU is reached through the SMN index/data pair in the north bridge's PCI configuration
space (D0F0 0x60 / 0x64) under the same `Global\Access_PCI` mutex HWiNFO, Ryzen Master and
ZenStates take, so Roch CPU can run beside them. The message numbers and mailbox addresses per
generation, the Curve Optimizer core addressing and the fuse map come from **Ivan Rusanov's
[ZenStates-Core](https://github.com/irusanov/ZenStates-Core)**, the library behind ZenStates
and SMUDebugTool; without his years of SMU work none of this would exist. Nothing was copied
(see the notices file), and the numbers this build was tested against are marked in the source.

**Reading the limits back needs PawnIO.** There is no SMU message that reports the PPT / TDC /
EDC limit currently in force. Every tool that shows it reads the *power table* the SMU
publishes in DRAM, and that needs a driver that can map arbitrary physical memory. The
WinRing0 build every monitoring tool ships (and this one bundles) was compiled without that
support: it answers *invalid parameter* for any address outside the BIOS ROM window. So Roch
CPU does what ZenStates does: if [PawnIO](https://pawnio.eu) (namazso's signed, sandboxed
driver, also used by LibreHardwareMonitor and HWiNFO) is installed, it loads the signed
`RyzenSMU` module shipped in `pawnio/` and reads the table through it, and the limit rows show
the real numbers, including what the BIOS set. Roch CPU never installs PawnIO itself. Without
it the log says so, the limit rows start as **Auto** (meaning: whatever the BIOS set) and show
the value you last wrote from here.

Three RSMU messages do answer without PawnIO, with the CPU's *stock* limits - on a Ryzen 7
9850X3D `0xD9` / `0xDB` / `0xDC` return 162 W / 120 A / 180 A, which are that part's PPT /
TDC / EDC - and writing 150 W then reading again still gives 162, so they are fuses, not
read-back. They are what **0** writes on those rows. (ZenStates labels the same three messages
*fused power / VDD TDC / SoC TDC*; the values on this CPU say otherwise.)

The table layout is not the same across generations. The Zen 5 (table 0x0062xxxx) offsets used
here were found by writing distinctive limits and watching which floats followed; after every
limit write Roch CPU checks the same thing again and switches the read-back off, saying so in
the log, if the float it expected did not move.

Two more things measured rather than assumed: the fused limits and the Set messages both use
milliwatts / milliamperes, and the HSMP mailbox is dead on desktop parts (it never becomes
ready), so it is only reachable from the raw `--smu hsmp` switch.

Supported: Matisse / Vermeer / Raphael / Granite Ridge and their Threadripper and EPYC
siblings, Zen / Zen+ with fewer rows, and the Renoir-to-Strix APUs with the message numbers
ZenStates uses for them (untested here). An unknown Zen generation is reported in the header
and nothing is written until the SMU answers the test message.

DDR5 VDD / VDDQ / VPP work on AMD the same way as on Intel: the PMIC on each DIMM, reached over
the FCH's PIIX4-compatible SMBus controller at I/O 0xB00 (`Hardware/SmbusPiix4.cs`), with the same
ADC calibration and the same refusal to write a rail whose register scale could not be confirmed.
The FCH can route that controller to several physical ports through a mux in its PM MMIO block;
switching it needs an MMIO write WinRing0 cannot do, so Roch CPU uses whichever port the BIOS
left selected. On the B850MPOWER the DIMMs are on it. The G.Skill kit there also showed that VDDQ
is not always the JEDEC 5 mV per step: its PMIC (vendor 8A12) uses 10 mV, which the calibration
now detects for VDDQ exactly as it did for VDD.

Diagnostics: `--probe` prints the topology with each core's SMU address, the SMU firmware
version, stock limits, scalar, boost limit and every core's Curve Optimizer value;
`--smu rsmu|mp1|hsmp 0xMSG [args]` sends one raw mailbox message and prints the six argument
registers (research only, it goes straight to the firmware); `--apply <row> <value>` applies
one row through the same path the window uses; `--pm-dump` dumps the whole power table (needs
PawnIO) and `--smn 0xADDR [count]` reads SMN registers, both for porting to a new firmware.

## Boards

Everything except the rail measurement is a CPU or Intel-chipset feature, so it does not care
who made the board: the ratios and voltages are model-specific registers and the Intel OC
mailbox, and the DDR5 rails are the JEDEC PMIC on each module reached over the PCH SMBus.

The one board-specific piece is the Super I/O that measures real Vcore, which is what lets an
apply be checked against the rail instead of trusting that the CPU's request was honoured:

| Super I/O family | Chips | Typically |
|---|---|---|
| Nuvoton, EC address space | NCT6683D, NCT6686D, NCT6687D | MSI 600/700-series |
| Nuvoton, banked registers | NCT6791D … NCT6799D | ASUS, some ASRock and Gigabyte |
| ITE | IT86xx, IT87xx | Gigabyte, some ASRock |

An unrecognised chip is not an error: the tool runs, the rail check is skipped, and the log
says which chip it found. Only the NCT6687D path has been exercised against real hardware;
the other two are written from the documented register layouts and are untested. `--sio-dump`
prints every channel if you want to check one.

## Requirements

* Windows 10/11 x64, run **as administrator** (the manifest asks for elevation).
* [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0), or build
  self-contained (below).
* `WinRing0x64.sys` beside the executable; the build copies it. This is the OpenLibSys
  WinRing0 1.2 driver used by OpenHardwareMonitor, Fan Control and others.
* On AMD, optionally [PawnIO](https://pawnio.eu) for PPT / TDC / EDC read-back (see the AMD
  section). Everything else works without it.
  * Windows **Memory Integrity (Core Isolation)** or the vulnerable-driver blocklist may
    refuse to load it. The window then shows a warning and every row reads N/A.
  * Defender sometimes flags WinRing0 as a *HackTool*. It is a plain MSR / port-I/O driver;
    the SHA-256 of the bundled file is
    `11BD2C9F9E2397C9A16E0990E4ED2CF0679498FE0FD418A3DFDAC60B5C160EE5`.
  * The service is created on demand and removed when Roch CPU exits. If a previous instance
    was killed, the next one adopts the leftover service and removes it on its own exit.

## Building

```bash
build.cmd
```

produces `dist\Roch CPU.exe` (framework-dependent). For a build that does not need the
.NET runtime installed:

```bash
build.cmd --self-contained
```

Or directly: `dotnet publish src/RochPower -c Release -o dist`.

### Self-test without the window

```bash
"dist\Roch CPU.exe" --probe report.txt
```

writes a report with every register the tool relies on (topology, turbo tables, mailbox
domains, PL1/PL2, BCLK, SMBus, DIMM PMIC registers, the ADC calibration; on AMD the SMU
firmware, stock limits, scalar, boost limit and Curve Optimizer values) plus no-op write
checks that rewrite the current values. Attach it when reporting a board that misbehaves.

`--vtest report.txt` goes one step further: it nudges the core voltage by 10–40 mV and the
ring ratio down by one, checks that the CPU follows (VID, ring ratio under load, package
power under an all-core load), and restores the exact previous values. Use it when a control
seems to do nothing.

`--vcore-test report.txt` is the sharpest one: it raises the core voltage request by 60 mV,
measures the rail the VRM actually produces, restores the previous setting, and states plainly
whether the board follows the CPU.

`--smbus-scan`, `--sio-dump` and `--sio-ldn` are the read-only probes for porting to a board with
a different sensor chip: devices answering on the SMBus, the Super I/O voltage channels, and the
Super I/O's logical devices with their I/O windows. `--clkgen` and `--clkgen-dump` do the same for
the clock generator, and `--vdd2` with no argument reports the VDD2 regulator without touching it.

`--help` lists every switch.

The EC dump and peek probes that used to be here are gone. They polled the EC as fast as the LPC
path allowed and hard-reset this machine twice: that chip also runs fan control and power
sequencing, and saturating it hangs the board.

## Using it

* Every row is one line: a name, the allowed range, and a typeable value in red. Type a value
  and press **Apply** (or Enter). The window sizes itself to hold every row, so nothing
  scrolls. Controls this system does not have are hidden rather than greyed out, which is why
  the E-core rows disappear when E-cores are off.
* **0** in any field restores the value captured when Roch CPU started; a voltage override
  goes back to *Auto* (adaptive). On AMD, PPT / TDC / EDC cannot be read at start-up, so **0**
  writes the CPU's stock limit instead, and **0** on a Curve Optimizer row sets every core to 0.
* **Revert** re-reads the hardware into the fields. **Reset** writes the start-up values back.
* **Per-Core Ratio Table** (Intel) opens the turbo table: the multiplier allowed for 1, 2, … N
  active P-cores and for each E-core group. **Curve Optimizer** (AMD) opens the per-core
  offset table, read back from the SMU.
* **Auto ratio step** (Intel) raises the CPU ratio by *step* every *interval* seconds until a
  write is rejected or you press Stop (F6). Run a stress test beside it to find the limit.
* **Log** opens the log window, which explains every failure: a locked MSR, a value the mailbox
  rejected, a PMIC in secure mode, an SMBus hidden by the BIOS, a driver Windows would not load.

The status line under the buttons says what the last Apply did.

## Safety

* Voltages above the usual daily-use range trigger a confirmation. Nothing stops you from
  confirming; you own the hardware.
* DDR5 PMIC register scales differ between kits: on overclocking kits the VDD register uses
  10 mV steps instead of the JEDEC 5 mV, which would double a voltage written with the wrong
  scale. Roch CPU measures every rail with the PMIC's own ADC at start-up, only enables
  writing on rails whose scale matches, and verifies each write by reading the register back
  **and** re-measuring with the ADC. A vendor-locked PMIC cannot be calibrated, so its rows
  stay read-only.
* Base clock and CPU VDD2 are the two controls where a wrong value stops the machine rather than
  showing a wrong number, so both are hard-capped, approached in small steps, and **measured after
  every step**, with the start-up value put back the moment one lands off target. See
  [Base clock and CPU VDD2](#base-clock-and-cpu-vdd2).
* Ratios and voltages set here are not persistent: a reboot returns to BIOS values.

## Layout

```
src/RochPower/Hardware   driver client (WinRing0), Intel MSR/CPU + OC mailbox, AMD CPU + SMU mailbox,
                         PCH SMBus, DDR5 PMIC, SMBIOS, BCLK meter, Super I/O,
                         EC I2C mailbox + clock generator + VDD2 regulator
src/RochPower/Core       settings model, hardware model (picks the Intel or AMD rows)
src/RochPower/UI         theme, main window, per-core ratio dialog (Intel), Curve Optimizer dialog (AMD), log window
drivers/                 WinRing0x64.sys (signed, extracted from LibreHardwareMonitorLib 0.9.4); pawnio/RyzenSMU.bin (signed PawnIO module, optional)
assets/                  the Roch mark, icon
```

`IKernelDriver` is the only thing the rest of the code talks to, so another ring-0 backend
(PawnIO, a vendor driver) can be dropped in without touching the UI.

## Validated on

MSI Z790MPOWER (BIOS P.90) with an i5-14600KF and a DDR5 kit at 1.470 / 1.410 / 1.800 V.
Every reading was cross-checked against MSI Dragon Power on the same machine. Four things came out
of that, all of them cases where the measurement disagreed with the documentation or with the
obvious reading and the measurement won:

* The OC mailbox's **SA voltage lives in domain 4** on Raptor Lake; published tables usually say 3.
* **E-core L2 is domain 5**, not 3. The rail exists and is settable even with the E-cores switched
  off in the BIOS, so that row is not gated on the core count.
* The **DDR5 VDD register scale** above: 10 mV per step on overclocking kits, not the JEDEC 5 mV.
* The **base clock is a divider**, so its register runs backwards against frequency — and both of
  the obvious ways to measure the result are blind to a change.

MSI B850MPOWER (BIOS 1.A21) with a Ryzen 7 9850X3D (Granite Ridge, SMU 0.98.83). The Curve
Optimizer read-back matched the values ZenStates showed on the same machine, the SMU accepted
every limit / scalar / boost-limit write and echoed it, and the stock-limit and power-table
findings above were measured there.
