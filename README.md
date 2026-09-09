<img src="assets/mark.png" alt="Roch" width="96">

# Roch CPU

A Windows CPU and DDR5 memory tuning tool for supported Intel LGA1700 and AMD Ryzen systems. Adjust clocks, power limits, voltages and Curve Optimizer in a compact light/dark interface. Available controls depend on your hardware and BIOS.

## Install

1. Download the package from [Releases](https://github.com/RochStudio/Roch-CPU/releases).
2. Extract the entire folder—keep the bundled driver and support files beside `Roch CPU.exe`.
3. Run `Roch CPU.exe` as administrator. Framework-dependent builds need the **.NET 8 Desktop Runtime (x64)**; self-contained builds do not.

Windows 10/11 x64 is required. If Windows blocks a driver, some controls will be unavailable.

## Build the latest source

Install the **.NET 8 SDK**, then run:

```bat
build.cmd --self-contained
```

Open `dist\Roch CPU.exe`. The current source version is **1.0.2**; it has not been released yet.

> Overclocking can cause crashes, data loss or hardware damage. Change one setting at a time and test stability. AMD BCLK writing is not enabled on the tested B850MPOWER.

[Hardware details](docs/reference.md) · [BCLK investigation](docs/b850mpower-bclk-investigation.md) · [License](LICENSE)
