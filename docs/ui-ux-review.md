<!-- ZuTM (c) Ze'ev Russak <zutm@20032014.xyz> — Apache License 2.0. -->

# ZuTM UI/UX Review — October 2026

Review of the WinUI 3 app surface (`src/ZuTM.App`) against current Windows 11 /
Fluent 2 / WinUI 3 practice (Windows App SDK 2.5.1, .NET 10). Every finding
names the file it applies to; the plan at the end phases the work so each phase
ships independently and keeps the E2E suites green.

## What is already right

Worth preserving — several fundamentals are done well:

- Mica backdrop applied with correct layer brushes (`LayerFillColor*`) for the
  pane and status strips (`MainWindow.xaml.cs:91`).
- Consistent use of the Fluent type ramp (`TitleTextBlockStyle`,
  `CaptionTextBlockStyle`, …) instead of ad-hoc font sizes.
- The WHP acceleration notice is exemplary: `InfoBar` + severity switching +
  explicit elevated-consent explanation + progress inside the bar
  (`VmDetailView.xaml.cs:166`).
- Delete confirmation defaults to Cancel; destructive consequences are spelled
  out (`MainWindow.xaml.cs:204`).
- Tooltips carry real explanations, not label echoes.
- Thorough `AutomationProperties.AutomationId` coverage — the E2E suites and
  `scripts/testenv/ui-drive.ps1` depend on them. **Every redesign below must
  keep these IDs on the surviving elements.**

## Findings

Severity: **P1** hurts every user or risks data/flow breakage; **P2** a clear
usability or platform-convention gap; **P3** polish.

### Shell & window

1. **P1 — Duplicated branding / no title-bar integration.** The OS title bar
   says "ZuTM — Virtual Machines" and the content adds a second FontIcon +
   "ZuTM" caption row (`MainWindow.xaml:33-36`). WASDK 2.5.1 ships the
   `TitleBar` control: extend content into the title bar, put the app identity
   there, and move Search + primary actions into the title-bar row — the
   Windows 11 pattern (Settings, File Explorer, Dev Home).
2. **P2 — Ad-hoc master/detail instead of NavigationView.** A hand-rolled Grid
   gives no pane collapse, no adaptive behavior when the window narrows, and
   no conventional Settings entry point (`MainWindow.xaml:50-103`). Either
   adopt `NavigationView` (VM list in the pane, Settings in the footer) or add
   pane-collapse + narrow-window handling to the current layout.
3. **P2 — Window placement not persisted, no minimum size.** Every launch is
   1180×760 (`MainWindow.xaml.cs:101-109`). Save/restore size and position;
   set a minimum so the two-pane layout cannot crush.
4. **P3 — Dead layout tokens, drifting spacing.** `NavPaneWidth` and
   `SectionHeaderMargin` are defined in `App.xaml:16-17` and never used, while
   section headers hardcode `Margin="0,16,0,4"` in `VmDetailView.xaml` (six
   times) and `0,10,0,0` in the editor dialog. Pick the token, apply it
   everywhere.

### Feedback & status

5. **P1 — `IsBusy`/`Banner` are dead affordances.** `MainViewModel` exposes
   both (`MainViewModel.cs:57-75`) but no XAML binds them: folder rescans show
   no progress, and `LoadError` (unusable VM folder, corrupt bundle) is
   completely invisible. Bind an `InfoBar` for `Banner` and a header
   `ProgressRing` for `IsBusy`.
6. **P1 — Every outcome is a modal dialog titled "ZuTM".** Clone, snapshot,
   restore results and failures all route through `ShowInfoAsync`
   (`MainWindow.xaml.cs:329-339`). Success is not a decision — it should be a
   transient status, not a modal interruption. Failures belong in a sticky
   `InfoBar`. Side benefit: ContentDialogs are single-instance; the update
   prompt (`MainWindow.xaml.cs:423`) can already collide with a user-opened
   dialog and crash. Fewer modals, fewer collisions.
7. **P1 — Multi-GB ISO download runs inside a modal dialog with no cancel.**
   `NewVmDialog` blocks the entire app while `ImportRemoteIsoAsync` downloads
   (`NewVmDialog.xaml.cs:332-355`). Create the VM immediately, download in the
   background with progress on the VM row + status bar, cancellable; the
   dialog closes at create-time and the download continues.
8. **P2 — No transitional states.** Start/Stop/Reset give zero acknowledgment
   until QEMU's state flips; double-tap (`OnVmDoubleTapped`) and the Start
   button can both re-fire mid-transition. Add "Starting…/Stopping…"
   transitional status text, a row/detail `ProgressRing`, and command
   de-duplication.
9. **P2 — Unhandled exceptions are silent.** `App.OnUnhandledException` logs
   to crash.log and swallows (`App.xaml.cs:23-30`). The user sees a dead UI.
   Surface a dismissible error InfoBar pointing at the log.

### VM list

10. **P1 — No context menu on list items.** Right-click → Start / Stop /
    Clone / Delete / Open folder is a baseline affordance; today the only
    hidden shortcut is double-tap-to-start (`MainWindow.xaml:341`), which is
    undiscoverable and can start a VM accidentally.
11. **P2 — No status color language.** The row icon is always accent-colored
    and the error `InfoBadge` renders as a bare dot (`MainWindow.xaml:77-82`).
    Tint the state icon/dot: running = success, paused = caution, stopped =
    neutral, error = critical.
12. **P2 — Search resets selection.** `OnSearchChanged` → `ApplyFilter` clears
    the selection on every keystroke (`MainWindow.xaml.cs:136-148`) even
    though a keep-selection variant already exists (`ApplyFilterKeepSelection`)
    — the wrong one is wired to the TextBox.
13. **P2 — Weak empty states.** The detail-pane empty state offers no action
    (`VmDetailView.xaml:12-16`), and "No virtual machines yet" hides in the
    status strip (`MainWindow.xaml.cs:170`). Empty state gets a "Create a
    virtual machine" accent button; list pane gets an inline hint.

### VM detail pane

14. **P1 — Seven flat text buttons in one row.** Start/Pause/Resume/Reset/
    Stop/Clone/Delete all render identically except red Delete foreground
    (`VmDetailView.xaml:26-34`). Pause/Resume are one logical toggle split in
    two; destructive and ordinary actions carry the same visual weight. Move
    to a `CommandBar`: Start as the prominent action, a single Pause/Resume
    toggle, Reset/Clone/Snapshot/Restore in overflow, Delete separated with
    critical styling. Keep `EditVmButton`, `SnapshotButton`, `RestoreButton`
    IDs on the surviving controls.
15. **P2 — Nine sections in one endless scroll with a terminal at the bottom.**
    The serial console — an interactive tool — sits at the bottom of the page
    scroll (`VmDetailView.xaml:150-151`). Group secondary sections
    (Network/Notes/lifecycle) into `Expander`s; give the console a dedicated
    fixed-height region (or its own tab/window) with a Clear button and clear
    attached/detached state.
16. **P2 — Truncated values have no tooltip.** `SettingsRow` ellipsizes but
    leaves long paths/ISO names undiscoverable (`SettingsRow.xaml:17`).
17. **P3 — Per-section Refresh buttons.** USB section carries its own Refresh
    button (`VmDetailView.xaml:102`); auto-refresh on expand/selection covers
    most cases and declutters.

### Dialogs & forms

18. **P1 — Config editor accepts free text for constrained values.**
    Architecture, Machine, Graphics device and Network mode are TextBoxes with
    "e.g." hints (`VMConfigEditorDialog.xaml:19-20,32,35`). A typo becomes a
    mysterious QEMU boot failure. Replace with ComboBoxes driven from the same
    catalog `QemuCommandLineBuilder` understands; keep CPU model as validated
    text; consider an explicit "extra QEMU arguments" escape hatch.
19. **P1 — NewVmDialog is a 16-control form behind a modal.**
    (`NewVmDialog.xaml`) First-run users need five decisions (name,
    architecture, memory, disk, install source); USB bus, routing, format,
    allocation, vCPUs, ballooning and UEFI compete with them. Restructure as a
    two-step flow: **Essentials** (name, architecture, memory, disk size,
    install source as radio cards: local ISO / download distro / none) with
    **Customize** (everything else) collapsed by default.
20. **P2 — Settings dialog is raw.** VM folder is a bare TextBox with no
    browse button or "open folder" link (`SettingsDialog.xaml:13`); About
    hardcodes year with no assembly version; Save/Cancel instead of the
    instant-apply card pattern. Adopt `SettingsCard`/`SettingsExpander`
    (Community Toolkit) or hand-rolled equivalents, add a folder picker, and a
    System/Light/Dark theme selector.
21. **P3 — Snapshot naming has no validation or hint list** (free text,
    silently slugified, `MainWindow.xaml.cs:258`).

### Accessibility, platform, i18n

22. **P2 — No keyboard accelerators at all.** Baseline set: Ctrl+N (new VM),
    F5 (refresh), Ctrl+F (focus search), Ctrl+, (settings), Delete key on a
    selected list item (routes through the existing confirmation).
23. **P2 — Keep self-labeling when converting to icon buttons.** Current text
    buttons are accessible by construction; any icon-only conversion needs
    `AutomationProperties.Name` mirroring the tooltip.
24. **P3 — `Strings/en-US/Resources.resw` is dead.** All UI strings are
    hardcoded English. Either start routing user-visible strings through the
    resw (localization-readiness) or delete the stub and document the choice.
25. **P3 — No drag & drop.** Dropping a `.utm` bundle (import) or an `.iso`
    (attach/mount) onto the window matches the file-format-compatible
    positioning.

## Long-running operations — target design

Every operation that can outlast a couple of seconds needs one consistent
contract: **never inside a modal, always visible where the user is looking,
cancellable whenever the backend can actually cancel, honest when it can't.**

### Inventory (audited against current code)

| Operation | Backend | Typical duration | Progress today | Cancel today |
|---|---|---|---|---|
| Download distro ISO (new VM) | `RemoteIsoDownloader` — already has `IProgress<RemoteIsoDownloadProgress>`, `CancellationToken`, `.part` staging | 1–10+ min (GBs) | text inside the modal dialog only | **none exposed** |
| Import local ISO (new VM) | `UtmBundle.ImportDriveImage` → plain `File.Copy` | sec–min (GBs) | none | none |
| Create disk image | `DiskImageCreator` → `qemu-img create` (synchronous) | fast for sparse; minutes for fixed VHD/VDI at 100+ GiB | none (`qemu-img create` emits no progress) | none (kill + delete partial is feasible) |
| Clone VM | `UtmBundle.Clone` → per-file `File.Copy` | min (copies qcow2 payloads) | none | none |
| Snapshot save / restore | QMP `savevm`/`loadvm` | sec–min (RAM + disk state) | none — QEMU internal snapshots emit no progress events | **impossible** once started |
| VM start / stop | `VmLauncher` / `StopAsync` | 1–30 s | transitional `StatusText` exists ("Starting…") | stop escalation exists |
| Update download | `UpdateInstaller.DownloadVerifiedAsync` — `IProgress<long>` wired | sec–min (~100 MB) | `StatusText` in Settings dialog only | token param exists, never passed |
| Folder rescan | `ReloadAsync` | fast locally, slow on network shares | none (`IsBusy` unbound) | n/a |
| WHP enable (DISM) | `WhpFeature.EnableElevated` | 1–3 min | ring in InfoBar | UAC decline |

### Target model: a per-VM `Activity`

One concept replaces the ad-hoc handling. `VmItemViewModel` gains:

```csharp
public VmActivity? Activity   // null = idle; raises PropertyChanged
public sealed record VmActivity(
    VmActivityKind Kind,        // DownloadingIso, ImportingIso, CreatingDisk, Cloning, SavingSnapshot, RestoringSnapshot
    string Message,             // "Downloading ubuntu-24.04.3-desktop-amd64.iso"
    double? Percent,            // null = indeterminate
    long? BytesReceived, long? TotalBytes,
    bool CanCancel);
```

plus an internal `CancellationTokenSource`. The library exposes an active-count
for the status bar. Progress reports are throttled to ~4 Hz / 1% steps
(the throttling `NewVmDialog` already does moves into the shared layer).

**Presentation — three surfaces, all non-modal:**

1. **List row (primary).** `Activity` replaces the status caption
   ("Downloading… 43%") and adds a thin determinate `ProgressBar` across the
   row bottom (small indeterminate ring when `Percent` is null). The row stays
   selectable and the user can navigate anywhere — the download keeps running.
2. **Detail pane.** The same data as a strip under the InfoBars, with a Cancel
   button when `CanCancel`.
3. **Status bar.** Aggregate: "1 operation — Downloading ubuntu-24.04.3… (43%
   · 11 MB/s)". A shared formatter renders percent / MiB-of-GiB / speed / ETA
   when `TotalBytes` is known, "612 MiB received" when it is not.

**Behavior rules:**

- Dialogs collect input, then close; the work happens on the row. The new VM
  appears in the list immediately after bundle creation.
- Gating: while an `Activity` runs, conflicting commands are disabled
  (Start/Delete/Clone on the affected VM and clone target). A disabled
  command's tooltip says why ("Installer download in progress").
- Cancel semantics per capability: download/import/clone/disk-create cancel
  cleanly (`.part` and partial files deleted — the downloader already does
  this); savevm/loadvm offer **no** cancel and say so up front.
- Failure lands in the existing `vm.Error` channel (detail InfoBar + row
  badge); downloads get a Retry action.

### Per-operation target behavior

| Operation | Pattern |
|---|---|
| Download distro ISO | Dialog closes after `CreateAsync`; download runs on the row; determinate with speed/ETA; **Pause (resumable)** + Cancel + Retry; CD drive attaches automatically on completion. Start stays gated until it lands. |
| Import local ISO | Same as download, via a new `Core` `CopyWithProgressAsync` (buffered loop, `IProgress<long>`, cancellation) replacing `File.Copy`. |
| Create disk image | Indeterminate ring + honest label ("Creating 20 GiB disk image…"); Cancel = kill `qemu-img` + delete the partial file; sparse images finish in seconds so the ring must not flash. |
| Clone | Determinate by bytes (sum file sizes up front); Cancel deletes the partial clone directory. `UtmBundle.Clone` becomes async with progress. |
| savevm / loadvm | Indeterminate, no cancel, pre-warned tooltip ("the VM stays paused until the state is written; this cannot be interrupted"). The VM already shows Paused during savevm — the Activity adds the missing "why". |
| Update download | Keep Settings as the home; pass the cancellation token; add a Download progress ring + Cancel beside `Check now`. |
| Folder rescan | Bind `IsBusy` → header `ProgressRing` (Phase 1). |
| VM start | Transitional status already exists; add a silent re-entrancy guard — a second Start on a `Starting` VM currently sets a spurious "The VM is not stopped." error. |

### Download destination & resumable downloads

**Destination setting.** Settings gains a "Downloads folder" card. Default is
*inside each VM's bundle* (the status quo — bundles stay self-contained and
`.utm`-portable). When the user picks a folder:

- Downloads stage their `.part` there and the finished ISO **stays there**,
  attached to the VM as an external CD drive. The bundle format already
  models this (`UtmDrive.IsExternal` + `State.ExternalDrivePaths`), so no
  format work is needed.
- `NewVmDialog` shows the effective destination under the install-source
  controls ("Saves to: D:\ISOs — change in Settings") instead of adding a
  per-download picker — the dialog stays lean.
- Documented consequence: bundles referencing an external ISO are not
  self-contained. `UtmBundle.Clone` today drops local external paths
  (`UtmBundle.cs:345`), so a clone would lose its CD — small Core follow-up:
  re-reference the external file at clone time when it still exists, and show
  a warning row in the detail pane when a referenced external ISO is missing.
- Reuse: a second VM pointing at the same URL attaches the already-downloaded
  file (URL + size + ETag match) instead of re-downloading gigabytes.

**Pause / resume.** The downloader grows a session object
(`DownloadOperation` in `Core.Net`) with `Pause` / `Resume` / `Cancel` and a
state the `Activity` mirrors (Running / Paused / Completed / Failed):

- Resume sends `Range: bytes=<received>-` expecting `206`; a plain `200`
  (server ignores ranges) falls back to a clean restart. `If-Range` carries
  the stored ETag/Last-Modified so an upstream file that changed between
  sessions restarts instead of corrupting.
- Sidecar metadata next to the `.part` (`<name>.part.meta`: URL, ETag, total,
  received) is deleted on completion and cancel, kept on pause.
- Pause survives app restart: a scanned `.part` + sidecar (bundle Data
  directory or downloads folder) renders the row as "Download paused — 43% ·
  Resume" on the next launch.
- Graceful limits: no server Range support → resume falls back to a restart
  (the row says so); no Content-Length → indeterminate progress as today.

**Security acceptance (applies to every request, including Range
resumes):** http/https only — re-validate through
`RemoteImage.GetValidationError` before each re-request; validate the host
and refuse loopback/localhost and private/reserved-address targets before
connecting.

**Disk-space pre-flight:** when Content-Length is known, check free space on
the destination volume before starting; warn and point at the Downloads
folder setting when short.

**Threading:** `VmItemViewModel` property changes must land on the UI thread
(existing `DispatchAsync` pattern); `Progress<T>` captures the UI context at
construction.

**Test surface (repo standard: tests per feature):** Core — progress is
monotonic and completes, cancel leaves no `.part`/partial files (downloader
partly covered), clone totals match payload bytes; resume against a stub HTTP
server: `206` appends from the right offset, plain `200` falls back to a
restart, `If-Range` mismatch restarts, pause keeps `.part` + sidecar, cancel
deletes both, destination-parameter path lands the file in the chosen folder.
App — gating flags flip with `Activity`, pause/cancel restore `CanStart`,
retry re-runs cleanly.
`OnlineDistroIsoE2ETests` must keep passing: the download result on disk is
unchanged, only the driving UI moves out of the dialog.

## Plan

**Status (2026-10-15, second pass):** all four phases shipped. Phase 2:
CommandBar detail actions with Pause/Resume toggle, list context menu,
custom title bar (the WASDK `TitleBar` control is not present in WinUI
2.3.9/WASDK 2.5.1 — the ExtendsContentIntoTitleBar+SetTitleBar pattern
carries identity and actions instead), accelerators, search-keeps-selection,
window placement persistence + 860×560 minimum, pane collapse with 720px
auto-collapse (the documented NavigationView fallback — NavigationView's
selection semantics don't fit a bound ListView without breaking E2E ids).
Phase 3: New-VM essentials+Customize with source radio cards; config editor
constrained combos + expanders; settings cards with live theme preview,
folder pickers, About version, downloads manager. Phase 4: detail expanders,
console Clear + fixed height, status color dots, .utm/.iso drag & drop,
spacing token applied (dead `NavPaneWidth` removed). resw decision: strings
stay hardcoded English for now; `Strings/en-US/Resources.resw` is retained
for future localization rather than deleted (removing it risks PRI
generation) — revisit when localization becomes a goal. Earlier 2026-10-15
pass shipped Phase 1 in full (activity model, resumable downloads,
background creation, non-modal feedback) plus the downloads-folder setting.

**Runtime-verified (2026-10-15, second pass):** `ui-drive.ps1` smoke, a 16-check
UIA probe, and the distro download flow all pass against the real app. The
pass caught and fixed three defects that build verification could not see:
`Key="188"` compiled but crashed at XAML load (accelerator moved to
code-behind); the title-bar action stack overlapped the caption buttons
(reserved 148px, probe-verified 168px clearance); and `IsChecked="True"` on
the None radio fired its handler mid-`InitializeComponent`, killing the New-VM
dialog (initial check moved to the constructor). Three full distro runs
completed through the UI: dialog closes fast, download runs in background,
66 MiB Alpine ISO lands complete in the bundle with the CD drive attached and
no `.part` leftovers. **Runtime-verified again after review feedback (close
after download + title-area spacing):** the close path initially hung the UI
thread for 15-60s (the exact "(Not Responding)" title-bar state) — three
minidumps traced it to (1) `FreeLibrary` of the WHV probe library taking the
loader lock against XAML teardown, (2) the ListView's TwoWay `SelectedVm`
write-back firing the detail-view DP set from teardown-time callbacks, and
(3) `DispatcherQueue.TryEnqueue` blocking for the whole island teardown when
called from a callback straddling WM_CLOSE. Fixes: process-cached WHV library
handle (never freed), handler unsubscribed at `AppWindow.Closing`, always-
deferred `DetailView.Vm` assignment, deferred selection re-apply, throttled
(~10 Hz) progress callbacks, non-blocking VM dispose, and a two-phase close
(cancel → hide → cancel operations → close on a quiet dispatcher). Verified:
close after a completed 66 MiB download now exits in ~3 s with zero
unresponsive samples. UIA-probe note: `FindAll(TrueCondition)` over a window
enumerates on the target's UI thread and starves it — probes must use
name/id-conditioned `FindFirst`. **Still eyeballs-only:** drag-drop visuals,
theme preview rendering.

Constraints for every phase: keep every `AutomationProperties.AutomationId`
the E2E suites reference; all builds target win-x64 **and** win-arm64 with
warnings-as-errors; new files carry the ZuTM attribution header; small
conventional commits; add/adjust E2E coverage for any flow whose automation
surface changes.

### Phase 1 — Feedback & safety (no layout restructuring)

| # | Work | Files |
|---|------|-------|
| 1 | Bind `Banner` to a top `InfoBar`, `IsBusy` to a header `ProgressRing` | `MainWindow.xaml`, `MainViewModel.cs` |
| 2 | Replace `ShowInfoAsync` success modals with a transient status mechanism (auto-fading InfoBar or status-bar message); failures become sticky error InfoBars; kill the "ZuTM"-titled dialogs | `MainWindow.xaml.cs`, `VmDetailView` |
| 3 | **Activity model + three presentation surfaces** (list-row progress, detail strip, status-bar aggregate) per "Long-running operations — target design" above | `VmItemViewModel`, new `VmActivityStrip`, list template |
| 4 | Route the long operations through it: background ISO download after dialog close (**Pause/Resume/Cancel/Retry** via a resumable download engine in `Core.Net`, destination parameter plumbed from day one — see "Download destination & resumable downloads"), `CopyWithProgressAsync` for local-ISO import, async progress+cancel `Clone`, disk-create ring + kill-cancel, savevm/loadvm honest indeterminate, update-download cancel, start re-entrancy guard | `NewVmDialog.xaml.cs`, `VmLibraryService.cs`, `UtmBundle.cs`, `DiskImageCreator`, `UpdateViewModel`, new `Core` copy + download helpers |
| 5 | Unhandled-exception InfoBar ("details in %LOCALAPPDATA%\ZuTM\crash.log") | `App.xaml.cs`, `MainWindow.xaml` |
| 6 | Empty-state CTA button + in-list hint | `VmDetailView.xaml`, `MainWindow.xaml` |

Acceptance: reload and load-error visible; no modal appears for successful
clone/snapshot/restore; a 5 GB ISO download proceeds in the background — the
row shows percent/speed, the app stays usable, Pause → Resume continues from
the received offset, Cancel and Retry work and leave no partial files; clone
of a multi-GiB VM shows byte progress; gating disables Start/Delete during
those operations; E2E green.

### Phase 2 — Command surface & shell polish

| # | Work | Files |
|---|------|-------|
| 1 | Title bar: `TitleBar` control, content extends into title bar, Search + New VM live in the title-bar row, Refresh/Settings as icon buttons with labels-in-overflow or footer placement | `MainWindow.xaml(.cs)` |
| 2 | Detail actions → `CommandBar` (Start primary, Pause/Resume toggle, overflow for Reset/Clone/Snapshot/Restore, Delete critical + separated) | `VmDetailView.xaml` |
| 3 | Context menu on VM list items (Start/Stop/Clone/Delete/Open folder) | `MainWindow.xaml` |
| 4 | **Keyboard navigation:** complete Tab traversal across every interactable element (verify declaration order, no focus traps, visible focus visuals; read-only serial output stays traversable for copy) plus accelerators: Ctrl+N new VM, F5 refresh, Ctrl+F focus search, Ctrl+, settings, Delete = delete the selected VM (confirmation applies) | `MainWindow.xaml`, dialogs |
| 5 | Search keeps selection when the selected VM matches (use `ApplyFilterKeepSelection`) | `MainWindow.xaml.cs` |
| 6 | Window placement persistence + minimum size | `MainWindow.xaml.cs`, `AppSettings.cs` |

Acceptance: one visible primary action per VM state; right-click menu covers
all lifecycle ops without selection change; accelerators work; window
reopens at last size/position; E2E green (IDs preserved).

### Phase 3 — Forms & settings

| # | Work | Files |
|---|------|-------|
| 1 | NewVmDialog → two-step: Essentials + collapsed Customize; install source as radio cards (local ISO / distro download / none); keep all existing IDs for E2E | `NewVmDialog.xaml(.cs)` |
| 2 | Config editor: ComboBoxes for architecture/machine/graphics/network mode from a catalog shared with `QemuCommandLineBuilder`; validated CPU model; expanders per section | `VMConfigEditorDialog.xaml(.cs)`, new catalog in Core |
| 3 | Settings as cards: folder row with Browse… picker + "Open folder", theme selector (System/Light/Dark), About with real assembly version | `SettingsDialog.xaml(.cs)` |
| 4 | Tooltips on truncated `SettingsRow` values | `SettingsRow.xaml` |
| 5 | **Downloads folder setting**: card with folder picker + "Open folder"; when set, downloaded ISOs attach as external CD drives; destination notice in `NewVmDialog`; missing-external warning row in the detail pane; clone re-references existing external ISOs | `SettingsDialog.xaml(.cs)`, `AppSettings.cs`, `NewVmDialog.xaml(.cs)`, `VmDetailView`, `UtmBundle.cs` |

Acceptance: a new VM takes ≤5 visible decisions by default; invalid machine
names are unrepresentable in the editor; theme switch applies without restart
(verify dark contrast); E2E green including the online-distro flow.

### Phase 4 — Structure, polish, backlog

| # | Work | Files |
|---|------|-------|
| 1 | Serial console: dedicated fixed-height region (or tab), Clear button, state text; secondary detail sections into Expanders | `VmDetailView.xaml`, `SerialConsoleControl.xaml` |
| 2 | NavigationView adoption **or** pane-collapse + narrow-width handling (decide by trying NavigationView behind the existing IDs first) | `MainWindow.xaml` |
| 3 | Status color language on list rows (running/paused/stopped/error) | `MainWindow.xaml`, `VmItemViewModel` |
| 4 | Drag & drop: `.utm` import, `.iso` mount | `MainWindow.xaml(.cs)`, library |
| 5 | Spacing tokens applied; delete dead `NavPaneWidth`/`SectionHeaderMargin` or use them | `App.xaml`, detail views |
| 6 | Decide resw strategy (route strings vs delete stub); document | `Strings/`, backlog doc |
| 7 | Downloads manager: list downloaded ISOs (size, used-by VMs, Reveal/Delete) and reuse existing downloads for the same URL | library, Settings or dedicated page |

Acceptance: window usable at 720px width; console reachable in one click from
a running VM; drag-drop import round-trips a UTM bundle (compatibility tests
green).

## Risks & notes

- **Backend honesty:** some operations cannot offer what the UI would like —
  `savevm`/`loadvm` have no progress events and cannot be interrupted;
  `qemu-img create` emits no progress. The Activity model must show
  indeterminate progress and disable Cancel for these rather than pretending.
- **Resume depends on the server:** Range support and stable ETags vary by
  distributor mirror; the design falls back to a clean restart where they are
  missing and must never resume against a changed file.
- **Private-address guard vs LAN mirrors:** the security acceptance above
  refuses private/reserved-address targets; that blocks legitimate LAN-mirror
  URLs. If that matters in practice it needs an explicit allow-list decision,
  not a silent exception.
- **ContentDialog single-instance:** until Phase 1 lands, any new modal risks
  colliding with the update prompt. Prefer InfoBars for anything non-blocking.
- **x:Bind re-entrancy:** the collection-rebuild crash class documented in
  `MainWindow.xaml.cs:55-62` — any list restructuring must keep the
  deferred-update pattern.
- **WHP acceleration bar** is the model to follow for future advisories
  (severity, explicit consent, in-bar progress).
- Localizability is a deliberate backlog item, not an accident: either commit
  to resw now or record the decision.
