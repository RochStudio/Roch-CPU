# ASUS Z790-A D4 validation — 2026-09-16

Current status: E-core changes are physically verified at 42x and the requested
44x. The ASUS NCT6798D monitor is now identified and supplies measured Vcore.
The 1.300 V mailbox request is retained, but its absolute measured voltage differs
from the request; the voltage measurements below must be considered separately
from successful register checks. Earlier sections are a chronological record,
including restrictions that applied before the diagnostic boot and BIOS change.

Hardware: Intel Core i9-14900K, ASUS ROG STRIX Z790-A GAMING WIFI D4,
BIOS 3107, microcode 0x12F. Requested behavior: 55x P-core and 1.300 V
CPU core voltage; BIOS CPU voltage is Auto.

## Observed on this machine

- The released 1.0.3 app detected 8 P-cores, 16 E-cores and 32 threads.
- The P-core table read 55 in all eight slots; that alone does not prove live
  clocks follow the table.
- E-core ratio 43 read successfully, but reapplying 43 through 1.0.3 failed:
  `WRMSR 0x650 failed on CPU 0`.
- The per-thread audit returned zero data for all six voltage mailbox domains
  on both logical CPU 0 and logical CPU 16.
- CPUID and Windows both report a hypervisor. Windows reports
  `VirtualizationBasedSecurityStatus=2` and `SecurityServicesRunning={2}`:
  VBS and Memory Integrity are running.
- Super I/O Vcore measurement was unavailable. VID must not be described as
  measured CPU core voltage.

Hypervisor/BIOS filtering is a candidate explanation, not a confirmed diagnosis.
[Intel documents VBS compatibility constraints for XTU](https://www.intel.com/content/www/us/en/support/articles/000093813/processors/processor-utilities-and-programs.html).
[Microsoft documents the VBS status fields](https://learn.microsoft.com/en-us/windows/security/hardware-security/enable-virtualization-based-protection-of-code-integrity).
No Windows security, BIOS or boot settings were changed.

## Development build

1.0.4-dev checks mailbox write/readback, rejects ignored requests, attempts
rollback on verification failure, verifies turbo/ring/power register writes,
uses an E-core thread for E-core access, and reports reset failures. Invalid
numbers and fractional multipliers are rejected. Live VID is separate from
the programmed voltage target. A failed refresh cannot verify a write against
an old cached value.

15 regression checks passed using a simulated driver. The self-contained
Windows x64 build completed successfully in `dist`.

## Completed hardware validation

The initial attempt was blocked while the old elevated 1.0.3 process was open.
After the user closed it, the single-instance check passed and the controlled
test ran at 17:26:22 on 2026-09-16. Results:

- P-core: baseline table 55x, observed active peak 54x. Lowering the ceiling to
  52x produced a 52x register value and a 52x active peak. This demonstrates
  that P-core ceiling changes affect actual clocks. It does not guarantee a
  sustained 55x clock under every workload.
- P-core baseline restored: all eight entries read back as 55.
- E-core: existing-value write still failed on logical CPU 16. Changing the
  access thread did not resolve this machine's restriction.
- CPU voltage: 1.300 V encoded as `0x00153300`, but readback was `0x00000000`.
  The patched app correctly rejected the request instead of reporting success.
- Original core mailbox restored and verified as adaptive, target 0, offset 0.

VBS/Memory Integrity was still running after the test. BIOS restrictions and
hypervisor filtering have not been distinguished; no claim is made that either
is the confirmed cause. CPU voltage and E-core adjustment remain unresolved.

`dist\Roch CPU.exe --intel-validate 1.3` briefly
lowers the P-core ceiling and checks live clocks, checks an existing-value
E-core write, verifies the 1.300 V mailbox target, and restores snapshots.
Its report is `dist\intel-validation.txt`. This is not an overclock stability
test and cannot verify physical Vcore without an independent voltage reading.

The tool must not be called fully fixed until the remaining board/firmware or
virtualization restrictions are resolved and retested.

## Follow-up ring / E-core / voltage work

The user confirms P-core changes now work and reports that all controls work on
their MSI Z790MPOWER. Preserve that board's populated OC-mailbox path.

The next candidate skips the ring VF write only when the entire ring domain reads
zero. Previously every ring change required a mailbox write and rolled back the
architectural ring register when the empty ASUS mailbox ignored that write.
This corrects the unconditional mailbox dependency; physical ring response on the
ASUS board still requires `--ring-validate` with the existing app closed.

The candidate adds live E-core/ring readings and the underlying driver error to
register-write failures. E-core and CPU voltage remain unresolved. Read-only calls
to `ASUSManagement.asio_hw_fun15` failed with WMI "Invalid object" for both
`AsusMbSwInterface_0` and `ATK_0`; no ASUS WMI write methods were invoked.

18 simulated regression checks cover empty ring domains, populated MSI-style
domains (including an automatic ratio with a voltage target), and rollback on a
rejected mailbox write. The candidate is built in `.tools/candidate` while the
current `dist` executable is running. No additional hardware tuning was performed
during this follow-up while another Roch CPU instance remained open.

## Ring hardware test completed — 18:15:29

After the old app closed, the candidate was copied to `dist` and the single-instance
ring diagnostic passed. Original MSR 0x620 was `0x0832` (maximum 50, minimum 8),
with an observed active ring peak of 45. Temporarily lowering the maximum to 43
read back as `0x082B` and produced an active ring peak of 43. The original
`0x0832` register was restored and verified before opening the updated GUI.
This demonstrates that the architectural ring ceiling controls the live ring on
this ASUS board even when its OC mailbox returns an empty domain. It does not
guarantee a sustained ring clock at an increased ceiling. E-core and core-voltage
adjustment remain unresolved. Report: `dist/ring-validation.txt`.

## Voltage restriction confirmed — 18:18–18:25

The user confirms ring control works and now requests E-core 44x and CPU core
1.300 V. P-core and ring behavior must remain intact; MSI Z790MPOWER compatibility
is still required.

Windows event 12550 in `Microsoft-Windows-Hyper-V-Hypervisor-Operational`, dated
16:58:10 during the current boot (started 16:51:10), explicitly records:

```text
Msr=0x150; IsWrite=1; MsrValue=0x8000001000000000;
AccessStatus=6; ImageName=WinRing0x64.sys
```

This confirms Hyper-V restricted the application's voltage-mailbox read command
(mailbox reads require an MSR write to submit the command). Earlier speculation
about the mailbox now has direct Windows evidence. A separate event restricts
HWiNFO's read of MSR 0x607, so that VR mailbox is not an established alternative.
There is no corresponding 0x650 event; do not claim the E-core cause is confirmed.

The candidate reads these structured events from the current boot, exposes the
specific restriction, and disables voltage controls only when the relevant write
restriction is recorded. P-core/ring code is unchanged. The detector was verified
against the real log with `--windows-audit`; 22 regression checks pass, including
stale-event and unrelated-register cases.

A diagnostic boot plan was prepared but NOT applied in
`.tools/prepare-diagnostic-boot.ps1` (default is plan-only). It backs up BCD, copies
the normal Windows entry, sets Hyper-V/VSM off only for that copy, selects it for
one restart, and keeps the normal default entry. It never initiates a restart.
This needs explicit user approval because Memory Integrity and Hyper-V-dependent
services would be inactive during that boot. Read-only readiness checks found
the OS volume fully decrypted and BitLocker protection off. No boot, BIOS, or
Windows security settings have been changed.

The prepared `--ecore-validate 44` test remains pending while the old app is open.
Normal window-close automation was ineffective on the elevated app; the user
was asked to close it before the single-instance hardware test.

## One-time diagnostic boot configured — 18:36:26

The user explicitly approved: "do the one-time diagnostic boot". The prepared
script was applied with administrator rights and completed successfully. No
restart was initiated. BCD was exported to
`.tools/boot-backup-20260916-183626/BCD.backup` (32768 bytes).

- Diagnostic entry: `{654b2a8c-b134-11f1-a3d9-ec27aace7365}`.
- Verified `hypervisorlaunchtype Off` and `vsmlaunchtype Off` in that entry.
- Boot manager `bootsequence` selects that entry for the next restart only.
- Normal default and display order remain
  `{654b2a88-b134-11f1-a3d9-ec27aace7365}`, unchanged from the snapshot.
- Snapshot files and the entry ID are in the backup directory; execution report
  is `.tools/diagnostic-boot-result.txt`.

After the user restarts: first verify CPUID hypervisor presence and Windows VBS
status, then run the prepared E-core 44x and 1.300 V validation with only one Roch
CPU instance. The actual behavior after reboot has not been tested yet. A later
restart should use the original default entry and its normal virtualization
configuration. Remove the created diagnostic entry after testing/returning to
the normal boot; do not modify or delete the original default entry.

## Diagnostic restart result — 18:42:55

The user returned after restarting. Windows reports boot time 18:40:10 and the
current entry is `Roch CPU diagnostic - Hyper-V off`, with both launch options
still `Off`. The one-time `bootsequence` has been consumed; the normal default
entry remains selected for the next restart.

However, `Win32_ComputerSystem.HypervisorPresent=True`, Device Guard reports
`VirtualizationBasedSecurityStatus=2`, and `SecurityServicesRunning={2}`. Thus the
configured diagnostic entry did not deactivate Hyper-V/Memory Integrity on this
Windows installation. Do not treat this boot as a successful test without VBS.
The HVCI registry setting remains `Enabled=1`; no `Locked` or `Mandatory` value
was returned by the inspected HVCI key. The exact cause of the boot options being
ineffective has not been established. Do not remove code-integrity policy files
or change registry/security settings based only on their presence.

The 44x E-core validation launch is awaiting the Windows UAC prompt (consent.exe
is present, Roch CPU has not started). No hardware result is available yet and
no further security changes have been made. Read-only verification report:
`.tools/diagnostic-boot-active.txt`.

## User disabled Memory Integrity; diagnostic retry prepared

The user said they disabled Memory Integrity. Readback confirms its registry
`Enabled=0`, but the current 18:40:10 boot still reports HypervisorPresent=True,
VBS status 2, and security service {2}. A restart is needed for the user's change.
The previous E-core test never launched: its UAC operation was canceled, and no
E-core validation report exists. Do not report that test as run or failed.

To retry the approved diagnostic with the user's change applied, the existing
diagnostic entry was reselected in `bootsequence`. Both launch settings are Off;
normal default/displayorder remain unchanged. Readback is in
`.tools/diagnostic-boot-rearmed.txt`. No restart was initiated and no registry
setting was changed by the assistant. The user's own Memory Integrity toggle
change persists independently of the one-time boot; do not claim that selecting
the normal boot will automatically reverse that user change.

## Successful hardware validation — 19:34

After the second restart and recovery of the desktop tool runner, Windows reports
HypervisorPresent=False, VBS status 0, and no running security services. Boot time
is 19:28:51. The current hardware probe reports BIOS **3202** (05/07/2026), whereas
earlier probes reported 3107. Do not attribute every changed register value solely
to virtualization; the BIOS also differs from the earlier session.

The raw E-core register now reads `0x000000000000002C`: one populated ratio group
(44x), with seven unused zero entries. The app previously populated all eight
bytes. E-core writes now preserve the zero entries, and the per-core editor disables
unused groups. Fully populated tables still update all groups for MSI compatibility.

Hardware results with the corrected build:

- E-core table changed to 42x; active E-core peak was 42x.
- E-core table then changed to the requested 44x; active peak was 44x.
- Original sparse table restored and verified: `44,0,0,0,0,0,0,0`.
- P-core regression: lowered 54x to 52x, observed 52x, restored all entries to 54x.
- CPU voltage: 1.300 V override was retained by the real mailbox, with mode=override.
- Core mailbox restored to its starting adaptive setting, target approximately
  1.272 V, ratio 54, offset 0. Overall validation: 0 failed checks.
- Super I/O remains unidentified, so physical Vcore measurement is unavailable.

24 simulated regression checks pass, including sparse ASUS and populated MSI-style
E-core tables. Release publish and diff whitespace checks pass. Reports are
`dist/ecore-validation.txt`, `dist/intel-validation.txt`, and `dist/probe.txt`.
These are brief functional tests, not sustained overclock-stability tests.

## ASUS voltage monitor and measured response — 19:48

The actual Super I/O chip ID is `0xD42B`, NCT6798D. The old `0xD428` mapping
prevented detection. The corrected build identifies it at LPC 0x2E, HWM 0x0290,
verifies vendor ID 0x5CA3 and reads Vcore on VIN0 with 8 mV resolution. Detection
and indexed reads share the ISA-bus mutex. MSI EC channel mapping and its separate
regulator override path are preserved.

The brief first measured test recorded:

| Phase | Programmed core target | Measured Vcore |
| --- | --- | --- |
| Before | Adaptive 1.272 V | 1.136–1.200 V |
| Override | 1.300 V | 1.224 V |
| Restored | Adaptive 1.272 V | 1.200–1.208 V |

VID remained around 1.31–1.37 V and is not the Vcore measurement. These readings
show a response associated with the override but do not establish an exact
1.300 V physical rail. Original P-core and mailbox settings were verified restored.
The validator now allows one second to settle and samples for two more seconds
at 200 ms intervals. It reports mean, range and sample count for all three phases.
The GUI's inconclusive-rail message no longer asserts an unsupported fixed-VRM
diagnosis, and explicitly states that the programmed target remains changed.

### Settled repeat — 19:52:29

The repeat completed with exit code 0 and zero failed register/clock checks:

| Phase | Vcore range and mean | Samples |
| --- | --- | --- |
| Before override | 1.200 V | 11 |
| 1.300 V override | 1.224 V | 10 |
| After restoring adaptive 1.272 V | 1.200 V | 11 |

The measured rail rose 24 mV and returned exactly to its measured baseline,
demonstrating a repeatable physical response to the approximately 28 mV programmed
target change. This does not establish that the physical rail equals the absolute
target: the reported rail remains 76 mV below 1.300 V. No compensating voltage
increase was attempted. P-core 54x and the original core mailbox were restored and
verified; E-core remains at its starting 44x. The corrected executable is in `dist`.

## Voltage and 101 MHz BCLK investigation — after 19:55

The user confirms P-core, E-core and ring work, rejects voltage as complete, and
requests BCLK control at **101 MHz**. Continue investigating the physical 1.300 V
target; a 24 mV response does not fulfill that request.

ASUS's Z790 BIOS manual distinguishes `Actual VRM Core Voltage` (external regulator
output) from `Global Core SVID Voltage` (the cores' request, influenced by the former).
The current ASUS path only writes Intel's request. MSI's separate regulator writer
must not be reused on an unidentified ASUS regulator.

The official per-model ASUS driver API lists AI Suite 3 3.03.44:
https://dlcdnets.asus.com/pub/ASUS/mb/14Utilities/AI_Suite_3_v3.03.44/AISuite3_DIP_SystemInformation_EzUpdate_v3.03.44.zip

The archive was downloaded and extracted for static inspection; AI Suite was not
installed. DIP5 2.03.57 includes `CCT/Z590/cctWin.exe`, signed by ASUSTeK with a valid
Authenticode signature. Its documented `-gc` branch reads the clock settings;
`-sc:...` is a separate setter. Automatic approval review initially rejected
execution. After static inspection and the user's explicit approval of `-gc`, the
query ran both normally and as administrator. The elevated report at 20:09:07 says
`Administrator: True` and `Base clock setting is not supported.` Its exit code is
zero despite the unsupported result. No `-sc` command or BCLK write was attempted.
This is a result for that vendor interface, not proof that BIOS BCLK adjustment
is impossible.

Windows' read-only firmware API returned the current DSDT (656885 bytes, valid
checksum). ACPICA disassembly shows ASUS `RSMB`, `WSMB`, `RSMW`, `WSMW`, `RSMK`, and
`WSMK` are empty stubs returning zero. Their presence in WMI is not a working SMBus
transport. No firmware method or SMI was invoked during this inspection.

Static inspection of ASUS's COM type library identifies `atkexCom.axdata`, the
`IAtkItemManager` interface, named item metadata, and separate get/set item methods.
At 20:14:59 its COM registration, `asComSvc`, `Asusgio3`, and `AsusCertService` are all
absent. A read-only capability probe is prepared in `.tools/asus-vendor-read.ps1`;
with the missing components it exits without invoking vendor code. The user has
been asked to approve installing these signed vendor components for further
capability checks. No driver or service has been installed at this checkpoint.

Research artifacts are in ignored `.tools`: `asus-cct-admin-read.txt`,
`asus-typelib.txt`, `asus-vendor-read.txt`, `acpi/DSDT.dsl`, and extracted vendor
packages. No source write path has been added for ASUS voltage or BCLK yet.

## Approved ASUS service installation and validation (20:21–20:49)

The user approved installing the signed ASUS components. `Asusgio3`,
`AsusCertService`, and 32-bit `asComSvc` (AXSP 4.03.12) are now installed and running.
Full AI Suite and fan-control utilities were not installed. `atkexCom.axdata`
capability and initialization both return 1. No restart was required.

An initial MBIF query incorrectly supplied an empty output buffer and crashed
asComSvc. The vendor adapter requires a preallocated byte SAFEARRAY. Normal COM
activation restarted the service; all subsequent buffer calls allocate 4096 bytes.
The corrected MBIF and voltage descriptor calls succeed. Research scripts retain
the initial failure and corrected results.

Group 3 exposes these distinct controls:

* `0x03010011`: BCLK Frequency. Minimum encoding `0xFF009C40`, increment 10,
  49801 entries, default index 6000. Nominal MHz = `(40000 + index*10)/1000`.
* `0x03020027`: Actual VRM Core Voltage. Current index 127, signed 24-bit minimum
  -635, increment 5, 255 entries: the current interface exposes a zero-offset
  regulator range, not an absolute manual-voltage range.
* `0x030D0012`: Global Core SVID Voltage. Extended descriptors obtained by
  `iAcpiGetItemBufferRef(id, 1, buffer, size)` expose offset, target, and mode.

The 92-byte extended SVID descriptor has a header, three seven-DWORD records and
an all-ones terminator. Each record contains ID, current index, flags, default
index, minimum, increment, and entries. Do not confuse current and default values.
On this board the current state is offset index 999 (0 mV), target index 1022
(1.272 V), and mode 0 (adaptive). The absolute-target minimum is 250 mV and step
1 mV. Mode 1 is Manual. The setter buffer is three ID/index pairs followed by
`0xFFFFFFFF`, inside the same 4096-byte preallocated array. This matches the
vendor DLL's extended-voltage setter (DIP4TurboVEVOAction 0x421460).

At 20:43, using the ASUS service with no direct Intel mailbox writes:

* 1.275 V Manual request read back completely; settled ASUS Vcore ~1.323 V.
* 1.300 V Manual request read back completely; settled ASUS Vcore ~1.341 V.
* The original mode, offset and target were restored and verified. Adaptive idle
  Vcore returned to about 0.755–0.764 V, with transient load-related increases.

This demonstrates a rail response to the vendor's Manual SVID path, **not an
exact 1.300 V measured output**. The ASUS HWM sensor (`0x06020011`) reports mV and
has different calibration from the previous raw Nuvoton 8 mV conversion. The app
uses ASUS's reading when the service is available; MSI retains its existing path.
Do not compensate the target upward or downward automatically to force a match.

At 20:41, BCLK started at ASUS nominal 100.250 MHz, with core-cycle samples around
99.93 MHz. A 100.750 MHz scalar request was acknowledged and read back but samples
stayed around 99.87 MHz. The diagnostic rejected this lack of physical movement
and restored index 6025 (100.250 MHz). It did not proceed to 101 MHz. The guarded
diagnostic remains available, but production ASUS BCLK is read-only.

Static inspection shows ASUS also has a separate physical clock backend with
GetClock/SetClock/IsSupported. Its official Z590 `cctWin.exe -gc` was rechecked
as administrator after component installation at 20:49 and still reports
`Base clock setting is not supported.` The ME interface and WMI provider both
have Windows device status OK, error code 0. No blind ICC setter or BIOS change
was attempted. The presence of writable ATK metadata is not evidence of live
clock control.

New code: `AsusBoardControl` uses validated descriptors, complete voltage snapshots,
readback and rollback; `AsusAudit` exercises the app Apply path and restores state.
Hardware evidence: ignored `.tools/asus-svid-validation.txt`,
`.tools/candidate/asus-audit.txt`, `.tools/asus-cct-admin-read.txt`.

The completed C# app Apply path subsequently passed both Manual targets and
complete-state restoration (`.tools/candidate/asus-voltage-validation.txt`).
At 1.300 V, ASUS telemetry settled near 1.341 V, with a 1.376 V transient sample.
The app reports retained target and measured Vcore separately and does not claim
that target readback establishes an exact rail voltage. All 30 regression checks
passed, including MSI ratio cases, malformed ASUS descriptors, correct SVID pair
encoding, bounded BCLK steps, rejected measurements, and physical-change rollback.

## Voltage clarification and live BCLK apply — September 17

The user confirms voltage control responds: 1.250 V requested in Roch CPU lowered
HWiNFO Vcore to about 1.300 V (the supplied screenshot shows 1.296 V). BIOS settings
are Auto with only XMP enabled; the earlier suggestion that LLC/SVID were manually
changed was corrected by the user. The approximately 46 mV difference in that
screenshot is not established as a fixed calibration error. Preserve the user's
current 1.250 V request; do not restore an older diagnostic baseline.

The ASUS core row now says `CPU Core target`, with a live measured Vcore line and
difference relative to the applied target. The editable value remains the actual
ASUS SVID request. The display does not subtract a guessed correction from sensors
or silently compensate the requested voltage. MSI controls are unchanged.

Static inspection of the official package's main `dip4.dll` found normal-item live
Apply at VA 0x5235F8 using setter option 2; its staging helper at 0x52369C uses 1.
BCLK's factory at 0x59AC10 selects that normal implementation for this board's
0x08000000 flags. The diagnostic now uses option 2, but this is **not a BCLK fix**.

At 07:07, the app was gracefully closed and the guarded 101 MHz diagnostic ran:

| Phase | ASUS nominal target | Median core-cycle measurement |
| --- | --- | --- |
| Baseline | 100.250 MHz | 100.016 MHz |
| Before first step | 100.250 MHz | 100.014 MHz |
| First step, option 2 | 100.750 MHz | 100.029 MHz |
| Restored | 100.250 MHz | 100.007 MHz |

The first step failed physical verification and restored the starting target.
101 MHz was not reached. Evidence: `.tools/asus-live-clock-launch.txt` and
`.tools/candidate/asus-audit.txt`; the prior option-1 report is preserved in
`.tools/asus-bclk-staging-test.txt`. CPU voltage was not modified by this test.
ASUS BCLK stays read-only in the production UI.

Public Intel processor documentation describes an integrated CPU BCLK PLL, so the
older PCH ICC utility's unsupported response does not establish that all clock
paths are unavailable. Public EDK2/Slim Bootloader header research did not establish
a usable runtime setter for this CPU. No speculative mailbox commands, PLL writes,
firmware changes or security changes were made. A verified physical control path
is still required before enabling ASUS BCLK editing.

Validation: all 30 regression checks passed after the display change, including
MSI ratio compatibility and ASUS physical-clock mismatch rollback.

## Physical voltage correction — September 17, 07:31–07:37

The user reported a continued approximately 50 mV excess at a 1.300 V request.
The read-only audit at 07:31 found ASUS SVID indices `(999, 1050, 1)` and Intel
mailbox domain 0 raw `0x00153336`: 1.300 V override, zero offset, ratio 54 on both
P- and E-core threads. Thus the voltage target conversion reaches the CPU correctly.
Cache SVID remained adaptive, and Actual VRM Core Voltage index 127 meant zero
regulator offset. The ASUS sensor read 1.359 V. No hypervisor was present.

The separate regulator item `0x03020027` uses a signed range -635 to +635 mV,
5 mV increments, index 127 = zero. Setter option 2 is the vendor's live scalar
apply path. A guarded hardware test with the SVID target unchanged at 1.300 V gave:

| Regulator offset | Median measured ASUS Vcore |
| --- | --- |
| 0 mV | 1.341 V |
| -25 mV | 1.323 V |
| -50 mV | 1.296 V |
| Restored 0 mV | 1.341 V |

This establishes that the regulator offset can correct the observed excess on
this configuration. It does not establish an exact constant error across loads,
different voltage targets, or different BIOS settings.

The app now exposes `CPU VRM offset` separately from `CPU Core target` and the
adaptive SVID offset. Metadata must match before enabling this control. Writes
validate 5 mV steps, read back the setting and restore the original index on error.
Zero explicitly clears this offset; Reset restores its startup value. MSI paths
and existing zero/default behavior for other controls are preserved.

At 07:37, the compiled app's `HardwareModel.Apply` path repeated both downward
steps successfully. The -50 mV phase had median 1.296 V, settled samples
1.296–1.305 V, and an initial 1.323 V transient. The CPU request remained exactly
1.300 V with no SVID offset. **The verified -50 mV regulator setting was retained**
to fulfill the requested correction. It stays active when the CPU SVID request
changes; it is visible and can be cleared by entering zero. No hidden correction
was added to the voltage target or telemetry. This was not a stress/stability test.

Evidence: `.tools/voltage-gap-read.txt`, `.tools/voltage-gap-intel.txt`,
`.tools/asus-vrm-offset-test.txt`, `.tools/candidate/asus-vrm-correction.txt`.
All 35 regression checks pass, including regulator rollback, ignored writes,
invalid steps, explicit zero clearing, and startup-offset restoration.

## Regulator control removed; SA and L2 verified — September 17, 08:28–08:36

The user accepts the CPU core voltage delta and requested removal of the CPU VRM
offset control. The previously retained -50 mV correction was explicitly cleared
using the existing app before removing the code. At 08:28, the vendor read back
regulator index 127 (0 mV). The row, setter helpers, dedicated commissioning
command and its zero-value behavior were removed. Core voltage still uses the
unchanged ASUS SVID target and displays measured Vcore separately.

SA and L2 expose dedicated extended descriptors:

| Control | ASUS item | Target index base | Offset descriptor | Sensor |
| --- | --- | --- | --- | --- |
| System Agent | 0x030D0022 | 700 mV, 1 mV steps, 1101 entries | minimum 0, 1000 entries | 0x06020091 |
| E-core L2 | 0x030D0026 | 250 mV, 1 mV steps, 1671 entries | minimum -999, 1999 entries | 0x0602008F |

SA must not reuse the CPU-core target base of 250 mV. Both setters preserve their
existing offset, write the vendor's ID/index pairs for Manual mode and target,
verify all three state fields, and roll back on a failed transaction. Descriptors
must match the selected rail. ASUS target rows use these board controls, while
MSI and other boards retain their original mailbox implementation.

The user had not specified rail targets when the initial validation ran. The
assistant stated the temporary 1.100 V assumption and restored each starting
state after testing through the compiled app's Apply method:

| Rail | Before median | Requested | After median | Restored state |
| --- | --- | --- | --- | --- |
| SA | 0.913 V | 1.100 V | 1.089 V | Auto, offset 0, target index 0 |
| E-core L2 | 0.920 V | 1.100 V | 1.104 V | Auto, offset index 999, target index 0 |

Both full-state readback and physical sensor response passed. No stress test was
performed. Logs: `.tools/asus-rails-prepare.txt`,
`.tools/asus-rails-descriptors.json`, `.tools/candidate/asus-sa-validation.txt`,
`.tools/candidate/asus-l2-validation.txt`. All 33 current regression checks pass,
including SA-specific encoding and rejection of a mismatched L2 descriptor.

BCLK remains unresolved. Further static tracing of `dip4.dll` 0x5551F8 and
0x555244 confirmed their alternate dispatch branches concern core-ratio controls,
not BCLK. The board's ACPI RMTW interface routes additional BIOS-option methods
through SMI; the disassembled AML does not reveal a validated runtime clock payload.
No speculative SMI command or repeated unsuccessful BCLK write was made. The
production BCLK row stays read-only until a physical control path is demonstrated.

## 2026-09-17: DDR4 rail labels and Cache SVID

The user confirmed SA and E-core L2 working and requested a ring-voltage check.
`BuildBoardRails` had bypassed `SuperIo.NamedRails` and displayed MSI EC channel 4
as VDD2 and channel 6 as AUX on the ASUS banked Nuvoton chip. Both row builders now
require a validated named channel. The ASUS profile has none, so both incorrect
rows disappear. MSI's validated EC mappings remain available. This fixes the
sensor naming error without claiming a DDR4 memory rail was physically detected.

ASUS Cache SVID is extended item `0x030D0028`. Its captured 92-byte descriptor
uses the core/L2 layout: target base 250 mV, 1 mV steps, 1671 entries; offset base
-999 mV, 1999 entries; Auto/Manual mode. The ring target now uses this board path,
validates the complete descriptor and preserves its offset. The setter restores
the previous mode, target and offset if full-state verification fails.

The compiled app's `--asus-ring-voltage-validate` test ran at 08:48:53. Starting
Cache SVID was Auto, offset index 999, target index 0. Existing core target was
1.300 V. Applying ring 1.300 V through `HardwareModel.Apply` produced Manual mode
and target index 1050, and the independent CPU OC mailbox domain 2 changed from
adaptive/0 V to override/1.300 V. Shared Vcore measured 1.341 V. Ring ratio stayed
50 max/8 min, and core, SA and L2 ASUS voltage states stayed unchanged. Finally,
the original complete cache state was restored and the CPU mailbox returned to
adaptive/0 V. Diagnostic exit code 0. The same run verified no VDD2/AUX rows were
present. Evidence: `.tools/candidate/asus-ring-voltage-validation.txt` and
`.tools/ring-validation-launch.txt`.

This verifies the programmed ring request, not an independent physical ring
supply or load stability. Intel identifies VCCCORE as the IA cores and ring power
rail in its [13th/14th-generation datasheet](https://cdrdv2-public.intel.com/743844/743844-015.pdf).
There is no separate ring sensor in the discovered ASUS sensor group. The UI
labels this value as a target and the log identifies the measured value as shared
Vcore. All 34 regression checks passed, including the captured Cache SVID layout.
