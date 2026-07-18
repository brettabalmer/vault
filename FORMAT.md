# `vault` file format (the contract)

Any reader — the reference C# `Vault.Core`, the npm port, a future Python port — MUST agree on everything in
this document. The fixtures under [`testvectors/`](testvectors/) are the executable form of this spec: every
reader decrypts them to the same map in CI.

`formatVersion` for this document: **2** (adds the keyring + vault identity; §6).

---

## 1. The keyring (encryption keys)

One machine holds keys for **many vaults** (one per repo), so the key store is a **keyring**: a list of
`<id> :: <base64 32-byte key>` pairings, one per [vault identity](#6-vault-identity) (§6). A line with **no
`::`** is the **legacy bare key** — pre-identity vaults, and the migration fallback.

```
# ~/.config/vault/key   (%APPDATA%\vault\key on Windows; mode SHOULD be 600, never committed)
9f3a1c4d8e2b7a60 :: Base64Key1==
2b7a609f3a1c4d8e :: Base64Key2==
Base64LegacyBareKey==
```

To decrypt a vault whose header carries identity **`id`**, a reader resolves the key in this order:

1. `$VAULT_KEY` (base64 of the 32 raw bytes) — an explicit single-key override (CI), used regardless of `id`;
2. `$VAULT_KEY_FILE` or `~/.config/vault/key`, parsed as a keyring → the pairing for `id`;
3. the **legacy bare key** in that keyring (fallback — so a vault can *gain* an id without breaking readers
   whose keyring hasn't been updated: assigning an id doesn't change the key).

A vault with **no** identity (legacy/identity-less) uses `$VAULT_KEY` or the legacy bare key directly. Each key
is 32 bytes; `vault init` creates a pairing from a CSPRNG, `vault keygen` writes a legacy bare key. Keys are
**never** committed and never leave the machine (`vault share-key` copies one to the clipboard for out-of-band
sharing).

## 2. The vault file — `<profile>.enc` (format v2)

One file per profile (`local.enc`, …) plus an optional `personal.enc` (§4a). **v2 is a UTF-8 text file** —
one `KEY=VALUE` line per var, so git can line-diff and auto-merge changes across worktrees:

```
#vault:2 id=9f3a1c4d8e2b7a60
# Managed by `vault` — do not hand-edit. Non-secret values are plaintext; secrets are enc:<base64>.
COSMOS_ENDPOINT=https://localhost:8081
COSMOS_KEY=enc:2zbLw1xUXS7ZHR70XDL9oyTx1Z4cECACop002i0DkuEWTdA2/8CkAcgGSRM5GcOobTo=
BLANK_SECRET=
```

- First line starts with **`#vault:2`** (format marker) and MAY carry the vault **identity** as an `id=<hex>`
  token (§6) — space-separated, plaintext, committed. No `id=` → an identity-less vault (uses the legacy key).
- All other lines starting `#` and blank lines are ignored.
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
| `required` | enum | `"yes"` (needed for local dev **and** the deployed cloud apps) \| `"devOnly"` (needed for local dev only — e.g. sandbox/integration creds — not expected in cloud) \| `"no"` (optional). A legacy JSON bool still loads (`true`→`yes`, `false`→`no`) and is rewritten to the string form on the next save. `vault check` fails if a `yes` **or** `devOnly` var is unset locally; cloud-deploy checks look at `yes` only. |
| `secret` | bool | mask in output; `false` = plain config |
| `platforms` | string[] | which readers surface it (`sveltekit`, `worker`, `events`, `python`, `tools`, `deploy`) |
| `profiles` | string[] | which vault files may hold a value (`local`, `azure-dev`, `azure-prod`) |
| `example` | string? | sample value for docs/onboarding |
| `default` | string? | value used when the vault has none (non-secret config) |
| `validate` | string? | optional .NET/JS-compatible regex the value must match |
| `source` | enum? | agent hint: `derived` \| `emulator` \| `az` \| `user` \| `external` |

`vault check` fails when a `required` var (for the active profile) is absent from both the vault and `default`,
or when a present value fails `validate`.

The manifest is authored either by hand or via the CLI: **`vault manifest add KEY [flags]`** creates a var
(and bootstraps `vault/manifest.json` if it doesn't exist yet — as does `vault init`), **`vault manifest set
KEY [flags]`** edits an existing var's fields, and **`vault manifest rm KEY`** removes one. Field flags mirror
the columns above (`--category --description --platforms a,b --profiles a,b --default --validate --source
--example --required yes|devOnly|no`, and booleans `--required`/`--no-required` (sugar for `yes`/`no`),
`--secret/--no-secret --personal/--no-personal`). New vars
default to `secret: true` (fail-safe). Editing through the CLI rewrites the file sorted by key (indented JSON;
hand-authored comments are not preserved).

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

## 6. Vault identity

Because one machine's keyring (§1) holds keys for many vaults, each vault carries an **identity** — an opaque
id (the reference impl uses 16 lowercase hex chars) — stamped in the `<profile>.enc` header (`#vault:2 id=…`)
and committed. All profiles in one `vault/` dir share the vault's id; the reader reads the id from the shared
file, looks up the matching key in the keyring, and uses it for every profile + `personal.enc`.

Lifecycle commands (reference CLI):

- **`vault init`** — give a vault its identity. On an **identity-less** vault it assigns a new id and **keeps
  the existing key** (writes an `id :: key` pairing) — non-destructive, and readers with only the legacy bare
  key still decrypt (the key is unchanged). On an **already-identified** vault it prompts, then resets to a new
  id **and a new key**: values you can still decrypt are re-encrypted, secrets you can't are wiped (plaintext
  config is kept). This is the "cloned the repo, want my own values" path.
- **`vault rekey`** — rotate to a new id + new key, **preserving all values** (requires the current key; refuses
  rather than wipe).
- **`vault share-key`** — copy this vault's `id :: key` pairing to the clipboard, for out-of-band sharing.
- **`vault add-key`** — add a teammate's `id :: key` pairing to your keyring.

A reader that meets an unknown id (no keyring pairing, no legacy bare key) MUST fail clearly — it names the id
and points at `vault add-key` — never silently fall back to a wrong key.
