# ZuTM — Software Design Requirements (SDR)

**ZuTM © 2026 Ze'ev Russak <zutm@20032014.xyz> — ZuTM Attribution License.**

- **Companion document:** [SDS.md](SDS.md) (Software Design Specification — how each requirement is designed).
- **Status vocabulary**

| Status | Meaning |
|---|---|
| ✅ Implemented | Shipped in-repo and covered by automated tests |
| 🟡 Partial | Core/protocol layer implemented; some surface (usually UI) missing — see notes |
| 🔜 Planned | Accepted requirement, scheduled on the roadmap |
| ⛔ Blocked | Wanted; blocked on an upstream dependency — the "why" is given |
| ❌ Not planned | Deliberately out of scope — the "why" is given |
| 🚫 Not possible | Technically infeasible on this platform — the "why" is given |

- **Origin column:** `UTM` = feature parity with UTM for macOS; `ZuTM` = unique to ZuTM (Windows-platform or project-specific); `UTM+` = exists in UTM but ZuTM extends it.

---

## 1. Virtual machine management

| ID | Requirement | Origin | Status | Notes / why |
|---|---|---|---|---|
| FR-01 | Create a VM from a wizard (name, architecture, machine, memory, vCPUs, disk size, UEFI) | UTM | ✅ | NewVmDialog; disk created with `qemu-img` |
| FR-02 | Start / stop a VM | UTM | ✅ | Stop = ACPI power-down w/ 30 s grace, then process-tree kill |
| FR-03 | Pause / resume a VM | UTM | ✅ | QMP stop/cont + STOP/RESUME event-driven status + Pause/Resume buttons |
| FR-04 | Hard reset a VM | UTM | ✅ | QMP system_reset + Reset button with tooltip |
| FR-05 | Delete a VM (bundle) | UTM | ✅ | Confirmation dialog (destructive default: Cancel) + guarded by stopped-state; `UtmBundle` deletion unit-tested at the path-safety layer |
| FR-06 | Clone/duplicate a VM | UTM | ✅ | `UtmBundle.Clone` (config + payloads + fresh UUID, refuses existing targets) unit-tested; Clone button clones only stopped VMs |
| FR-07 | Save/restore VM state (snapshots, "hibernate") | UTM | ✅ | `QmpClient` savevm/loadvm/delvm + snapshot-list parser (unit-tested); Snapshot…/Restore… dialogs; restore = pause->loadvm->cont |
| FR-08 | Edit every VM configuration section in the UI | UTM | ✅ | `VMConfigEditorDialog`: identity/system/QEMU/display/network/sharing editors over lossless `with` transforms; unknown keys ride along |
| FR-09 | VM library list with status | UTM | ✅ | List + status text + error badges |
| FR-10 | VM detail view | UTM | ✅ | System/Drives/Display/Network/Notes, read-only rendering |
| FR-11 | Sort/search the VM library | UTM | ✅ | Search box filters; library sorted by name |
| FR-12 | Custom VM icon + notes | UTM | 🟡 | Model round-trips Icon/IconCustom/Notes; icon picker UX pending |
| FR-13 | Per-VM debug log toggle | UTM | ✅ | `QEMU.DebugLog` → `Data\debug.log`, streamed from QEMU stdout/stderr |

## 2. Guest platforms & acceleration

| ID | Requirement | Origin | Status | Notes / why |
|---|---|---|---|---|
| FR-20 | QEMU-backed emulation, any guest architecture | UTM | ✅ | TCG JIT; arch is a raw string (any QEMU build target) |
| FR-21 | Hardware acceleration via Windows Hypervisor Platform (WHPX) | ZuTM | ✅ | x64→x86_64/i386, ARM64→aarch64 (ARM64 WHPX needs Windows 11 24H2+ with HypervisorPlatform enabled; TCG otherwise); auto-detected via `WHvGetCapability` |
| FR-22 | Multi-threaded TCG for multi-core guests | UTM+ | ✅ | `-accel tcg,thread=multi` unless `ForceMulticore` |
| FR-23 | ARM64 host support | ZuTM | ✅ | WinUI app + MSI build on `windows-11-arm`; see FR-24 caveat |
| FR-24 | Native ARM64 QEMU runtime | ZuTM | ✅ | qemu.weilnetz.de now publishes `aarch64/` ("QEMU Installer for Windows on ARM"); `fetch-qemu.ps1` auto-selects it on ARM64 hosts (SHA-512 verified, flat layout, `qemu-system-aarch64.exe` validated), and runtime discovery accepts the aarch64-native marker |
| FR-25 | Apple-backend (Virtualization.framework) VMs | UTM | 🚫 | macOS-only hypervisor; ZuTM refuses these bundles with a clear message instead of corrupting them |

## 3. System configuration (parity: UTM `System` section)

| ID | Requirement | Origin | Status | Notes / why |
|---|---|---|---|---|
| FR-30 | Architecture / machine target / CPU model | UTM | ✅ | Raw strings — forward-compatible with future QEMU targets |
| FR-31 | CPU flags add/remove | UTM | ✅ | Requires explicit CPU model; warning otherwise |
| FR-32 | vCPU count (0 = match host) | UTM | ✅ | |
| FR-33 | Force-multicore toggle | UTM | ✅ | Switches TCG to single-thread (correctness mode) |
| FR-34 | Memory size (MiB) | UTM | ✅ | |
| FR-35 | JIT cache size | UTM | ✅ | Stored/round-tripped; Windows QEMU ignores TCG cache sizing — value kept for UTM fidelity |
| FR-36 | Machine property override | UTM | ✅ | Appended to `-machine` |
| FR-37 | Additional raw QEMU arguments | UTM | ✅ | Appended verbatim; preserved losslessly |

## 4. Storage

| ID | Requirement | Origin | Status | Notes / why |
|---|---|---|---|---|
| FR-40 | QCOW2 disk creation | UTM | ✅ | `qemu-img create` |
| FR-41 | Import existing disk images | UTM | ✅ | Copy-in with collision-safe naming |
| FR-42 | All UTM drive types (Disk/CD/BIOS/kernel/initrd/dtb) | UTM | ✅ | Mapped to `-drive`/`-bios`/`-kernel`/`-initrd`/`-dtb` |
| FR-43 | All UTM drive interfaces (IDE/SCSI/SD/MTD/Floppy/PFlash/VirtIO/NVMe/USB) | UTM | ✅ | Correct frontend devices for bus-less interfaces |
| FR-44 | Read-only drives | UTM | ✅ | |
| FR-45 | External images (outside the bundle) | UTM | ✅ | macOS bookmarks → Windows absolute paths in `zutm-state.json` (never in config.plist) |
| FR-46 | UEFI boot (EDK2) | UTM | ✅ | pflash code+vars; blank var-store seeded on first boot |
| FR-47 | VirtIO drivers guidance in-guest | UTM | 🔜 | Docs/help page; not a code feature |

## 5. Networking

| ID | Requirement | Origin | Status | Notes / why |
|---|---|---|---|---|
| FR-50 | Shared NAT networking (slirp) | UTM | ✅ | `-netdev user` |
| FR-51 | Port forwarding (TCP/UDP) | UTM | ✅ | `hostfwd` per rule |
| FR-52 | Host-only / isolated networking | UTM | 🟡 | `restrict=on` approximation (slirp without outbound); exact UTM "Host" VLAN semantics not reproduced — why: Windows QEMU lacks the bundled host-net VLAN backend |
| FR-53 | Emulated (guest↔guest) networking | UTM | 🟡 | Same approximation as FR-52 + warning |
| FR-54 | Bridged networking (tap) | UTM | ❌ | **Why:** Windows QEMU ships no bridge backend; tap requires admin + third-party driver. Falls back to shared NAT **with an explicit warning**; revisit if an OSS bridge ships |
| FR-55 | Custom MAC addresses | UTM | ✅ | Wizard generates `52:54:00:` OUI locally |
| FR-56 | NIC model choice (virtio-net/e1000/…) | UTM | ✅ | Raw string passthrough |

## 6. Display, input & console

| ID | Requirement | Origin | Status | Notes / why |
|---|---|---|---|---|
| FR-60 | Per-section config editor UI | UTM | ✅ | Delivered as `VMConfigEditorDialog` (see FR-08) |
| FR-61 | SPICE display server per VM | UTM | ✅ | Loopback-only, ticketing disabled on localhost |
| FR-62 | SPICE console client (remote-viewer) launch | UTM | ✅ | Bundled under `runtimes\spice` in the MSI; arg-builder unit-tested |
| FR-63 | Dynamic resolution + clipboard via vdagent | UTM | ✅ | vdagent virtserialport wired; active when a SPICE client is attached |
| FR-64 | In-app serial terminal tab | UTM | ✅ | `SerialConsoleControl`: attach/detach to the running VM's serial endpoint, streaming output, Enter/Send input |
| FR-65 | Serial modes: built-in TCP / TCP client / TCP server | UTM | ✅ | Plus GDB stub and HMP monitor routing |
| FR-66 | Headless VMs (`-display none`) | UTM | ✅ | |
| FR-67 | USB tablet (absolute pointer) | UTM | ✅ | Always with a display |
| FR-68 | USB 2.0/3.0 bus selection | UTM | ✅ | ehci/xhci |
| FR-69 | USB device redirection (client-side) | UTM | ✅ | spicevmc usb-redir channels × `MaximumUsbShare` |
| FR-70 | Host-injected keyboard (QMP send-key) | ZuTM | 🟡 | `virtio-keyboard-pci` added for client-driven input; QMP `send-key` measured unreliable in headless mode on this QEMU build (no evdev events) — documented; interactive input flows through the SPICE client |
| FR-71 | Screendump (pixel capture) | UTM+ | 🟡 | QMP client implemented; host-side dump only sees the legacy VGA plane (black under KMS) and drops absolute paths on this build — guest-side capture used instead by E2E; both reasons documented |
| FR-72 | Multiple simultaneous displays per VM | UTM | 🟡 | Model + args support N displays; UI creates one |
| FR-73 | Audio device passthrough | UTM | ✅ | Hardware string passthrough (intel-hda default) |
| FR-74 | Display up/downscaling filter choice | UTM | ✅ | Stored/round-tripped; applied by the SPICE client |

## 7. Sharing & guest tools

| ID | Requirement | Origin | Status | Notes / why |
|---|---|---|---|---|
| FR-80 | qemu-guest-agent integration | UTM+ | ✅ | Channel emitted by the builder; **direct GA protocol client** (E2E-proven) — QEMU does not proxy `guest-*` over QMP |
| FR-81 | spice-vdagent service support | UTM | ✅ | E2E image ships + asserts the service |
| FR-82 | WebDAV directory sharing channel | UTM | 🟡 | Channel wired (`org.spice-space.webdav.0`); requires `spice-webdavd` inside the guest — guest-side setup documented, not automated |
| FR-83 | VirtFS (9p) directory sharing | UTM | 🟡 | Args emitted with warning — why: Windows QEMU builds have limited/absent 9p server support |
| FR-84 | Clipboard sharing toggle | UTM | ✅ | Transport-level; effective with SPICE client attached |
| FR-85 | Shared folders browser UX | UTM | 🔜 | On top of FR-82/83 |

## 8. UTM file-format compatibility (project-defining)

| ID | Requirement | Origin | Status | Notes / why |
|---|---|---|---|---|
| FR-90 | Read UTM v4 `.utm` bundles | ZuTM | ✅ | Clean-room per [utm-compatibility.md](utm-compatibility.md); real-fixture tests |
| FR-91 | Write bundles UTM can reopen | ZuTM | ✅ | XML plist, atomic writes, lossless unknown-key round-trip |
| FR-92 | Move VMs between ZuTM and UTM | ZuTM | ✅ | Round-trip tests incl. future/unknown enum values |
| FR-93 | Legacy (pre-v4) UTM configs | UTM | 🔜 | Low priority — why: bundles upgraded once on a Mac become v4; migration code is nontrivial for rare inputs |
| FR-94 | ZuTM-private state isolated from config.plist | ZuTM | ✅ | `zutm-state.json`; deleting it is harmless |
| FR-95 | Legacy `Images/` layout support | ZuTM | ✅ | Read fallback to UTM ≤3 layout |

## 9. App platform & UX

| ID | Requirement | Origin | Status | Notes / why |
|---|---|---|---|---|
| FR-100 | Native Windows 11 Fluent UI (WinUI 3, Mica, system themes) | ZuTM | ✅ | UTM gives layout/feature inspiration; the look is native Windows by design |
| FR-101 | Light/dark/system theme follow | ZuTM | ✅ | WinUI defaults |
| FR-102 | Settings: VM folder, update policy | ZuTM | ✅ | `%APPDATA%\ZuTM\settings.json` |
| FR-103 | First-class keyboard accessibility | ZuTM | 🟡 | AutomationIds on primary controls + tooltips; narrator/high-contrast audit still pending |
| FR-104 | Localization | UTM | 🟡 | Resource infrastructure in place (Strings/en-US/Resources.resw); remaining strings migrate incrementally |
| FR-105 | CLI for automation (`zutm start …`) | UTM (utmctl) | ✅ | `zutm` (src/ZuTM.Cli): list/show/start/stop/pause/resume/clone/delete; shares `VmLauncher` + `RuntimeRegistry` with the GUI; E2E incl. real start/stop |

## 10. Deployment, updates, security

| ID | Requirement | Origin | Status | Notes / why |
|---|---|---|---|---|
| FR-110 | MSI installer, x64 + ARM64 | ZuTM | ✅ | WiX v6; per-machine; Start-menu shortcut |
| FR-111 | Clean uninstall | ZuTM | ✅ | Standard MSI removal incl. shortcut folders |
| FR-112 | In-place major upgrade (same or newer version) | ZuTM | ✅ | `MajorUpgrade` w/ `AllowSameVersionUpgrades` |
| FR-113 | Online update check from GitHub Releases | ZuTM | ✅ | Weekly cadence + manual; strict semver precedence |
| FR-114 | Update integrity (SHA-256 + size) | ZuTM | ✅ | Tamper ⇒ delete + refuse |
| FR-115 | Authenticode code signing | ZuTM | 🟡 | CI hook live (base64 PFX secret -> signtool, timestamped) - activates when a certificate secret is configured; cert purchase pending |
| FR-116 | Downgrade protection | ZuTM | ✅ | MSI refuses older versions with a clear message |
| FR-117 | Update feed is user-configurable | ZuTM | ✅ | Repo string in settings |
| FR-118 | Loopback-only VM endpoints | ZuTM | ✅ | SPICE/QMP/serial/GA bind 127.0.0.1 only — remote exposure deliberately not offered |

## 11. Quality & test infrastructure

| ID | Requirement | Origin | Status | Notes / why |
|---|---|---|---|---|
| FR-120 | Unit tests for every feature | ZuTM | ✅ | 143 across plist/UTM-model/QEMU-args/QMP/serial/update |
| FR-121 | Real-QEMU E2E lifecycle | ZuTM | ✅ | Boot/handshake/pause/resume/quit |
| FR-122 | Real-guest E2E (Alpine headless, serial-driven) | ZuTM | ✅ | Login → commands → verify → clean poweroff |
| FR-123 | Real-desktop E2E (XFCE + SPICE tools, capture + control) | ZuTM | ✅ | Guest-side capture, host-driven control, in-guest verification |
| FR-124 | Test-environment builders as scripts (no human input) | ZuTM | ✅ | `scripts/testenv/*`; unattended installs over serial |
| FR-125 | UI automation tests | ZuTM | 🟡 | Launch/window smoke automated (ZUTM_UI_E2E=1 + ZUTM_APP_EXE); full control-level automation (Appium) still pending |
| FR-126 | CI/CD incl. both-arch MSIs + releases | ZuTM | ✅ | GitHub Actions; releases gate on VM E2E |

## 12. Explicitly not planned (with reasons)

| ID | Non-requirement | Why |
|---|---|---|
| NR-01 | Hosting UTM's Apple backend | Impossible — macOS-only API (see FR-25) |
| NR-02 | VNC as a primary display transport | SPICE is the UTM-compatible path with the tooling users expect; VNC adds surface without a user ask |
| NR-03 | Remote (non-loopback) console/QMP exposure by default | Security stance; can be revisited behind auth + explicit opt-in |
| NR-04 | Bundling a TPM emulator today | No maintained Windows `swtpm` — config flag preserved for UTM fidelity, device skipped with a warning |
| NR-05 | Bridged networking until an OSS bridge ships | See FR-54 |
| NR-06 | iOS companion | Out of scope for this project's lifetime |

---

### Traceability

Every ✅ above maps to: code in `src/` + at least one test in `tests/` (see the testing matrix in [SDS.md](SDS.md#8-verification)); the E2E suites additionally prove the requirements against real virtual machines.
