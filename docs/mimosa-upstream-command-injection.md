# Upstream report (draft): C# command-injection profile denies every `ProcessStartInfo`-based spawn

> Ready-to-file issue for the Mimosa maintainers. Verified against the Mimosa
> ZCode plugin 1.0.3 (protected release, local marketplace build), native
> offline engine, Windows 11 x64, October 2026. All verdicts below were
> produced by the CLI (`mimosa scan <file>`) with the native engine — the same
> engine the PreToolUse write-hook and the L3 git gate use for in-repo files.

## Summary

The deterministic C# command-injection profile (CWE-78) denies **every**
`Process.Start` call that goes through a `ProcessStartInfo` object — regardless
of program, arguments, or validation:

- `ArgumentList` argv (the profile's own remediation hint) — denied, even when
  every element is a compile-time literal.
- The `Arguments` property — denied, even with a constant string.
- Literal, argument-less utilities (`hostname.exe`) — denied.
- Fully validated programs (`Path.GetFullPath` + `File.Exists` + extension
  check + literal switches) — denied.

The **only** shape observed to pass is the plain string overload,
`Process.Start("literal.exe")`, which cannot carry arguments. The practical
effect is that no C# code that launches a process with arguments can pass the
write-hook or the git gate in `graded` mode, no matter how it is written.

## Minimal reproduction

`ProbeJ1.cs` — accepted:

```csharp
using System.Diagnostics;

internal static class ProbeJ1
{
    public static Process LiveProcess()
    {
        return Process.Start("hostname.exe")!;
    }
}
```

```
$ mimosa scan ProbeJ1.cs
✓ ProbeJ1.cs 未发现风险
```

`ProbeJ2.cs` — denied, two high findings, despite a literal program and a
literal argv:

```csharp
using System.Diagnostics;

internal static class ProbeJ2
{
    public static Process LiveProcess()
    {
        var startInfo = new ProcessStartInfo("hostname.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-a");
        return Process.Start(startInfo)!;
    }
}
```

```
$ mimosa scan ProbeJ2.cs
✗ 高危  命令注入  L13 [CWE-78]
✗ 高危  命令注入  L14 [CWE-78]
  2 高危
```

There is no taint anywhere in J2: the program is a literal, the single argv
element is a literal, and nothing external flows into the spawn. The deny
appears to key on the API shape (`ProcessStartInfo` object reaching
`Process.Start`) rather than on dataflow.

## Real-world impact (ZuTM)

ZuTM (https://github.com/zeevrussak/ZuTM) launches QEMU, msiexec and small
utilities in product and test code. Three representative sites, all flagged
high by the same profile:

1. `src/ZuTM.Update/UpdateInstaller.cs` — launches the verified MSI. This code
   was written specifically to satisfy the profile and is still denied:

   ```csharp
   var fullPath = Path.GetFullPath(installerPath);
   if (!File.Exists(fullPath) || !fullPath.EndsWith(".msi", StringComparison.OrdinalIgnoreCase))
   {
       throw new ArgumentException("Installer must be a downloaded .msi file.", nameof(installerPath));
   }

   var startInfo = new ProcessStartInfo("msiexec.exe") { UseShellExecute = false };
   startInfo.ArgumentList.Add("/i");
   startInfo.ArgumentList.Add(fullPath);
   ```

2. E2E harness launching QEMU with a fully internal argv list — denied.
3. `new ProcessStartInfo(Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe")
   { Arguments = "/c exit 0" }` — a constant argument string — denied.

Because no compliant shape exists for argument-bearing spawns, we must run the
git gate as `MIMOSA_GIT_GATE_MODE=warn`, which also disables hard blocking for
genuine findings — the opposite of what the gate is for.

## Shape matrix (all verified via `mimosa scan`, native engine)

| Shape | Verdict |
| --- | --- |
| `Process.Start("hostname.exe")` (string overload) | clean |
| `ProcessStartInfo("hostname.exe")` + initializer flags, no arguments | 2 × high |
| + `ArgumentList.Add("-a")` (literal) | 2 × high |
| + `ArgumentList` built in a loop from internal values | 2 × high |
| object-initializer `ArgumentList = { "/c", "exit", "0" }` | 2 × high |
| `ComSpec` + constant `Arguments = "/c exit 0"` | 2 × high |
| `msiexec.exe` + `GetFullPath` + `File.Exists` + `.msi` check + literal switches | 2 × high |
| dynamic program + argv from internal dictionary (QEMU harness) | 2 × high |

## Expected behavior

The profile should distinguish what the Node `execFile`/`spawn` handling
already distinguishes (per the README): fixed program vs user-controlled
program, fixed argv vs externally supplied argv, and interpreter code
parameters. Concretely, a spawn should not be denied as high when:

- the program is a compile-time literal or locally validated path, and
- argv elements carry no taint and contain no shell metacharacters, and
- no interpreter (`cmd.exe`/`/c`, shell) is in the chain — or the interpreter
  receives only proven-constant code parameters.

At minimum, `ProcessStartInfo` + literal-only `ArgumentList` should pass,
since it is the profile's own recommended remediation.

## Secondary observation: path-dependent scan strictness

The same source content receives different verdicts depending on where the
write-hook scans it: candidate writes to paths outside the project root
(e.g. `%TEMP%`) pass shapes that the native engine denies, while identical
content written inside the repo is denied. This makes hook-based probing
unreliable for verifying fixes — we initially built remediations on
temp-path "passes" that the in-repo engine rejected. One engine should answer
every path.

## Request

1. Fix the C# profile so fixed-program/argv-list spawns are not denied as
   high (see Expected behavior).
2. Align the write-hook engine across paths.
3. Until then, is there a supported per-path or per-rule exemption for the
   git gate? `backlog triage` does not feed the gate, and the finding ledger
   closes only when the rule stops matching — which cannot happen for shapes
   the profile over-matches.

---

Filed on behalf of ZuTM — a Windows port of UTM, QEMU-backed
(https://github.com/zeevrussak/ZuTM, (c) Ze'ev Russak <zutm@20032014.xyz>).
