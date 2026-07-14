# vault

A tiny, encrypted, git-committable secrets store for local development — one manifest that documents every var
(category, description, required, which platforms need it) and one AES-256-GCM vault file per environment.
Native readers seed the values into each platform's process environment at boot, so **you never hand-edit a
`.env` or `local.settings.json` again**.

Built because real projects spread secrets across `.env`, `.env.local`, two `local.settings.json`, and a pile
of deploy files, with no description of what's required or what a new developer must set.

> Naming: `vault` collides with HashiCorp Vault's CLI/`VAULT_*` convention. The key env var is `VAULT_KEY` and
> the key lives at `~/.config/vault/key`. Reader packages publish under a scope.

## How it works

- **`vault/manifest.json`** (committed, plaintext) — the schema: every var's `category`, `description`,
  `required`, `secret`, `platforms`, `profiles`, `default`, `validate`, and an agent `source` hint. This is the
  single source of truth for "what does a new dev need".
- **`vault/<profile>.enc`** (committed, encrypted) — the values, per environment (`local`, `azure-dev`, …).
  Safe to commit because it's AES-256-GCM. See [`FORMAT.md`](FORMAT.md).
- **`~/.config/vault/key`** (never committed) — the shared 32-byte key. Onboarding = get this key out-of-band.

The whole flow is agent-friendly: `vault missing --json` lists what's needed with hints, `vault set KEY --stdin`
sets one value without echoing the others or leaking into shell history, `vault check` gates your dev startup.

## Commands

```
vault                 full-screen TUI (Spectre.Console)   [coming soon]
vault check | verify  validate every required var is present + values match their format; nonzero exit on failure
vault list            status table (--category --platform --missing --json)
vault missing --json  required-but-unset vars, machine-readable
vault get KEY         one value, masked (--reveal for raw)
vault set KEY VALUE   set one value (or KEY --stdin)
vault unset KEY       remove a value
vault describe KEY    a var's metadata
vault export --platform P [--format dotenv|json|shell]
vault run -- CMD      run CMD with the vault injected into its env
vault import --from . one-time migration from scattered env files
vault keygen          create ~/.config/vault/key
```

Global flag: `--profile <local|azure-dev|azure-prod>` (default `local`).

## Consuming it

- **CLI**: NativeAOT single-file binary (`dotnet publish -r <rid> -c Release`), shipped via GitHub Releases +
  a Homebrew tap.
- **C# apps**: the `Vault.Reader` NuGet package — `AddVault(...)` seeds `Environment.SetEnvironmentVariable`
  for keys not already set (dev-only; no-op in Azure).
- **Node apps**: the `@<scope>/vault-reader` npm package — seeds `process.env` at server boot.
- **Python**: a reader module (planned).

All readers agree on [`FORMAT.md`](FORMAT.md), enforced by the shared fixtures in [`testvectors/`](testvectors/).

## Layout

```
src/Vault.Core     reference impl: crypto, manifest, vault, resolve
src/Vault.Cli      the `vault` binary (Spectre.Console, NativeAOT)
src/Vault.Reader   NuGet host-integration facade
src/Vault.Tests    xUnit: crypto, env text, resolution, conformance
packages/js        npm reader (port)
testvectors/       cross-language conformance fixtures
```

## Develop

```
dotnet test                                            # unit + conformance
dotnet publish src/Vault.Cli -r osx-arm64 -c Release   # standalone binary → bin/.../publish
```
