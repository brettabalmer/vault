// @brettabalmer/vault-reader — read a `vault` encrypted secrets file and seed process.env.
// Framework-agnostic port of the reference reader (see ../../FORMAT.md). Node builtins only.
import { createDecipheriv } from "node:crypto";
import { existsSync, readFileSync } from "node:fs";
import { homedir } from "node:os";
import { dirname, join } from "node:path";

const VERSION = 0x01;

/** Resolve the 32-byte key from $VAULT_KEY or ~/.config/vault/key. Returns a Buffer or null. */
export function loadKey() {
  const inline = process.env.VAULT_KEY;
  if (inline && inline.trim()) {
    const b = Buffer.from(inline.trim(), "base64");
    return b.length === 32 ? b : null;
  }
  const path = join(homedir(), ".config", "vault", "key");
  if (!existsSync(path)) return null;
  const b = Buffer.from(readFileSync(path, "utf8").trim(), "base64");
  return b.length === 32 ? b : null;
}

/** Walk up from `start` (default cwd) for a `vault/manifest.json`; returns the `vault/` dir or null. */
export function findVaultDir(start = process.cwd()) {
  const explicit = process.env.VAULT_DIR;
  if (explicit && existsSync(join(explicit, "manifest.json"))) return explicit;
  let dir = start;
  for (;;) {
    if (existsSync(join(dir, "vault", "manifest.json"))) return join(dir, "vault");
    const parent = dirname(dir);
    if (parent === dir) return null;
    dir = parent;
  }
}

/** Decrypt a base64 vault envelope (FORMAT.md §2) to its plaintext. Throws on tamper/wrong key. */
export function decrypt(base64Envelope, key) {
  const env = Buffer.from(String(base64Envelope).replace(/\s+/g, ""), "base64");
  if (env.length < 1 + 12 + 16 || env[0] !== VERSION) throw new Error("bad vault envelope");
  const nonce = env.subarray(1, 13);
  const tag = env.subarray(env.length - 16);
  const cipher = env.subarray(13, env.length - 16);
  const d = createDecipheriv("aes-256-gcm", key, nonce);
  d.setAAD(Buffer.from([VERSION]));
  d.setAuthTag(tag);
  return Buffer.concat([d.update(cipher), d.final()]).toString("utf8");
}

/** Parse a KEY=VALUE block (FORMAT.md §3). */
export function parseEnv(text) {
  const out = {};
  for (const raw of String(text).split("\n")) {
    const line = raw.trim();
    if (!line || line.startsWith("#")) continue;
    const eq = line.indexOf("=");
    if (eq <= 0) continue;
    out[line.slice(0, eq).trim()] = line.slice(eq + 1).trim();
  }
  return out;
}

/**
 * Resolve the {KEY:VALUE} map a reader for `platform`/`profile` should surface (FORMAT.md §5):
 * manifest defaults overlaid with vault values, filtered by platforms/profiles.
 */
export function resolve({ vaultDir, platform, profile = "local", key }) {
  const encPath = join(vaultDir, `${profile}.enc`);
  const manifestPath = join(vaultDir, "manifest.json");
  if (!existsSync(encPath) || !existsSync(manifestPath)) return {};
  const values = parseEnv(decrypt(readFileSync(encPath, "utf8"), key));
  const vars = JSON.parse(readFileSync(manifestPath, "utf8")).vars ?? [];
  const out = {};
  for (const v of vars) {
    if (!v.key) continue;
    const platformOk = !v.platforms?.length || v.platforms.includes(platform);
    const profileOk = !v.profiles?.length || v.profiles.includes(profile);
    if (!platformOk || !profileOk) continue;
    const value = v.key in values ? values[v.key] : (v.default ?? null);
    if (value !== null && value !== undefined) out[v.key] = value;
  }
  return out;
}

/**
 * Seed the resolved values into process.env, only for keys not already set. Returns the keys it set.
 * Silent no-op when there's no vault or key. Gate `enabled:false` (e.g. in cloud) to skip entirely.
 */
export function seedEnvironment({ platform, profile = "local", cwd, enabled = true } = {}) {
  if (!enabled || process.env.WEBSITE_INSTANCE_ID) return [];
  const vaultDir = findVaultDir(cwd);
  const key = loadKey();
  if (!vaultDir || !key) return [];
  const map = resolve({ vaultDir, platform, profile, key });
  const set = [];
  for (const [k, val] of Object.entries(map)) {
    if (process.env[k] === undefined) {
      process.env[k] = val;
      set.push(k);
    }
  }
  return set;
}
