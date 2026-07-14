/** @brettabalmer/vault-reader — types. See FORMAT.md. */
export function loadKey(): Buffer | null;
export function findVaultDir(start?: string): string | null;
export function decrypt(base64Envelope: string, key: Buffer): string;
export function parseEnv(text: string): Record<string, string>;
export function resolve(opts: {
  vaultDir: string;
  platform: string;
  profile?: string;
  key: Buffer;
}): Record<string, string>;
export function seedEnvironment(opts?: {
  platform: string;
  profile?: string;
  cwd?: string;
  enabled?: boolean;
}): string[];
