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

## D2: the communication port (standalone, no agent)

`AuditorPortTool.exe` ships in the package (`package.ps1` builds and copies it). It is the user-mode half of
the port, independent of the C# agent. The port allows **one** connection, so the agent must be stopped for D2–D3.

**Verified on the dev machine:** the tool compiles `/W4 /WX`; `DriverProtocol.cs` (the agent's mirror of
`driver_protocol.h`) and the tool encode the same SET_SCOPE / GET_STATS bytes.
**Only a VM can show:** that the loaded driver accepts a scope and answers.

| # | Step | Expected |
|---|---|---|
| D2.1 | Load the driver (D1.2), then `AuditorPortTool.exe selftest` | All checks PASS, `0 failed`; a valid scope is accepted, a replacing scope reports the new folder count, an empty scope leaves 0 folders, malformed messages are rejected, and GET_STATS still answers |
| D2.2 | `AuditorPortTool.exe stats` | Prints `folders=0 mask=0x… sent=… dropped=…` without error |
| D2.3 | `AuditorPortTool.exe set D:\TestShare` then `stats` | `folders=1`; a bad path (`AuditorPortTool.exe set Q:\nope`) is refused in the tool before sending |

## D3: capture, standalone (driver → port, no agent)

Create a folder to watch (for example `D:\TestShare`) with a couple of files. Keep the agent stopped.

| # | Step | Expected |
|---|---|---|
| D3.1 | `AuditorPortTool.exe watch --seconds 180 D:\TestShare` in one window; in another, create `a.txt` | A `Created` record prints with the drive-letter path `D:\TestShare\a.txt`, the right pid and the caller's SID |
| D3.2 | Edit and save `a.txt` | One `Modified` (repeated writes inside the file coalesce to one per handle) |
| D3.3 | Rename `a.txt` to `b.txt` in the same folder | `Renamed` with both old and new path |
| D3.4 | Move `b.txt` to a sub-folder | `Moved` with both paths |
| D3.5 | Delete `b.txt` | `Deleted` |
| D3.6 | Right-click → Properties → Security, change permissions / owner / auditing | `PermissionChange` / `OwnerChange` / `AuditingChange` as applicable |
| D3.7 | Open a file you have no rights to | the matching op with `status` non-zero (denied) |
| D3.8 | Touch a file **outside** `D:\TestShare` | nothing prints (scope is enforced in the kernel) |
| D3.9 | `--direct` instead of recursive, then touch a file in a sub-folder | nothing prints (direct children only) |

If paths print as `\Device\HarddiskVolumeN\…` instead of `D:\…`, the NT→drive-letter map is the thing to check
(same translation the agent's `NtPathMap` does); capture itself is still correct.

## D4: full pipeline (driver → agent → backend → UI)

Stop `AuditorPortTool.exe` first (frees the single port connection). The backend, ClickHouse/Postgres and the
frontend can run on the dev machine or the VM; point the agent's `BackendUrl` at wherever the backend runs.

1. In the agent's `appsettings.json` (or environment), set `Agent:DriverSource` to `Real`, plus the usual
   `ApiKey`, `ServerId`, `TenantId`, `BackendUrl`. Leave `DriverPortName` empty.
2. Load the driver, start the agent. The log shows `connected to driver port \AuditorDriverPort`.
   (If the driver is not loaded yet, the agent logs `driver not reachable … retrying` and connects when it is.)

| # | Step | Expected |
|---|---|---|
| D4.1 | Register/approve the server and a share for `D:\TestShare` in the UI | agent shows Online, `Agents online 1` |
| D4.2 | Start a session scoped to `D:\TestShare` with the default operations | the agent logs `driver scope applied: 1 folder(s) …`; GET_STATS/`stats` would show that folder |
| D4.3 | Create / modify / rename / delete files in `D:\TestShare` | rows stream into the live session table within ~1 s, with drive-letter paths, resolved account and process |
| D4.4 | Touch files outside the scope | no rows (kernel-enforced, confirmed independently in D3.8) |
| D4.5 | Stop the session | driver scope cleared (`stats` → `folders=0`); the session completes and the CSV matches the table |
| D4.6 | Unload the driver while the agent runs | the agent logs the port dropping and retries; reload → it reconnects (an `AgentOffline`/recovery integrity event brackets the gap) |

**Known at build time, confirm on the VM:** the agent's real driver source (`RealDriverSource`,
`RealDriverControl`, `DriverConnection`, `NtPathMap`, `DriverProtocol`) was written and structurally checked but
never run against the live driver — D4 is its first execution. If the agent build flags anything, it is almost
certainly in these five files; the simulated path (`DriverSource=Simulated`) is unaffected and still runs the
rest of the stack.
