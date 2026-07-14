# @brettabalmer/vault-reader

Read a [`vault`](https://github.com/brettabalmer/vault) encrypted secrets file (AES-256-GCM) and seed
`process.env`. Node built-ins only, no dependencies. Conforms to the tool's [`FORMAT.md`](https://github.com/brettabalmer/vault/blob/main/FORMAT.md).

```js
import { seedEnvironment } from "@brettabalmer/vault-reader";

// At server boot, before anything reads the env. No-op in Azure or when no key/vault is present.
seedEnvironment({ platform: "sveltekit", enabled: import.meta.env?.DEV ?? true });
```

Seeds only keys not already set, so per-worktree overrides and real cloud settings win. Also exports
`loadKey`, `findVaultDir`, `decrypt`, `parseEnv`, and `resolve` for custom flows.

The key is resolved from `$VAULT_KEY` or `~/.config/vault/key`; the vault dir is found by walking up for
`vault/manifest.json` (or `$VAULT_DIR`).
