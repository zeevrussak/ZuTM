# Error handling in ZuTM

ZuTM (c) Ze'ev Russak <zutm@20032014.xyz> — ZuTM Attribution License.

Design rule: **no failure may look like the window simply disappearing.**
Every layer that can fail has a defined, visible outcome.

## App runtime

Three global handlers in `App.xaml.cs` cover the ways a WinUI 3 app can hit
an unhandled exception:

| Handler | Catches | Behaviour |
|---|---|---|
| `Application.UnhandledException` | XAML / UI-thread callbacks | Log, keep the session (`Handled = true`), show the crash dialog |
| `AppDomain.UnhandledException` | Background threads (process is terminating) | Log + native message box best effort |
| `TaskScheduler.UnobservedTaskException` | Fire-and-forget task faults | Log (`SetObserved`) |

The **crash dialog** (queued onto the dispatcher, one at a time) shows the
exception, offers *Copy details*, *Continue* or *Close ZuTM*. When XAML
itself is too broken to host a dialog, the same text falls back to a Win32
message box — the window going away without a word is never the outcome.

### crash.log

All handlers write to `%LOCALAPPDATA%\ZuTM\crash.log` (`CrashLog` in
ZuTM.Core, unit-tested): timestamped, full exception detail, append-only,
rotated to `crash.prev.log` past 512 KiB. Writes are best-effort — a broken
log location must never become the crash. WinUI's stowed-exception fail-fast
(0xC000027B) leaves Windows Error Reporting with a native-only dump, so this
file is usually the *only* place the managed stack survives.

## Self-update (Settings ▸ Updates)

The old flow launched msiexec and closed the app immediately — a cancelled
UAC prompt or failed upgrade left the user with no app and no explanation.
The flow is now (`UpdateInstallRunner.InstallAsync`):

1. Confirm dialog; the window **hides** (does not close).
2. msiexec runs interactively with `/norestart`; ZuTM waits for the exit code.
3. Outcome classification (`ClassifyExitCode`, unit-tested):
   `0` success → app closes (replaced in place); `3010` → success, reboot
   pending; `1602` → **cancelled — the window comes back** with
   "still running your current version"; anything else → **failure — the
   window comes back** with the exit code and the MSI log location.

## MSI installer

- `MsiLogging = voicewarmupx!` — every msiexec run of the package
  automatically writes `%TEMP%\MSI*.log`, so real-world failures (where no
  one passes `/l*v`) are diagnosable.
- The WiX UI carries `FatalError` / `UserExit` dialogs, so a failed
  interactive install shows an error screen, not a vanishing wizard.
- `scripts/build-installer.ps1` verifies this contract in the built MSI
  (MsiLogging property + dialog table), and
  `scripts/testenv/installer-e2e.ps1` verifies on a real install that the
  automatic log actually appears.
