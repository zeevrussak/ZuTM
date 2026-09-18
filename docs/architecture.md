# ZuTM Architecture

**ZuTM © 2026 Ze'ev Russak <zutm@20032014.xyz>** — ZuTM Attribution License
(copies and derived works must credit the author; see LICENSE.TXT / AGENTS.md).

## 1. What ZuTM is

A Windows 11 (x64 + ARM64) virtualization front-end in the spirit of
[UTM for macOS](https://github.com/utmapp/UTM): a native Fluent (WinUI 3)
application that drives **QEMU**, exposes **SPICE** consoles, uses
**hardware acceleration** where the platform allows, and is **file-format
compatible with UTM** — `.utm` bundles move between the two apps.

Clean-room boundary: no UTM source is included or derived from; the format
compatibility layer is specified in
[utm-compatibility.md](utm-compatibility.md) and implemented from that spec.

## 2. Component map

```
┌────────────────────────────────────────────────────────────┐
│ ZuTM.App (WinUI 3, net10.0-windows10.0.26100)              │
│  MainWindow ─ VmListView ─ VmDetailView ─ NewVmDialog      │
│  SettingsDialog (VM folder, updates, About/credits)        │
│  ViewModels: Main / VmItem / Update (hand-rolled MVVM)     │
│  Services: VmLibraryService, AppSettings                   │
└───────┬──────────────────┬──────────────────┬──────────────┘
        │                  │                  │
┌───────▼──────┐  ┌────────▼───────┐  ┌───────▼────────┐
│ ZuTM.Core    │  │ ZuTM.Spice     │  │ ZuTM.Update    │
│ Plist (XML + │  │ remote-viewer  │  │ semver, GitHub │
│  bplist00)   │  │ console host   │  │ Releases feed, │
│ UTM config + │  └────────────────┘  │ SHA-256 verify │
│  .utm bundles│                      │ msiexec handoff│
│ QEMU cmdline │                      └────────────────┘
│  + WHPX/TCG  │
│ QMP client,  │
│  VM process  │
└──────┬───────┘
       │ spawn
┌──────▼───────────────────────────────────────────┐
│ QEMU (GPLv2, separate program, bundled)          │
│  -accel whpx | tcg   -spice …   -qmp tcp …       │
└──────────────────────┬───────────────────────────┘
                       │ SPICE / TCP (loopback)
             remote-viewer (virt-viewer, GPLv2+)
```

## 3. Key flows

### 3.1 Starting a VM
1. `VmLibraryService.StartAsync` → `QemuRuntime.Discover()` (bundled
   `runtimes\qemu`, or `ZUTM_QEMU_ROOT`, or dev checkout).
2. `PortAllocator` reserves loopback TCP ports (SPICE, QMP, serial, guest agent).
3. `QemuCommandLineBuilder` maps the `UtmConfiguration` to argv (pure, unit-pinned).
4. `QemuVmProcess.StartAsync` spawns QEMU, streams stdout/stderr to the
   bundle's `Data\debug.log`, then connects `QmpClient` (retry while QEMU boots).
5. `SpiceConsoleLauncher` opens remote-viewer on the SPICE port.
6. Stop = `system_powerdown` via QMP → 30 s grace → `Kill(entireProcessTree)`.

### 3.2 UTM compatibility
- Bundle = directory `*.utm` with `config.plist` (XML plist) + `Data\`.
- Reader accepts `ConfigurationVersion` 4 (`Apple` backend → clear refusal).
- **Lossless rule:** every key ZuTM doesn't model is captured per-section and
   re-emitted verbatim; enum-ish fields are raw strings. Pinned by round-trip
   unit tests against a real-world UTM fixture.
- Host-only state (external drive paths, window placement) lives in
  `zutm-state.json` — UTM never sees it, and deleting it is harmless.

### 3.3 Acceleration matrix

| Host | Guest | Accelerator |
|---|---|---|
| x64 (WHPX feature on) | x86_64 / i386 | **WHPX** (hardware) |
| x64 | anything else | TCG JIT (multi-thread) |
| ARM64 (WHPX feature on) | aarch64 | **WHPX** (hardware) |
| ARM64 | anything else | TCG JIT |
| WHPX feature off | any | TCG |

`AcceleratorDetector` probes `WHvGetCapability(HypervisorPresent)` via
P/Invoke (arm64-safe, no x64-only imports).

### 3.4 Online updates
1. `UpdateChecker` → GitHub `releases/latest` (repo configurable).
2. Strict semver comparison (`SemanticVersion`, spec §11 precedence).
3. Asset `ZuTM-{x64|arm64}.msi` matching the running architecture.
4. `UpdateInstaller.DownloadVerifiedAsync` — streamed download + progress,
   SHA-256 (GitHub asset digest) + size verification, tamper ⇒ delete + refuse.
5. `msiexec /i` major-upgrade (in-place; old version uninstalled by MSI).
   Code signing (Authenticode) is the release pipeline's job.

## 4. Deployment

- **MSI per architecture** (WiX v6): `ZuTM-x64.msi`, `ZuTM-arm64.msi`;
  per-machine, embedded cabinets, `MajorUpgrade` with
  `AllowSameVersionUpgrades`; Start-menu shortcut; clean uninstall
  (`RemoveFolder` + standard MSI rollback). `scripts/build-installer.ps1`
  publishes self-contained + harvests files with `wix heat`.
- QEMU + remote-viewer bundle under `runtimes\` (GPL components shipped as
  separate programs — see NOTICE).
- CI (`.github/workflows/ci.yml`): unit tests (x64 Debug+Release), E2E with a
  real QEMU, both MSIs, and on `v*` tags a GitHub Release with SHA256SUMS —
  the feed the in-app updater reads.

## 5. Testing strategy

| Layer | Suite | Approach |
|---|---|---|
| Plist engine | `ZuTM.Core.Tests` | XML/binary round-trips, hand-assembled binary fixture, malformed inputs |
| UTM compat | `ZuTM.Core.Tests` | Real-world config.plist fixture; lossless round-trip incl. unknown keys + future enum values |
| QEMU cmdline | `ZuTM.Core.Tests` | Pure builder; per-feature argv assertions + Windows-divergence warnings |
| QMP | `ZuTM.Core.Tests` | In-process fake QMP server; handshake, ids, events, errors, concurrency, disconnects |
| Bundles | `ZuTM.Core.Tests` | Real temp-dir load/save/import/atomicity |
| Update | `ZuTM.Update.Tests` | Mock HTTP; semver precedence matrix; tamper/size rejection |
| E2E | `ZuTM.E2E` (`ZUTM_E2E=1`) | Real QEMU boot + QMP lifecycle; on-disk bundle round-trip |

## 6. Deliberate v0.1 limitations (roadmap)

- Legacy (pre-v4) UTM configs → migrate on load (UTM accepts after upgrading
  the bundle on a Mac once).
- TPM (needs swtpm on Windows), VirtFS caveats, bridged networking (no bundled
  bridge backend on Windows QEMU — falls back to shared NAT with a warning).
- Native ARM64 QEMU builds pending upstream; x64 build emulated on ARM64 hosts.
- In-app config editing of every section (model + save exist; full editors land
  with the settings-UI milestone), snapshots, `remote-viewer` HWND embedding
  inside the main window (today: external window).
