# Work Remaining — 2026-09-19

**ZuTM © 2026 Ze'ev Russak <zutm@20032014.xyz> — ZuTM Attribution License.**

Authoritative statuses live in [SDR.md](../SDR.md); this is the prioritized
backlog distilled from it after today's pass.

## Near term (high value, design ready)

1. **Per-section config editor UI (FR-08/60).** The model + save layer are
   complete and lossless; build the System/Drives/Display/Network/Input/
   Sharing/QEMU editors over `VmItemViewModel.Bundle`. Largest single UX gap.
2. **Snapshots / save-restore (FR-07).** `Data\vmstate` reserved in the
   bundle format; needs QMP `savevm`/`loadvm` orchestration + UI + tests
   against the Alpine images.
3. **In-app serial terminal (FR-64).** Embed a terminal control bound to the
   serial TCP endpoint (the protocol layer is battle-tested by the E2E
   suites).
4. **CLI for automation (FR-105).** Core is UI-independent by construction;
   a small `zutm.exe` command surface (list/start/stop/clone/delete) reuses
   `VmLibraryService`.
5. **Authenticode signing (FR-115).** Blocked on certificate purchase only;
   CI hook ready.

## Medium term

6. **Legacy UTM (<v4) migration (FR-93).** Low demand path; nontrivial
   parser work for rare inputs.
7. **UI automation tests (FR-125).** WinAppDriver/Appium shell over the
   WinUI app; unit/E2E layers already cover everything below the UI.
8. **Shared-folders UX (FR-85)** on top of the WebDAV/VirtFS channels
   (FR-82/83) — includes documenting guest-side `spice-webdavd` setup.
9. **Localization (FR-104).** Extract inline strings to resources; keep
   `zutm@20032014.xyz` credit string non-translatable.
10. **Accessibility audit (FR-103).** Narrator/high-contrast pass over the
    shell and dialogs.
11. **Icon picker UX (FR-12)** and **sort/search (FR-11)**.

## Blocked / external

- ~~Native ARM64 QEMU (FR-24)~~ **Unblocked 2026-09-19:** qemu.weilnetz.de
  publishes ARM64-native installers (`/aarch64/`); fetch script and runtime
  discovery handle them — validate on the ARM64 dev machine.
- **TPM emulation (NR-04).** No maintained Windows `swtpm`.
- **Bridged networking (FR-54/NR-05).** No OSS bridge backend for Windows
  QEMU; current behavior = shared-NAT fallback with explicit warning.
- **VirtFS full support (FR-83).** Limited 9p in Windows QEMU builds —
  warning emitted, args passed.

## Not planned (rationale in SDR §12)

Apple backend (impossible), VNC transport, remote console exposure by
default, iOS companion.

## Continuous hygiene

- Keep the E2E image Alpine series pinned; bump and rebuild via
  `scripts/testenv/build-images.ps1` when upgrading (CI cache key includes
  the series).
- Re-run the security checklist (SDS AD-19/AD-20) for any new input source
  or spawned process.
