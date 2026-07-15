// Cross-language conformance: the JS reader must decrypt the committed testvectors identically to the
// C# reference (FORMAT.md contract). Run with `node --test`.
import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync, readdirSync, existsSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { dirname, join } from "node:path";
import { readVaultFile, decryptValue, resolve } from "./index.js";

const here = dirname(fileURLToPath(import.meta.url));
const vectorsDir = join(here, "..", "..", "testvectors");

for (const name of readdirSync(vectorsDir, { withFileTypes: true }).filter((d) => d.isDirectory())) {
  const dir = join(vectorsDir, name.name);
  if (!existsSync(join(dir, "expected.json"))) continue;

  test(`vector: ${name.name} decrypts to expected`, () => {
    const key = Buffer.from(readFileSync(join(dir, "key.txt"), "utf8").trim(), "base64");
    const expected = JSON.parse(readFileSync(join(dir, "expected.json"), "utf8"));

    const got = readVaultFile(join(dir, "vault", "local.enc"), key);
    assert.deepEqual(got, expected.vault);

    const map = resolve({
      vaultDir: join(dir, "vault"),
      platform: expected.resolved.platform,
      profile: expected.resolved.profile,
      key,
    });
    assert.deepEqual(map, expected.resolved.map);
  });

  test(`vector: ${name.name} tampering fails`, () => {
    const key = Buffer.from(readFileSync(join(dir, "key.txt"), "utf8").trim(), "base64");
    const line = readFileSync(join(dir, "vault", "local.enc"), "utf8").split("\n").find((l) => l.includes("=enc:"));
    const eq = line.indexOf("=");
    const name2 = line.slice(0, eq);
    const raw = Buffer.from(line.slice(eq + 1 + 4), "base64"); // strip "enc:"
    raw[Math.floor(raw.length / 2)] ^= 0x01;
    assert.throws(() => decryptValue(name2, "enc:" + raw.toString("base64"), key));
  });
}
