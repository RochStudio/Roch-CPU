# Third-party notices

## WinRing0 (`drivers/WinRing0x64.sys`)

Roch CPU needs ring-0 access for MSRs, port I/O and PCI configuration space. It uses the
OpenLibSys **WinRing0 1.2** kernel driver, which is bundled in `drivers/` and copied next to the
executable by the build.

* Copyright (C) 2007-2009 OpenLibSys.org, Noriyuki Miyazaki. Released under the BSD licence.
* The bundled binary was extracted from
  [LibreHardwareMonitorLib 0.9.4](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor),
  which shipped it as an embedded resource.
* SHA-256: `11BD2C9F9E2397C9A16E0990E4ED2CF0679498FE0FD418A3DFDAC60B5C160EE5`
* Authenticode: signed by Noriyuki MIYAZAKI, counter-signed by GlobalSign. The signing
  certificate expired in 2008; the countersignature is what keeps it loadable.

Notes for anyone auditing this: WinRing0 is a general-purpose low-level access driver, which is
why security software sometimes flags it, and why Windows Memory Integrity (Core Isolation) or
the Microsoft vulnerable-driver blocklist may refuse to load it. It contains no exploit code.
Roch CPU installs it as a demand-start service and removes it again on exit. This build of the
driver was compiled without `_PHYSICAL_MEMORY_SUPPORT`: its read-memory call only maps the
BIOS ROM window (0xC0000-0xFFFFF), which is why the AMD SMU power table cannot be read.

## LibreHardwareMonitor

Register maps for the Super I/O hardware monitors (the Nuvoton NCT6683/6686/6687 EC address
space, the banked NCT679x parts, and the ITE IT86xx/87xx parts) follow
[LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor),
licensed **MPL-2.0**. No LibreHardwareMonitor code is included; the register layouts and the
access sequences were used as reference and reimplemented.

Where this project's measurements disagreed with that reference, the measurement won and the
difference is documented in the source — for example the CPU AUX channel on MSI's NCT6687D
needs the same x2 input divider as VDD2, which the generic map does not apply.

## PawnIO and the RyzenSMU module (`drivers/pawnio/RyzenSMU.bin`)

On AMD, if the user has installed namazso's [PawnIO](https://pawnio.eu) driver, Roch CPU loads
the `RyzenSMU` module from [PawnIO.Modules](https://github.com/namazso/PawnIO.Modules) into it
to read the SMU power table, which WinRing0 cannot. The module is shipped as the signed binary
from the PawnIO.Modules 0.2.4 release (the same blob ZenStates-Core embeds), unmodified.

* Copyright (C) 2025 namazso. Licensed **LGPL-2.1-or-later**; the licence text is beside it.
* SHA-256: `B84ECA7F32C63B3D8C14B2C6D45482706DF8683AA6F43EB8BEAD9DC62181D38F`
* PawnIO itself is not bundled and is never installed by Roch CPU.

The client in `Hardware/PawnIo.cs` follows the ioctl layout of LibreHardwareMonitor's and
ZenStates-Core's PawnIO wrappers (32-byte function name followed by 64-bit arguments).

## ZenStates-Core, ZenStates, SMUDebugTool

The AMD path talks to the SMU the way Ivan Rusanov's
[ZenStates-Core](https://github.com/irusanov/ZenStates-Core) (GPL-3.0, the library behind
[ZenStates](https://github.com/irusanov/ZenStates) and
[SMUDebugTool](https://github.com/irusanov/SMUDebugTool)) does: the RSMU / MP1 / HSMP mailbox
addresses per generation, the message numbers, the core-address encoding for the Curve
Optimizer and the fuse registers that give the CCD and core map were taken from it as
reference and reimplemented in `Hardware/AmdSmu.cs` and `Hardware/AmdCpu.cs`. No ZenStates
code is included. Roch CPU is itself GPL-3.0, so the two are licence-compatible either way.

Where this project's measurements disagreed with the reference, the measurement won and the
difference is documented in the source: the three "fused limit" messages on Zen 4 / Zen 5
return the CPU's stock PPT / TDC / EDC rather than the power / VDD TDC / SoC TDC ZenStates names
them, and they do not track a written limit.

The SMN index/data access and the Global\Access_PCI mutex convention follow the
[ryzen_smu](https://gitlab.com/leogx9r/ryzen_smu) Linux driver and LibreHardwareMonitor.

## MSI Dragon Power

Roch CPU is not derived from MSI Dragon Power and contains none of its code. It was written to
provide comparable functionality on boards other than MSI's, using interfaces documented by
Intel (model-specific registers, the overclocking mailbox) and by JEDEC (the DDR5 SPD hub and
PMIC over SMBus).

Dragon Power was used as a reference implementation for behaviour comparison on one MSI board,
which is how the DDR5 PMIC voltage scales and the OC mailbox domain numbering were validated.

The base-clock path in `Hardware/EcClockGen.cs` was found by observing that tool's behaviour: its
kernel driver's exported entry points were forwarded through a logging proxy while it changed the
base clock, and the resulting port-level trace showed which registers of the board's embedded
controller carry a transaction to the clock generator on the controller's own I2C bus. What was
taken from this is an interface description - register addresses and a handshake - which is the
same class of fact as a datasheet, obtained by watching hardware rather than by reading or
translating any of the vendor's code; none of that code was disassembled and none of it is
included here. The frequency encoding itself was not taken from the observation at all: reading it
off the trace gives the wrong answer, and it was established by measuring the resulting clock, as
`EcClockGen` and `BclkController` document.

## The Roch mark

The `assets/` artwork is shared with [Roch GPU](https://github.com/RochStudio/Roch-GPU) and
[Roch Viewer](https://github.com/RochStudio/Roch-Viewer).
