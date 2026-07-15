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

## 2. The vault file — `<profile>.enc` (format v2)

One file per profile (`local.enc`, …) plus an optional `personal.enc` (§4a). **v2 is a UTF-8 text file** —
one `KEY=VALUE` line per var, so git can line-diff and auto-merge changes across worktrees:

```
#vault:2
# Managed by `vault` — do not hand-edit. Non-secret values are plaintext; secrets are enc:<base64>.
COSMOS_ENDPOINT=https://localhost:8081
COSMOS_KEY=enc:2zbLw1xUXS7ZHR70XDL9oyTx1Z4cECACop002i0DkuEWTdA2/8CkAcgGSRM5GcOobTo=
BLANK_SECRET=
```

- First line is exactly **`#vault:2`** (format marker). Lines starting `#` and blank lines are ignored.
- Each entry: split on the **first** `=`. The **key** matches `^[A-Za-z_][A-Za-z0-9_]*$`. The **value** is
  either **plaintext** (non-secret) or an **`enc:<base64>`** token (secret), or empty.
- Which keys are secret is decided by the **manifest** (`"secret": true`); the reader itself is
  self-describing (the `enc:` prefix marks encryption) and needs no manifest to decrypt.
- A **blank secret is stored blank** (`KEY=`), not encrypted.
- Entries are written **sorted by key** (stable diffs).

**Per-value token** — `enc:` followed by base64 of `nonce(12) ‖ ciphertext ‖ tag(16)`:

- **AES-256-GCM**, tag 16 bytes, **AAD = the key name** (a token can't be moved to another key).
- **nonce = first 12 bytes of HMAC-SHA256(key, keyName ‖ 0x00 ‖ plaintext)** — *deterministic*. So an
  unchanged secret re-encrypts to the **same** token (no git churn), and a nonce only ever repeats for an
  identical `(key, name, value)` → identical token, which is safe. A failed tag check is a hard error.

**Legacy v1** (files NOT starting `#vault:2`): the whole payload was one base64 `version(0x01) ‖ nonce(12) ‖
AES-256-GCM(KEY=VALUE block, AAD=version) ‖ tag`. Readers MUST still accept v1; the next write upgrades the
file to v2.

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
