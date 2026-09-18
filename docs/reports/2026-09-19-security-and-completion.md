# Work Report — 2026-09-19: Security Pass, Feature Completion, Test Expansion

**ZuTM © 2026 Ze'ev Russak <zutm@20032014.xyz> — ZuTM Attribution License.**

Scope of this pass: security audit + fixes, completion of partial SDR items,
new tests for previously untested features, full unit + E2E validation with
real virtual machines, and documentation updates. Final state: **Release
build clean; 135 + 42 unit tests and 5 E2E tests green.**

## 1. Security audit — findings and fixes

Manual review of every input boundary and every spawned process. Seven
findings, all fixed, all with regression tests where automatable:

| # | Finding | Severity | Fix | Test |
|---|---|---|---|---|
| S-1 | `PlistXml` used default XML settings — DTD/behavior not pinned (defense-in-depth gap for untrusted `config.plist`) | Medium | `XmlReaderSettings` pinned: `DtdProcessing.Ignore`, `XmlResolver=null`, no PIs, 64 MiB cap | `PlistSecurityTests` (DOCTYPE still parses; undeclared entity rejected; non-plist roots rejected) |
| S-2 | `UtmBundle.ResolveDriveImagePath` combined *untrusted* `Drive.ImageName` with the data directory — `..\..\x` escaped the bundle (arbitrary file read) | High | `IsSafeBundleFileName` gate (no separators, no `:`/`..`/invalid chars) before any resolve | `UtmBundleSecurityTests` (7 traversal shapes) |
| S-3 | `UtmBundle.DeleteDriveImage` same pattern — arbitrary file **delete** | High | Same gate; traversal names are a no-op | `UtmBundleSecurityTests` (4 traversal shapes + sentinel untouched) |
| S-4 | `UpdateInstaller.DownloadVerifiedAsync` used network-provided `asset.Name` in the temp path — `..\..\evil.exe` escaped `%TEMP%` | High | Sanitize invalid chars + `..`, `Path.GetFullPath` containment assertion before download | `UpdateInstallerTests.DownloadVerified_SanitizesHostileAssetNames` |
| S-5 | `UpdateInstaller.StartInstaller` interpolated path into an `msiexec` command line — embedded quote = argument injection | Medium | `ArgumentList` only | Covered by existing validation tests |
| S-6 | `VmLibraryService.CreateDiskImage` interpolated path into `qemu-img` command line | Medium | `ArgumentList` only | App-layer (no unit host); code-reviewed |
| S-7 | `OpenFolderCommand` interpolated settings path into `explorer.exe` args | Low | `ArgumentList` | App-layer |

Accepted residual risks (documented, unchanged): SPICE `disable-ticketing` is
safe under the loopback-only design rule (AD-01/D-01 in SDS); Authenticode
signing remains open (FR-115 — requires a certificate purchase, pipeline hook
ready).

## 2. Completed work items (SDR status changes)

| SDR | Item | Before → After |
|---|---|---|
| FR-03 | Pause/resume VM | 🟡 → ✅ — QMP stop/cont + STOP/RESUME **event-driven status** (guest-agent/monitor pauses reflect in the UI) + Pause/Resume buttons |
| FR-04 | Hard reset | 🟡 → ✅ — QMP `system_reset` + Reset button (destructive-action tooltip) |
| FR-05 | Delete VM | 🔜 → ✅ — confirmation dialog (default button = Cancel), stopped-state guard, library removal, selection cleanup |
| FR-06 | Clone VM | 🔜 → ✅ — `UtmBundle.Clone` (full copy: config + payloads + fresh UUID + " copy" name; refuses existing targets), Clone button, stopped-state guard |
| FR-119 (new) | Untrusted-input hardening | ✅ — the S-1…S-7 work, formalized as a requirement |

New `VmStatus` states: Pausing/Paused/Resuming (status text + all
Can*-flags recompute).

## 3. New tests for previously untested features

| Suite | Coverage added | Count |
|---|---|---|
| `QemuRuntimeTests` | bin vs flat layout, `SystemExecutable`, EDK2 firmware discovery (both filename variants, absent case), env override, invalid-root non-adoption | 7 |
| `PortAllocatorTests` | valid TCP range, launch port-set shape, optional guest-agent port | 3 |
| `ZutmStateTests` | missing/corrupt → null, full round-trip, version normalization | 4 |
| `UtmBundleCloneTests` | copy+identity, identity-preserving mode, target-exists refusal, default sibling path | 4 |
| `UtmBundleSecurityTests` | S-2/S-3 traversal matrices + happy path | 12 |
| `PlistSecurityTests` | S-1 | 3 |
| `UpdateInstallerTests` | S-4 | 1 |

**Test totals: 143 → 177 unit** (135 Core + 42 Update/Spice), E2E 5/5.

## 4. Validation loop

1. `dotnet build ZuTM.slnx -c Release` → **succeeded** (App x64 included).
2. Core suite → 135/135. Two initial failures were *test expectations*,
   not code bugs (entity rejection is the safe outcome; ancestor-walk
   finding the dev repo's runtime is by design) — tests corrected, code
   unchanged.
3. Update suite → 42/42 after one assertion fix (sanitized name keeps
   `evil.exe` *inside* the sanitized stem — the containment property that
   matters is "cannot escape %TEMP%", asserted via `GetFullPath` prefix).
4. Full E2E with `ZUTM_E2E=1` (real QEMU + both Alpine VMs, ~1 m 41 s) →
   5/5.

No code defects surfaced in the loop beyond the security findings above.

## 5. Documentation updated

- `docs/SDR.md` — FR-03/04/05/06 promoted to ✅ with notes; FR-119 added.
- `docs/SDS.md` — **AD-19** (untrusted-input boundary) and **AD-20**
  (argument-list-only process spawning) recorded in Security design.
- This report (`docs/reports/`).
