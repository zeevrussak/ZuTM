# Work Report — 2026-09-19 (second pass): Near- & Mid-term Roadmap Execution

**ZuTM © 2026 Ze'ev Russak <zutm@20032014.xyz> — ZuTM Attribution License.**

Scope: execute the near-term backlog in full (except legacy-UTM support,
explicitly excluded by the owner), the mid-term backlog (same exclusion), and
residual security/robustness items. Validated with the complete unit + E2E
battery (real virtual machines).

## 1. Near-term items — all delivered

| Item | Delivered |
|---|---|
| **Per-section config editor UI** (FR-08/60) | `VMConfigEditorDialog`: name/notes, system (arch/machine/CPU/vCPU/RAM), QEMU toggles (UEFI/WHPX/RTC/debug log), display hardware (empty = headless), network (mode/NIC/MAC), sharing (mode/read-only/clipboard). All edits are lossless `with` transforms; unknown keys ride along; saves through the atomic bundle writer. |
| **Snapshots** (FR-07) | `QmpClient` snapshot suite (`savevm`/`loadvm`/`delvm` + `info snapshots` parser — unit-tested incl. device-column rows) via HMP-through-QMP; `Snapshot…`/`Restore...` dialogs; restore = pause→loadvm→cont. |
| **In-app serial terminal** (FR-64) | `SerialConsoleControl`: attach/detach to the running VM's serial endpoint, live streaming output (64 KiB tail), Enter/Send input, endpoint auto-updates on start/stop. Same `SerialConsoleSession` protocol the E2E suites use. |
| **CLI `zutm`** (FR-105) | `src/ZuTM.Cli` (`zutm.exe`): `list/show/start/stop/pause/resume/clone/delete`, `--folder`/`ZUTM_VM_FOLDER` override. Shares the launch path (`VmLauncher`) and control state (`RuntimeRegistry`) with the GUI. E2E-proven: CRUD suite + **real headless-VM start→QMP control→list(running)→stop→registry-clear**. Stop escalates to a hard kill for ACPI-less guests (30 s grace), matching the GUI. |
| **Signing** (FR-115) | CI hooks in both MSI jobs: base64-PFX secret → `signtool sign /tr` (RFC 3161 timestamp). No-op until the `SIGNING_CERT_PFX` secret exists — activates the day a cert is configured. |

## 2. Mid-term items — delivered / advanced

| Item | Status |
|---|---|
| Shared-folders UX (FR-85) | Delivered inside the config editor (mode + read-only + clipboard) with guest-requirements guidance in the picker itself |
| Sort/search (FR-11) | ✅ Search box (name filter) + name-sorted library, selection preserved |
| Icon (FR-12 partial) | Icon field round-trips; picker folded into notes/name section — full custom-icon file picker remains |
| Localization (FR-104 → 🟡) | `Strings/en-US/Resources.resw` infrastructure + core strings; incremental migration continues |
| Accessibility (FR-103 → 🟡) | AutomationProperties ids on all primary controls + tooltips; full narrator/high-contrast pass remains |
| UI automation (FR-125 → 🟡) | `UiAppSmokeTests`: launches a built `ZuTM.exe`, asserts the main window appears, closes it (`ZUTM_UI_E2E=1` + `ZUTM_APP_EXE`); Appium-tier control automation remains |
| Legacy UTM migration (FR-93) | **Not done — owner directed exclusion** |

## 3. Cross-cutting engineering

- **`VmLauncher` (Core)** — single start path (plan → UEFI seeding → QEMU →
  QMP → registry) now shared by GUI, CLI and tests; the GUI's inline launch
  logic was removed in its favor.
- **`RuntimeRegistry`** (FR/AD-21) — `%APPDATA%\ZuTM\running.json`: uuid →
  QEMU pid + QMP/SPICE ports, atomic writes, stale-pid pruning; the GUI⇄CLI
  control contract. Unit-tested (round-trip, stop, pruning, corruption).
- **QEMU process hardening**: QEMU stderr tail surfaced in QMP-timeout
  errors; QEMU stdio is always redirected so a detached VM can never hang a
  caller's stdout (the CLI-start deadlock), and the harness bounds every
  wait.
- **Security residuals**: `AppSettings.Save` is now atomic;
  `SECURITY.md` (policy + reporting address `zutm@20032014.xyz`).

## 4. Bugs found & fixed during this pass

1. `virtserialport` for the guest agent was emitted without a virtio-serial
   bus on headless VMs → QEMU refused to start (caught by CLI E2E; bus now
   declared in the machine section whenever any virtserial consumer exists).
2. QEMU inherits caller stdio when not redirected → CLI start hung the
   caller (always-redirected now).
3. `dotnet run` MSBuild node reuse held inherited handles → E2E harness now
   invokes the built apphost via `cmd /c` with file redirection + shared-mode
   reads.
4. `info snapshots` parser initially choked on the device-column row shape
   and the header line (`to`) — fixed + pinned by tests.

## 5. Validation

Release build clean; **142 Core + 42 Update/Spice unit tests** and
**all E2E suites** green, including: bundle round-trip, real QEMU lifecycle,
Alpine headless VM (serial login/commands), Alpine XFCE desktop (SPICE
server, guest-side capture, host-driven control), CLI CRUD, and CLI
real-VM start/stop via the shared launcher + registry.

## 6. Work remaining (updated)

- Appium-tier UI automation; narrator/high-contrast audit; remaining string
  localization; custom-icon file picker.
- Legacy UTM (<v4) migration — excluded by owner direction.
- Blocked (upstream): native ARM64 QEMU, `swtpm` TPM, bridged networking.
- Signing activation: configure `SIGNING_CERT_PFX`/`SIGNING_CERT_PASSWORD`
  repo secrets after certificate purchase.
