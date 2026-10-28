# AuditorDriver: test plan for a separate VM

Everything below runs only in a disposable VM (snapshot it first). Nothing here was run on the development machine: the driver has never been loaded.
Each task lists what was verified at build time on the dev machine and what only a VM can show.

## 0. VM setup (once)

1. Windows 10 22H2 or Windows Server 2019/2022, x64, Hyper-V Gen 2 or VMware. Take a snapshot called `clean`.
2. Elevated PowerShell:
   ```
   bcdedit /set testsigning on
   shutdown /r /t 0
   ```
   After the reboot the desktop shows a "Test Mode" watermark. For Gen 2 VMs, turn Secure Boot off first (test signing is refused with it on).
3. On the dev machine, from `Backend/Driver`:
   ```
   pwsh scripts/package.ps1          # builds, stamps the INF, makes the catalog
   pwsh scripts/sign-test.ps1        # test-signs .sys and .cat, writes wfsa-test-signing.cer
   ```
   Copy the whole `build/package` folder into the VM (for example `C:\wfsa`).
4. In the VM, elevated:
   ```
   cd C:\wfsa
   .\install.ps1 -IUnderstandThisInstallsAKernelDriver
   ```
   It trusts the test certificate for this VM only, copies the driver and registers the service. It refuses on a machine that is not a VM or not in test-signing mode.
5. To undo: `.\uninstall.ps1 -IUnderstandThisRemovesAKernelDriver`, or revert to the `clean` snapshot.

## D1: load and unload

**Verified on the dev machine:** the driver compiles with `/W4 /WX` and no warnings; the image is x64, native subsystem, imports only `ntoskrnl.exe` and `fltmgr.sys`; `Inf2Cat` accepts the INF and builds the catalog with no errors or warnings.
**Only a VM can show ("Done when"):** that it loads and unloads.

| # | Step | Expected |
|---|---|---|
| D1.1 | `fltmc filters` before loading | `AuditorDriver` is not listed |
| D1.2 | `fltmc load AuditorDriver` | Exit code 0, no error text |
| D1.3 | `fltmc filters` | `AuditorDriver` listed, altitude `385250`, frame 0 |
| D1.4 | `fltmc instances` | An instance on each local volume (for example `C:`); none on a network or RAW volume |
| D1.5 | `fltmc unload AuditorDriver` | Exit code 0 |
| D1.6 | `fltmc filters` | `AuditorDriver` is gone |
| D1.7 | Repeat load, unload 20 times in a loop | No error, no leaked handle, no bugcheck |
| D1.8 | Load, then `shutdown /r` | VM reboots cleanly (a mandatory unload path) |
| D1.9 | Verifier: `verifier /standard /driver AuditorDriver.sys`, reboot, repeat D1.2 to D1.7 | No verifier violation; `verifier /query` shows no pool leaks after unload |

If D1.2 fails: read `%windir%\inf\setupapi.dev.log` (install) and `sc query AuditorDriver`. If Windows reports an invalid signature, check that the VM is in test-signing mode and that `wfsa-test-signing.cer` is in both `Root` and `TrustedPublisher`.
