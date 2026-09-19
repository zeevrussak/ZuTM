# Security Policy — ZuTM

**ZuTM © 2026 Ze'ev Russak <zutm@20032014.xyz> — ZuTM Attribution License.**

## Reporting

Report vulnerabilities privately to **zutm@20032014.xyz** (owner: Ze'ev Russak).
Please include reproduction steps and affected commit; plaintext PGP is fine at
this project's scale. We aim to respond within 7 days.

## Supported versions

The latest `v*` release and `main` are supported.

## Design posture (details in docs/SDS.md §5)

- **Loopback-only control plane** — SPICE/QMP/serial/guest-agent bind
  127.0.0.1 with per-launch ephemeral ports; `disable-ticketing` is
  acceptable *only* under this rule.
- **Untrusted inputs** — `config.plist` (DTD/entity hardened, size-capped
  XML), release JSON (sanitized asset names + containment), bundle file names
  (traversal gate before any resolve/delete). Regression tests pin all three.
- **Argument-list-only process spawning** — no interpolated command lines.
- **Update integrity** — TLS + SHA-256 + size; refuse-and-delete on
  mismatch; no digest ⇒ no download. Authenticode signing is pending a
  certificate (CI hook ready).
- No telemetry; the only network calls are the GitHub update check and
  package downloads from the configured feed.
