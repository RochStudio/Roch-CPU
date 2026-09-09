# B850MPOWER BCLK investigation

## Result

Hardware capability exists, but a verified Roch CPU Windows write path has not
been established for the Ryzen 7 9850X3D / B850MPOWER combination. No live base-clock
adjustment was attempted. Keep this setting read-only rather than exposing an
unverified Intel register sequence on an AMD board.

## Evidence

- MSI describes this board's OC Engine as a precision clock generator supporting
  independent BCLK adjustment: https://www.msi.com/Motherboard/B850MPOWER
- MSI documents a BIOS PBO BCLK Booster for B850MPOWER:
  https://www.msi.com/index.php/blog/msi-pbo-bclk-booster-high-efficiency-mode-unleashing-ryzen-7-9800x3d-gaming-performance
  BIOS capability does not establish a Windows-accessible register protocol.
- The current `HardwareModel` initializes `BclkControl` only when an Intel `Cpu`
  instance and the supported EC mailbox/meter are available. AMD uses `Amd`, not
  that Intel CPU instance. `AddBclkRow` supplies no writer without this controller.
- `EcClockGen` explicitly describes a Raptor Lake board interface: EC mailbox,
  address 0xD2, unlock register 0xFD, frequency block 0xE0. Its apparently read-only
  block retrieval also performs an unlock write. This is not an appropriate blind
  probe on the AMD board.
- https://github.com/rafradek/zen-bclk-oc is a Linux kernel module whose stated
  compatibility is Zen 2/3. It is a research lead, not verified support for this
  Zen 5 system or the motherboard's independent external clock generator.

## Needed before implementation

Identify the board's actual clock-generator part and routing, establish the
documented or captured vendor read/write protocol, and establish trustworthy AMD
readback. Only then design a bounded write/restore test with an agreed target and
recovery procedure. Do not equate FMax, CPU multiplier, or an assumed 100 MHz
reference with measured BCLK control.

Version 1.0.2 changes version metadata only in this investigation; it does not
claim added BCLK support. UI alternatives were generated as preview images, not
implemented. No release or tag was created.
