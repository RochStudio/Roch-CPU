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
Roch CPU installs it as a demand-start service and removes it again on exit.

## LibreHardwareMonitor

Register maps for the Super I/O hardware monitors (the Nuvoton NCT6683/6686/6687 EC address
space, the banked NCT679x parts, and the ITE IT86xx/87xx parts) follow
[LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor),
licensed **MPL-2.0**. No LibreHardwareMonitor code is included; the register layouts and the
access sequences were used as reference and reimplemented.

Where this project's measurements disagreed with that reference, the measurement won and the
difference is documented in the source — for example the CPU AUX channel on MSI's NCT6687D
needs the same x2 input divider as VDD2, which the generic map does not apply.

## MSI Dragon Power

Roch CPU is not derived from MSI Dragon Power and contains none of its code. It was written to
provide comparable functionality on boards other than MSI's, using interfaces documented by
Intel (model-specific registers, the overclocking mailbox) and by JEDEC (the DDR5 SPD hub and
PMIC over SMBus).

Dragon Power was used as a reference implementation for behaviour comparison on one MSI board,
which is how the DDR5 PMIC voltage scales and the OC mailbox domain numbering were validated.

## The Roch mark

The `assets/` artwork is shared with [Roch GPU](https://github.com/RochStudio/Roch-GPU) and
[Roch Viewer](https://github.com/RochStudio/Roch-Viewer).
