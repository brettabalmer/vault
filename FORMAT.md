# `vault` file format (the contract)

Any reader — the reference C# `Vault.Core`, the npm port, a future Python port — MUST agree on everything in
this document. The fixtures under [`testvectors/`](testvectors/) are the executable form of this spec: every
reader decrypts them to the same map in CI.

`formatVersion` for this document: **1**.

---

## 1. The encryption key

A single symmetric **32-byte** key, base64-encoded, resolved in this order:

1. `$VAULT_KEY` environment variable (base64 of the 32 raw bytes), else
2. the file at `$VAULT_KEY_FILE`, else
3. `~/.config/vault/key` (`%APPDATA%\vault\key` on Windows).

The key file contains exactly the base64 string (trailing whitespace/newline trimmed). Mode SHOULD be `600`.
`vault keygen` creates it from a CSPRNG. The key is **never** committed and never leaves the machine.

## 2. The vault file — `<profile>.enc`

One file per profile (`local.enc`, `azure-dev.enc`, …). Contents are the **base64** encoding of this binary
envelope:

```
┌─────────┬────────────┬───────────────────────────┬──────────┐
│ version │   nonce    │        ciphertext         │   tag    │
│ 1 byte  │  12 bytes  │        N bytes            │ 16 bytes │
└─────────┴────────────┴───────────────────────────┴──────────┘
```

- **version** = `0x01`. A reader MUST refuse a version it does not implement.
- **nonce** = 12 random bytes (fresh per write; AES-GCM nonces must never repeat under one key).
- **ciphertext ‖ tag** = AES-256-GCM over the plaintext (§3). The tag is the standard 16-byte GCM auth tag.
- **AAD** = the single version byte (`0x01`). Binds the version into the authentication.

Decryption: base64-decode → split off version/nonce/tag → AES-256-GCM verify+decrypt with the key and AAD.
A failed tag check is a hard error (wrong key or tampered file) — never fall back to plaintext.

The base64 text is what gets committed to git. It's line-wrapped at 76 cols for readable diffs; readers MUST
strip all whitespace before decoding.

## 3. The plaintext — `KEY=VALUE` lines

The decrypted bytes are UTF-8 text, one assignment per line:

- Split on the **first** `=`. Everything after it is the value verbatim (values may contain `=`, `;`, spaces —
  connection strings do).
- Keys match `^[A-Za-z_][A-Za-z0-9_]*$`.
- Blank lines and lines beginning with `#` are ignored.
- No quoting, no escaping, no variable expansion, no multi-line values. A value is a single line of UTF-8.
- On write, entries are emitted sorted by key (stable diffs), `KEY=VALUE\n`.

This mirrors the repo's existing `scripts/lib/env-file.mjs` reader so migration is lossless.

## 4. The manifest — `manifest.json`

Lives in the **consuming** project (e.g. `comicLoader/vault/manifest.json`), not in a vault file. It is
plaintext and committed. Top level:

```jsonc
{ "formatVersion": 1, "vars": [ /* entries */ ] }
```

Each entry:

| field | type | meaning |
|---|---|---|
| `key` | string | the env var name (matches §3 key rule) |
| `category` | string | grouping for the UI |
| `description` | string | human/agent explanation |
| `required` | bool | must be present for `vault check` to pass |
| `secret` | bool | mask in output; `false` = plain config |
| `platforms` | string[] | which readers surface it (`sveltekit`, `worker`, `events`, `python`, `tools`, `deploy`) |
| `profiles` | string[] | which vault files may hold a value (`local`, `azure-dev`, `azure-prod`) |
| `example` | string? | sample value for docs/onboarding |
| `default` | string? | value used when the vault has none (non-secret config) |
| `validate` | string? | optional .NET/JS-compatible regex the value must match |
| `source` | enum? | agent hint: `derived` \| `emulator` \| `az` \| `user` \| `external` |

`vault check` fails when a `required` var (for the active profile) is absent from both the vault and `default`,
or when a present value fails `validate`.

## 4a. Personal overrides — `personal.enc`

Alongside the committed `<profile>.enc` (shared, same for everyone), a reader also loads an optional
**`personal.enc`** in the same directory: a **gitignored, per-developer** file (same envelope, same key) whose
values **override** the shared ones. This is where per-developer values live — your own LLM key, local model
name, machine paths, dev identity — without clobbering the shared vault.

- Resolution order (lowest→highest): manifest `default` → `<profile>.enc` (shared) → `personal.enc` (personal).
- A manifest var with `"personal": true` is written to `personal.enc` by `vault set` (default); `--personal` /
  `--shared` force the target for any var.
- `personal.enc` is **never committed** — add `vault/personal.enc` to `.gitignore`.

## 5. Resolution

A reader for platform *P* and profile *R* produces the map:

1. start empty;
2. for every manifest var whose `platforms` includes *P* and `profiles` includes *R*: if the vault has a value
   use it, else if `default` is non-null use that (skip if neither);
3. hand back `{KEY: value}`.

Boot-time readers seed these into the process environment **only for keys not already set**, so computed
overrides (e.g. per-worktree values) and real cloud settings always win.
