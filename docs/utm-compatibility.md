# UTM `.utm` Bundle Compatibility Specification

**Status:** Implemented (reader/writer in `src/ZuTM.Core/Utm/`)
**Target:** UTM stable schema `ConfigurationVersion = 4`
**Guarantee:** ZuTM opens UTM bundles; UTM opens ZuTM-written bundles.

> ZuTM is a clean-room implementation of this format. This document is the
> internal spec ZuTM codes against. Credit: the format is defined by the
> behavior of [UTM](https://github.com/utmapp/UTM) (© osy, Apache-2.0).

## 1. Bundle layout

A `.utm` "file" is a directory:

```
My VM.utm/
├── config.plist      XML property list (see §2); written by UTM as XML
└── Data/             all VM payloads (older UTM ≤3 used Images/, v4 uses Data/)
    ├── disk-0.qcow2  QCOW2 disk images (name = Drive.ImageName)
    ├── efi_vars.fd   UEFI variables (QEMU config, UEFI boot)
    ├── debug.log     debug log when QEMU.DebugLog = true
    ├── tpmdata       emulated TPM state (QEMU.TPMDevice = true)
    └── vmstate       snapshot/hibernate state
```

## 2. `config.plist` root keys

| Key | Type | Notes |
|---|---|---|
| `Backend` | string | `"QEMU"` for QEMU VMs; `"Apple"` (unsupported on Windows — ZuTM refuses gracefully) |
| `ConfigurationVersion` | int | `4`. ZuTM accepts 4..4; `<4` = too old (UTM migrates legacy — out of scope, planned); `>4` = too new |
| `Information` | dict | see §3 |
| `System` | dict | §4 |
| `QEMU` | dict | §5 |
| `Input` | dict | §6 |
| `Sharing` | dict | §7 |
| `Display` | array of dict | §8 (may be empty for headless) |
| `Drive` | array of dict | §9 |
| `Network` | array of dict | §10 |
| `Serial` | array of dict | §11 |
| `Sound` | array of dict | §12 |

## 3. `Information`

| Key | Type | UTM default |
|---|---|---|
| `Name` | string | required |
| `UUID` | string (UUIDv4, uppercase w/ dashes) | generated |
| `IconCustom` | bool | `false` |
| `Icon` | string | icon name; file `Data/<icon>` when `IconCustom` |
| `Notes` | string | `""` |

## 4. `System`

| Key | Type | Default |
|---|---|---|
| `Architecture` | string | `"x86_64"` |
| `Target` | string | machine, e.g. `"q35"` |
| `CPU` | string | `"default"` |
| `CPUFlagsAdd` | array of string | `[]` |
| `CPUFlagsRemove` | array of string | `[]` |
| `CPUCount` | int (MiB-independent; 0 = host count) | `0` |
| `ForceMulticore` | bool | `false` |
| `MemorySize` | int MiB | `512` |
| `JITCacheSize` | int MiB (0 = default) | `0` |

## 5. `QEMU`

| Key | Type | Default |
|---|---|---|
| `DebugLog` | bool | `false` |
| `UEFIBoot` | bool | `false` |
| `RNGDevice` | bool | `true` |
| `BalloonDevice` | bool | `true` |
| `TPMDevice` | bool | `false` |
| `Hypervisor` | bool (use host hypervisor accel) | `true` |
| `TSO` | bool (TCP segmentation offload) | `false` |
| `RTCLocalTime` | bool | `false` |
| `MachinePropertyOverride` | string | `""` |
| `AdditionalArguments` | array of dict `{{qemuArgument=[...]}}` | `[]` |

Newer UTM builds may add `SpiceServerPort`/`SpiceServerTlsPort`/
`SpiceServerPassword` — unknown keys are preserved losslessly (§13).

## 6. `Input`

| Key | Type | Default |
|---|---|---|
| `UsbBusSupport` | string: `None` \| `Default` \| `USB 2.0` \| `USB 3.0` | `Default` |
| `UsbSharing` | bool | `false` |
| `MaximumUsbShare` | int | `3` |

## 7. `Sharing`

| Key | Type | Default |
|---|---|---|
| `DirectoryShareMode` | string: `None` \| `WebDAV` \| `VirtFS` | `None` |
| `DirectoryShareReadOnly` | bool | `false` |
| `ClipboardSharing` | bool | `false` |

## 8. `Display[]`

| Key | Type | Default |
|---|---|---|
| `Hardware` | string (e.g. `virtio-gpu-gl`, `QXL`, `bochs`, `ramfb`) | arch-dependent |
| `VgaRamMib` | int | `128` |
| `DynamicResolution` | bool | `false` |
| `UpscalingFilter` | string `Linear`\|`Nearest` | `Linear` |
| `DownscalingFilter` | string `Linear`\|`Nearest` | `Linear` |
| `NativeResolution` | bool | `false` |

## 9. `Drive[]`

| Key | Type | Notes |
|---|---|---|
| `ImageName` | string? | file inside `Data/`; **absent ⇒ external image** (UTM stores a security bookmark — macOS-only; on Windows ZuTM keeps path in its own registry, not in config.plist) |
| `ImageType` | string | `None`\|`Disk`\|`CD`\|`BIOS`\|`LinuxKernel`\|`LinuxInitrd`\|`LinuxDTB` |
| `Interface` | string | `None`\|`IDE`\|`SCSI`\|`SD`\|`MTD`\|`Floppy`\|`PFlash`\|`VirtIO`\|`NVMe`\|`USB` (only meaningful for Disk/CD) |
| `InterfaceVersion` | int | `1` |
| `Identifier` | string (UUID) | stable drive id |
| `ReadOnly` | bool | `false` (external default `true`) |

## 10. `Network[]`

| Key | Type | Default |
|---|---|---|
| `Mode` | string `Emulated`\|`Shared`\|`Host`\|`Bridged` | `Shared` |
| `Hardware` | string (e.g. `virtio-net-pci`, `e1000`, `rtl8139`) | arch-dependent |
| `MacAddress` | string | generated |
| `IsolateFromHost` | bool | `false` |
| `PortForward` | array of dict | §10.1 |
| `BridgeInterface` | string | `""` |
| `VlanGuestAddress` … | string | VLAN (Host mode) fields |
| `HostNetUuid` | string | host network UUID (UTM-managed) |

### 10.1 `PortForward[]`

`Protocol` (`TCP`|`UDP`), `HostPort` (int), `GuestAddress` (string, may be
empty), `GuestPort` (int). `HostAddress` optional bind address.

## 11. `Serial[]`

| Key | Type | Default |
|---|---|---|
| `Mode` | string `Terminal`\|`TcpClient`\|`TcpServer`\|`Ptty` | `Terminal` |
| `Target` | string `Auto`\|`Manual`\|`GDB`\|`Monitor` | `Auto` |
| `Hardware` | string (e.g. `isa-serial`) | arch-dependent |
| `Terminal` | dict (theme/colors/font; cosmetic) | — |
| `TcpPort` | int | `0` |
| `WaitForConnection` | bool | `false` |

## 12. `Sound[]`

| Key | Type | Default |
|---|---|---|
| `Hardware` | string (e.g. `intel-hda`, `usb-audio`, `AC97`) | arch-dependent |

## 13. Lossless round-trip rule (CRITICAL)

ZuTM's reader loads **every** key it understands into the model and keeps
**every key it does not understand** in a per-section `UnknownKeys`
dictionary. The writer re-emits them verbatim. Ordering inside dictionaries
is not guaranteed by plist semantics and must not matter. Therefore:

- ZuTM→UTM→ZuTM must never lose a field.
- Unknown enum values load as strings (ZuTM models them as
  `string`-backed smart enums, not closed C# enums).

## 14. Property list encoding rules

- `config.plist` is **XML plist** (`<?xml …?><plist version="1.0"><dict>…`).
  ZuTM also **reads** binary plists (`bplist00`) defensively; it always
  **writes** XML, matching UTM.
- Types used: `dict`, `array`, `string`, `integer`, `true`/`false`,
  `data` (rare), `date` (none in v4 schema).
- Integer width: plist integers are 64-bit signed.

## 15. Windows-side extensions (ZuTM-only)

ZuTM stores host-specific state (window bounds, external drive paths,
last-run timestamps) in a **sibling** `zutm-state.json` inside the bundle —
**never** in `config.plist` — so UTM sees a pristine file and its
change-detection (file resource identity) is not confused. Deleting
`zutm-state.json` must be harmless.
