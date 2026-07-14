# Conformance test vectors

These fixtures are the **executable form of [`FORMAT.md`](../FORMAT.md)**. Every reader — the reference C#
`Vault.Core`, the npm port, a future Python port — MUST decrypt them identically in CI. If a change here
requires editing a reader, the format changed and `FORMAT.md` + `formatVersion` must change too.

Each vector directory contains:

| file | meaning |
|---|---|
| `key.txt` | the base64 AES-256 key (a **fixed, public, non-secret** test key — never used for real data) |
| `vault/manifest.json` | the manifest |
| `vault/<profile>.enc` | the encrypted vault file |
| `expected.json` | `vault` = the raw decrypted map; `resolved` = the platform/profile slice a reader must produce |

A reader passes when, using `key.txt`:
1. decrypting `<profile>.enc` yields exactly `expected.vault`, and
2. resolving for `expected.resolved.platform`/`profile` yields exactly `expected.resolved.map`.

Tampering test: flipping any byte of a `.enc` file MUST cause a hard decryption failure (GCM tag mismatch),
never a partial/plaintext read.
