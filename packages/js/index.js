// @brettbalmer/vault-reader — read a `vault` encrypted secrets file and seed process.env.
// Framework-agnostic port of the reference reader (see ../../FORMAT.md). Node builtins only.
import { createDecipheriv } from "node:crypto";
import { existsSync, readFileSync } from "node:fs";
import { homedir } from "node:os";
import { dirname, join } from "node:path";

const VERSION = 0x01;

function decodeKey(b64) {
  const b = Buffer.from(String(b64).trim(), "base64");
  return b.length === 32 ? b : null;
}

/** The vault identity from a `#vault:2 id=…` header, or null (legacy/identity-less). */
export function readVaultId(encPath) {
  try {
    const first = readFileSync(encPath, "utf8").split("\n", 1)[0] ?? "";
    if (!first.trimStart().startsWith("#vault:2")) return null;
    for (const tok of first.split(" ").filter(Boolean))
      if (tok.startsWith("id=")) {
        const id = tok.slice(3).trim();
        return id.length ? id : null;
      }
    return null;
  } catch {
    return null;
  }
}

/**
 * Resolve the 32-byte key for a vault identity `id` from the keyring (FORMAT.md §1):
 * $VAULT_KEY → keyring[id] → legacy bare key. Returns a Buffer or null.
 */
export function loadKey(id = null) {
  const inline = process.env.VAULT_KEY;
  if (inline && inline.trim()) return decodeKey(inline);

  const path =
    process.env.VAULT_KEY_FILE && existsSync(process.env.VAULT_KEY_FILE)
      ? process.env.VAULT_KEY_FILE
      : join(homedir(), ".config", "vault", "key");
  if (!existsSync(path)) return null;

  let bare = null;
  for (const raw of readFileSync(path, "utf8").split("\n")) {
    const line = raw.trim();
    if (!line || line.startsWith("#")) continue;
    const sep = line.indexOf("::");
    if (sep < 0) {
      bare ??= decodeKey(line);
      continue;
    }
    if (id !== null && line.slice(0, sep).trim() === id) return decodeKey(line.slice(sep + 2));
  }
  return bare; // legacy bare-key fallback
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

/** Decrypt one `enc:…` value token (AAD = the var name). Per-value crypto, FORMAT.md. */
export function decryptValue(name, token, key) {
  const raw = Buffer.from(token.slice(4), "base64"); // strip "enc:"
  const nonce = raw.subarray(0, 12);
  const tag = raw.subarray(raw.length - 16);
  const cipher = raw.subarray(12, raw.length - 16);
  const d = createDecipheriv("aes-256-gcm", key, nonce);
  d.setAAD(Buffer.from(name, "utf8"));
  d.setAuthTag(tag);
  return Buffer.concat([d.update(cipher), d.final()]).toString("utf8");
}

/** Read a vault file → map. v2 (per-value: plaintext or enc:… tokens) or legacy v1 (whole blob). */
export function readVaultFile(path, key) {
  const text = readFileSync(path, "utf8");
  if (!text.trimStart().startsWith("#vault:2")) return parseEnv(decrypt(text, key)); // legacy v1
  const out = {};
  for (const raw of text.split("\n")) {
    const line = raw.replace(/\r$/, "");
    if (!line || line.startsWith("#")) continue;
    const eq = line.indexOf("=");
    if (eq <= 0) continue;
    const name = line.slice(0, eq);
    const val = line.slice(eq + 1);
    out[name] = val.startsWith("enc:") ? decryptValue(name, val, key) : val;
  }
  return out;
}

/** Parse a KEY=VALUE block (FORMAT.md — plaintext, used for legacy v1 payloads). */
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
  const values = readVaultFile(encPath, key);
  // Per-developer overrides (gitignored personal.enc, layered on top).
  const personalPath = join(vaultDir, "personal.enc");
  if (existsSync(personalPath)) for (const [k, v] of Object.entries(readVaultFile(personalPath, key))) values[k] = v;
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
  if (!vaultDir) return [];
  const key = loadKey(readVaultId(join(vaultDir, `${profile}.enc`)));
  if (!key) return [];
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
