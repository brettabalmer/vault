# Brettabalmer.Vault.Reader

The .NET reader for the [`vault`](https://github.com/brettabalmer/vault) local-dev secrets tool. Decrypts a
`vault/<profile>.enc` file (AES-256-GCM per-value v2 format + keyring identity) and seeds the values into the
process environment — **only for keys not already set**.

**Dev-only and silent by design:** it no-ops in Azure (`WEBSITE_INSTANCE_ID` present) so deployed apps keep
reading their App Service settings, and it never throws or writes to stdout (safe in isolated Azure Functions
workers).

```csharp
using Vault.Core;

// First line of Program.cs — seeds env for the "worker" platform from the nearest vault/ dir.
EnvSeeder.SeedEnvironment("worker");
```

Key resolution uses the keyring at `~/.config/vault/key` (`$VAULT_KEY` / `$VAULT_KEY_FILE` override), picking
the key that matches the vault's identity header, with a legacy bare-key fallback. See the repo's `FORMAT.md`
for the full contract. The namespace is `Vault.Core`; the package also exposes the primitives (`Crypto`,
`KeyStore`, `VaultFile`, `Manifest`, `Resolve`).
