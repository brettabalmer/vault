/** @brettbalmer/vault-reader — types. See FORMAT.md. */

/** The vault identity from a `#vault:2 id=…` header, or null (legacy/identity-less). */
export function readVaultId(encPath: string): string | null;

/**
 * Resolve the 32-byte key for a vault identity `id` from the keyring:
 * $VAULT_KEY → keyring[id] → legacy bare key. Returns a Buffer or null.
 */
export function loadKey(id?: string | null): Buffer | null;

/** Walk up from `start` (default cwd) for a `vault/manifest.json`; returns the `vault/` dir or null. */
export function findVaultDir(start?: string): string | null;

/** Decrypt a legacy v1 whole-file base64 envelope to its plaintext. Throws on tamper/wrong key. */
export function decrypt(base64Envelope: string, key: Buffer): string;

/** Decrypt one `enc:…` value token (v2, AAD = the var name). Throws on tamper/wrong key. */
export function decryptValue(name: string, token: string, key: Buffer): string;

/** Read a vault file → map. Handles v2 (per-value) and legacy v1 (whole blob). */
export function readVaultFile(path: string, key: Buffer): Record<string, string>;

/** Parse a KEY=VALUE block (used for legacy v1 payloads). */
export function parseEnv(text: string): Record<string, string>;

/** Resolve the {KEY:VALUE} map for a platform/profile: manifest defaults overlaid with vault + personal values. */
export function resolve(opts: {
  vaultDir: string;
  platform: string;
  profile?: string;
  key: Buffer;
}): Record<string, string>;

/**
 * Seed the resolved values into process.env, only for keys not already set. Returns the keys it set.
 * Silent no-op when there's no vault/key or when `enabled` is false (e.g. in cloud / production).
 */
export function seedEnvironment(opts?: {
  platform: string;
  profile?: string;
  cwd?: string;
  enabled?: boolean;
}): string[];
