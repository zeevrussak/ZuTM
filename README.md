# ZuTM

**ZuTM** is a Windows 11 (x64 & ARM64) virtualization app in the spirit of
[UTM for macOS](https://github.com/utmapp/UTM) — QEMU-backed, hardware
accelerated, and **file-compatible with UTM**: VMs created in one can be
opened in the other.

> **ZuTM © 2026 Ze'ev Russak &lt;zutm@20032014.xyz&gt;**
> If you copy or base work on ZuTM, you must give credit to the author.
> See [LICENSE.TXT](LICENSE.TXT) and [AGENTS.md](AGENTS.md).

## Features

- **QEMU backend** — full QEMU machine configurability, same philosophy as UTM.
- **Hardware acceleration** — Windows Hypervisor Platform (WHPX) when
  available, TCG JIT fallback (ARM64 host → TCG x86 emulation; ARM64
  guest-on-ARM64-host → WHPX).
- **UTM `.utm` bundle compatibility** — open UTM VMs on Windows, move them
  back; unknown fields round-trip losslessly
  ([spec](docs/utm-compatibility.md)).
- **SPICE display** — SPICE graphical console with remote-viewer tooling,
  plus serial console, QEMU Guest Agent, and QMP machine control.
- **Native Windows 11 experience** — WinUI 3 / Windows App SDK, Fluent
  design, Mica, light/dark/system themes, layout mirroring UTM
  (VM list + detail pane, "Start" new-VM wizard).
- **Deployment** — MSI installers (x64 + ARM64), clean uninstall,
  in-app online updates from GitHub Releases with SHA-256 integrity
  verification.

## Status

v0.1.0 foundation milestone — shipped and verified in-repo:

- **Core** (93 unit tests): plist engine (XML + bplist00), UTM v4 config
  model with lossless round-trip, `.utm` bundle I/O, QEMU command-line
  builder (WHPX/TCG, drives, NAT+hostfwd, SPICE+vdagent, QMP, UEFI), QMP
  client, VM process lifecycle.
- **Update client** (41 unit tests): strict semver, GitHub Releases feed,
  SHA-256-verified download, msiexec hand-off.
- **WinUI 3 app**: VM list/detail, new-VM wizard, settings + updates UI.
- **E2E suite** (`ZUTM_E2E=1`): real-QEMU QMP lifecycle, on-disk bundle
  round-trip.
- **MSI** (`scripts/build-installer.ps1`): x64 + ARM64 WiX installers with
  in-place major upgrades; CI publishes GitHub Releases the app updates
  from.

See [docs/architecture.md](docs/architecture.md).

## Building

Prereqs: **.NET 10 SDK** (LTS), Windows App SDK workload (VS or
`dotnet workload install maui-windows`), WiX v6 (`dotnet tool install
--global wix`).

```powershell
git clone https://github.com/zutm/ZuTM
cd ZuTM
dotnet build ZuTM.slnx -c Release -p:Platform=x64   # or ARM64
dotnet test  ZuTM.slnx -c Release                   # unit tests
scripts/fetch-qemu.ps1                    # download QEMU runtime (optional)
```

## Repository map

| Path | Purpose |
|---|---|
| `src/ZuTM.Core` | Domain model, UTM bundle compat, QEMU argument builder, QMP client, VM process manager |
| `src/ZuTM.Spice` | SPICE console client integration |
| `src/ZuTM.Update` | GitHub Releases update client + integrity verification |
| `src/ZuTM.App` | WinUI 3 fluent application |
| `tests/` | xUnit unit tests + E2E automation |
| `installer/` | WiX MSI (x64/arm64), uninstall, upgrade |
| `docs/` | Compatibility & architecture specs |
| `.github/workflows/` | CI/CD |

## Credits

- **ZuTM** — © 2026 [Ze'ev Russak](mailto:zutm@20032014.xyz), released
  under the ZuTM Attribution License.
- **[UTM](https://github.com/utmapp/UTM)** — © osy, Apache-2.0. ZuTM is
  inspired by UTM's design and interoperates with its file format; no UTM
  source code is contained in this repository.
- **QEMU** — GPLv2, Trademark of Fabrice Bellard; distributed alongside
  ZuTM as a separate program.
- **SPICE / virt-viewer** — GPLv2+; `remote-viewer` ships as the display
  client in this milestone.
