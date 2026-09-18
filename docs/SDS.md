# ZuTM — Software Design Specification (SDS)

**ZuTM © 2026 Ze'ev Russak <zutm@20032014.xyz> — ZuTM Attribution License.**

- **Companion document:** [SDR.md](SDR.md) — the requirements this design satisfies (FR-xx references below).
- **Normative details** live in sibling specs: [utm-compatibility.md](utm-compatibility.md) (file format) and [architecture.md](architecture.md) (component overview). This document records the *design decisions* and their rationale.

---

## 1. System context

```
            ┌────────────────────────────────────────────────┐
            │                ZuTM.App (WinUI 3)              │
            │  MainWindow · NewVm · Settings · (VM editors)  │
            ├──────────────┬─────────────────┬───────────────┤
            │ VmLibrarySvc │ UpdateViewModel │ AppSettings   │
            └──────┬───────┴────────┬────────┴───────────────┘
                   │ ZuTM.Core      │ ZuTM.Update / ZuTM.Spice
        ┌──────────▼─────────┐  ┌──▼──────────────┐
        │ plist · UTM model  │  │ semver · feed    │
        │ bundle I/O · args  │  │ SHA-256 verify   │
        │ QMP · serial · proc│  │ msiexec handoff  │
        └──────────┬─────────┘  └─────────────────┘
                   │ spawn / TCP(loopback)
        ┌──────────▼──────────────────────────────┐
        │ QEMU (GPLv2, separate program)          │
        │  WHPX | TCG · SPICE · QMP · GA · serial │
        └──────────┬──────────────────────────────┘
                   │ spice://127.0.0.1:<port>
        remote-viewer (virt-viewer, GPLv2+) — the console client
```

Design constants:

- **D-01 · Loopback-only control plane.** Every VM endpoint (SPICE, QMP, serial, guest agent) binds `127.0.0.1` with an ephemeral port allocated per launch (FR-118). Rationale: no remote exposure by default; no port collisions; ports are internal state, not configuration.
- **D-02 · The app never reimplements virtualization.** QEMU is a separate GPLv2 program discovered under `runtimes\qemu` (or `ZUTM_QEMU_ROOT`). This keeps licensing clean (NOTICE) and matches how UTM treats QEMU on macOS.
- **D-03 · Clean-room UTM compatibility.** The `.utm` format is implemented from documented behavior only; no UTM source is included (license incompatibility with the ZuTM Attribution License).

## 2. Component design

### 2.1 ZuTM.Core — `Plist/`

| Decision | Rationale |
|---|---|
| **AD-1 Own plist engine** (XML + bplist00 reader/writer, format sniffing) | Tiny dependency footprint; full control over ordering/atomicity; binary support needed to *read* third-party tools' plists. Verified against a hand-assembled binary fixture so writer/reader consistency can't mask format bugs. |
| Records are immutable (`record` + `init`) | VM configurations are snapshots; edits are explicit `with` transforms — matches the lossless-round-trip discipline (FR-92). |
| `PlistDictionary` ordered | plist dictionaries are unordered *semantically*; ordering preserved for byte-stable diffs. |

### 2.2 ZuTM.Core — `Utm/` (compatibility layer)

- **AD-2 Section models with `UnknownKeys`.** Every UTM section captures keys it does not model and re-emits them verbatim; enum-like fields are raw strings. This is the mechanism behind FR-90–92 and is pinned by round-trip tests against a real-world fixture *plus* injected future keys/values.
- **AD-3 Guest state lives in `zutm-state.json`, never `config.plist`.** UTM watches config.plist identity; host-only data (external image paths, window placement, last-run) is kept in a deletable sidecar. External-image paths replace macOS security bookmarks with Windows absolute paths keyed by drive `Identifier` (FR-45).
- **AD-4 Atomic config writes.** config.plist is staged then `File.Replace`d so a crash mid-save can never truncate the file a Mac will open next.
- **AD-5 Version gate = exactly 4.** `< 4` refused with "too old" (legacy migration is FR-93, deliberately low priority), `> 4` refused with "upgrade ZuTM" — mirrors UTM's own guard so behavior is predictable on both sides.

### 2.3 ZuTM.Core — `Qemu/`

| Decision | Rationale |
|---|---|
| **AD-6 Pure command-line builder** (no I/O; image resolution injected) | Deterministic argv from a configuration; every feature flag's argv is unit-pinned (FR-37/40-46/50-56/61-74). Warnings — never silent behavior changes — document Windows divergences (bridged fallback FR-54, VirtFS FR-83, TPM NR-04). |
| **AD-7 WHPX probe with test injection** (`whpxAvailable` parameter) | Detection is host-state; the *policy* (same-arch → WHPX, else TCG) is fully unit-testable on any host. Probe failures (missing/stubbed `WinHvPlatform`) degrade to TCG rather than crash (FR-21). |
| **AD-8 QMP client: single reader pump + id-keyed replies** | One task parses the newline-JSON stream; commands get monotonic ids; events fan out to a channel + event. The pump's completion fails any late-registered waiter — the fix for a class of hangs where a server dies between commands. Handshake retries tolerate QEMU's boot window. |
| **AD-9 Serial console session with split markers** (`echo ZDO""NE` vs `ZDONE`) | Completion can only be proven by real output — the echoed input line can never contain the verbatim marker. Paired with: ANSI DSR auto-reply (busybox ash blocks on `ESC[6n` otherwise), write serialization (`NetworkStream` writes are not thread-safe), bounded ring log, and a login resync. This driver is what makes the unattended installs and the VM E2E suites possible. |
| **AD-10 Direct qemu-guest-agent protocol client** | QEMU does **not** proxy `guest-*` over QMP; the `zutm-qga` chardev socket *is* the GA endpoint. Speaking newline-JSON to it directly (guest-ping/guest-exec) is the host-driven control path proven by the desktop E2E (FR-80). |
| **AD-11 VM lifetime lease** (`QemuVmProcess.Lease`) | Failure paths in installers/tests must not leak running QEMU processes; disposal is scoped even on exceptions. |
| **AD-12 Layout-agnostic runtime discovery** | QEMU Windows builds exist in both flat (weilnetz; **exes must stay at the root** or module loading breaks — QXL et al. silently vanish) and `bin\` layouts. `QemuRuntime` accepts both and walks ancestors so tests/tools find the repo runtime. |

### 2.4 ZuTM.App (WinUI 3)

- **AD-13 Hand-rolled MVVM** (ObservableObject/RelayCommand, ~100 LOC) instead of a toolkit package: the app's binding surface is small and this keeps the dependency tree minimal.
- **AD-14 Native Fluent, UTM-inspired layout.** Left VM list + right detail + wizard mirrors UTM's interaction model; all chrome is stock WinUI (Mica, system themes) — the "looks like Windows 11" requirement is met by *using* Windows 11's design language (FR-100/101).
- **AD-15 Services own QEMU, windows own marshaling.** `VmLibraryService` is UI-free; the window injects a dispatcher (`InvokeOnUiAsync`) so process-exit events land on the UI thread safely.

### 2.5 ZuTM.Update

| Decision | Rationale |
|---|---|
| **AD-16 Strict semver with spec §11 precedence** | Release tags must order correctly including pre-releases; a bespoke comparator WILL get rc-vs-release wrong. |
| **AD-17 Refuse unverifiable updates** | No digest on the release asset ⇒ no download (FR-114). Tampered/short files are deleted, never executed. |
| **AD-18 Updates ride MSI major-upgrade** | One install technology: the updater downloads + verifies, then hands to `msiexec /i` — uninstall/rollback semantics come from Windows Installer, not custom code (FR-110–112). |

## 3. Data designs

| Artifact | Location | Shape |
|---|---|---|
| VM configuration | `<bundle>.utm\config.plist` | UTM v4 XML plist — normative spec in utm-compatibility.md |
| VM payloads | `<bundle>.utm\Data\` | qcow2 disks, `efi_vars.fd`, `debug.log`, `tpmdata`, `vmstate` |
| Host-only state | `<bundle>.utm\zutm-state.json` | `{version, externalDrivePaths, lastStartedUtc, window}` |
| App settings | `%APPDATA%\ZuTM\settings.json` | `{version, vmFolder, updateRepository, checkForUpdates, lastUpdateCheckUtc}` |
| Update artifacts | GitHub Releases | `ZuTM-x64.msi`, `ZuTM-arm64.msi`, `SHA256SUMS.txt`; asset `digest` field verified |
| Install layout | `Program Files\ZuTM` | App + self-contained WinAppSDK + `runtimes\{qemu,spice}` |

## 4. Interface designs (external)

- **QEMU argv** — the builder output; the de-facto ABI between model and hypervisor. Pinned by unit tests so any QEMU-arg change is a deliberate commit.
- **QMP** (RFC-less JSON stream) — handshake, id-correlated replies, async events; used for lifecycle, send-key, screendump, query-spice.
- **qemu-ga protocol** — same JSON shape minus greeting; `guest-ping`/`guest-exec{,-status}`; captures are base64 `outdata/errdata` (this qemu-ga build ignores `capture-output` — verdicts are written to files and read over serial instead).
- **Serial console** — raw TCP to `-serial tcp:…`; documented control-language decisions in AD-9.
- **GitHub Releases API** — `releases/latest` + asset browser URLs; user-agent pinned to the project.

## 5. Security design

1. Loopback-only endpoints (D-01) — SPICE runs `disable-ticketing` *because* it is loopback-only; changing the bind address requires revisiting ticketing (tracked as the guard on FR-118).
2. Update chain: TLS + semver gate + SHA-256 + size + refuse-if-no-digest (AD-17); Authenticode is the remaining gap (FR-115).
3. External drive paths are host state, never silently copied into shared configs.
4. No telemetry, no network calls outside the update check.

## 6. Deployment & update design

Build → `scripts/build-installer.ps1` publishes self-contained per-arch, harvests with WiX heat, emits `ZuTM-<arch>.msi` + `SHA256SUMS`. CI runs unit → E2E(real QEMU) → vm-e2e(Alpine suites) → both MSIs → GitHub Release on `v*` tags. The installed app checks the feed weekly and upgrades in place through the same MSI (AD-18).

## 7. Test design

| Level | What it pins | Representative requirements |
|---|---|---|
| Plist unit (fixtures, binary hand-assembly, malformed) | Format correctness both directions | FR-90/91 |
| UTM-model unit (real fixture + injected future keys) | Lossless round-trip | FR-92 |
| QEMU-args unit (per-feature argv + warnings) | The model→hypervisor ABI | FR-30–46/50–74 |
| QMP unit (fake server: handshake/ids/events/errors/concurrency/disconnect) | Control-plane robustness incl. orphaned-waiter hang class | FR-03/04, AD-8 |
| Serial-session unit (fake console, DSR, markers, bounded log) | The unattended-driver language | AD-9 |
| Update unit (mock HTTP, semver matrix, tamper) | Feed + integrity semantics | FR-113/114 |
| E2E — real QEMU, machine-less | Process + QMP lifecycle | FR-121 |
| E2E — Alpine headless VM | Boot/login/command/poweroff over ZuTM's own pipeline | FR-122 |
| E2E — Alpine XFCE desktop | SPICE server, guest-side capture, host-driven control, tools running | FR-123 |
| Environment builders (scripts) | Zero-human-input image production | FR-124 |

The E2E suites double as the feasibility record for platform caveats: headless `send-key` never reaching evdev, host screendump's KMS blindness and absolute-path drops, and `guest-*`-not-proxied are all *measured* there, and those measurements drive design choices (AD-10, guest-side capture, virtio-keyboard).

## 8. Verification

Current status: 102 + 41 unit tests and 5 E2E tests green on the development machine, including both real-VM suites; CI reproduces unit/E2E on every push and the VM suites on dispatch/tags. The SDR's status column is the authoritative implemented/planned ledger.
