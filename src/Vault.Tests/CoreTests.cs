using System.Text.Json;
using Vault.Core;
using Xunit;

namespace Vault.Tests;

public class CryptoTests
{
    private static byte[] Key() => Convert.FromBase64String("AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=");

    [Fact]
    public void RoundTrips()
    {
        var key = Key();
        var plain = "A=1\nB=hello world\nC=has=equals;and;semis\n";
        var enc = Crypto.Encrypt(plain, key);
        Assert.Equal(plain, Crypto.Decrypt(enc, key));
    }

    [Fact]
    public void FreshNoncePerEncrypt()
    {
        var key = Key();
        Assert.NotEqual(Crypto.Encrypt("X=1", key), Crypto.Encrypt("X=1", key));
    }

    [Fact]
    public void WrongKeyThrows()
    {
        var enc = Crypto.Encrypt("X=1", Key());
        var other = new byte[32];
        Assert.Throws<VaultCryptoException>(() => Crypto.Decrypt(enc, other));
    }

    [Fact]
    public void TamperedCiphertextThrows()
    {
        var key = Key();
        var enc = Crypto.Encrypt("X=secret", key);
        var bytes = Convert.FromBase64String(enc);
        bytes[^1] ^= 0xFF; // flip a tag byte
        Assert.Throws<VaultCryptoException>(() => Crypto.Decrypt(Convert.ToBase64String(bytes), key));
    }

    [Fact]
    public void PerValueRoundTripsAndIsDeterministic()
    {
        var key = Key();
        var t1 = Crypto.EncryptValue("COSMOS_KEY", "s3cr3t", key);
        var t2 = Crypto.EncryptValue("COSMOS_KEY", "s3cr3t", key);
        Assert.True(Crypto.IsEncrypted(t1));
        Assert.Equal(t1, t2); // deterministic → stable git diffs
        Assert.Equal("s3cr3t", Crypto.DecryptValue("COSMOS_KEY", t1, key));
    }

    [Fact]
    public void PerValueAadBindsTheName()
    {
        var key = Key();
        var t = Crypto.EncryptValue("A", "v", key);
        // A token encrypted under name "A" must not decrypt under name "B" (AAD mismatch).
        Assert.Throws<VaultCryptoException>(() => Crypto.DecryptValue("B", t, key));
    }

    [Fact]
    public void ToleratesWhitespaceInBase64()
    {
        var key = Key();
        var original = "X=1\nY=2";
        var wrapped = string.Join("\n", Chunk(Crypto.Encrypt(original, key), 10));
        Assert.Equal(original, Crypto.Decrypt(wrapped, key));
    }

    private static IEnumerable<string> Chunk(string s, int n)
    {
        for (int i = 0; i < s.Length; i += n) yield return s.Substring(i, Math.Min(n, s.Length - i));
    }
}

public class EnvTextTests
{
    [Fact]
    public void SplitsOnFirstEquals()
    {
        var m = EnvText.Parse("CONN=Endpoint=https://x;Key=abc==;\n# comment\n\nEMPTY=");
        Assert.Equal("Endpoint=https://x;Key=abc==;", m["CONN"]);
        Assert.Equal("", m["EMPTY"]);
        Assert.False(m.ContainsKey("# comment"));
    }

    [Fact]
    public void SerializeIsSortedAndRoundTrips()
    {
        var m = new SortedDictionary<string, string>(StringComparer.Ordinal) { ["B"] = "2", ["A"] = "1" };
        Assert.Equal("A=1\nB=2\n", EnvText.Serialize(m));
        Assert.Equal(m, EnvText.Parse(EnvText.Serialize(m)));
    }

    [Theory]
    [InlineData("GOOD_KEY1", true)]
    [InlineData("_leading", true)]
    [InlineData("1leading", false)]
    [InlineData("has-dash", false)]
    [InlineData("", false)]
    public void KeyValidation(string key, bool valid) => Assert.Equal(valid, EnvText.IsValidKey(key));
}

public class KeyringTests
{
    private static byte[] K(byte b) { var k = new byte[32]; Array.Fill(k, b); return k; }

    [Fact]
    public void ParsesPairingsAndLegacyBare()
    {
        var text = $"# comment\n{"aaaa"} :: {Convert.ToBase64String(K(1))}\nbbbb :: {Convert.ToBase64String(K(2))}\n{Convert.ToBase64String(K(9))}\n";
        var ring = KeyStore.Parse(text);
        Assert.Equal(2, ring.ById.Count);
        Assert.Equal(K(1), ring.ById["aaaa"]);
        Assert.Equal(K(2), ring.ById["bbbb"]);
        Assert.Equal(K(9), ring.LegacyBare);
    }

    [Fact]
    public void ParsesEmptyToNothing()
    {
        var ring = KeyStore.Parse("# just a comment\n\n");
        Assert.Empty(ring.ById);
        Assert.Null(ring.LegacyBare);
    }
}

public class IdentityTests
{
    private static byte[] Key() => Convert.FromBase64String("AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=");

    [Theory]
    [InlineData("#vault:2", null)]
    [InlineData("#vault:2 id=abc123", "abc123")]
    [InlineData("#vault:2 id=abc123 name=foo", "abc123")]
    [InlineData("not a header", null)]
    public void ParseIdReadsHeaderToken(string header, string? expected)
        => Assert.Equal(expected, VaultFile.ParseId(header));

    [Fact]
    public void WriteStampsIdAndReadRoundTrips()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vault-idtest-" + Guid.NewGuid().ToString("N"));
        try
        {
            var file = new VaultFile(dir, "local");
            var map = new SortedDictionary<string, string>(StringComparer.Ordinal) { ["A"] = "1", ["S"] = "shh" };
            file.Write(Key(), map, n => n == "S", "deadbeef01");
            Assert.Equal("deadbeef01", file.ReadId());
            var got = file.Read(Key());
            Assert.Equal("1", got["A"]);
            Assert.Equal("shh", got["S"]);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void ReadPlaintextOnlyDropsSecrets()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vault-idtest-" + Guid.NewGuid().ToString("N"));
        try
        {
            var file = new VaultFile(dir, "local");
            file.Write(Key(), new SortedDictionary<string, string>(StringComparer.Ordinal) { ["A"] = "1", ["S"] = "shh" },
                n => n == "S", "id01");
            var map = VaultIdentity.ReadPlaintextOnly(file, out var dropped);
            Assert.Equal("1", map["A"]);
            Assert.False(map.ContainsKey("S"));   // encrypted → unrecoverable without the key
            Assert.Contains("S", dropped);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void NewIdIsSixteenHexChars()
    {
        var id = VaultIdentity.NewId();
        Assert.Equal(16, id.Length);
        Assert.Matches("^[0-9a-f]{16}$", id);
    }
}

public class ManifestEditTests
{
    [Fact]
    public void LoadDocMissingReturnsEmptyUnlessMustExist()
    {
        var path = Path.Combine(Path.GetTempPath(), "vault-nomani-" + Guid.NewGuid().ToString("N"), "manifest.json");
        var doc = Manifest.LoadDoc(path);          // mustExist:false
        Assert.Empty(doc.Vars);
        Assert.Throws<FileNotFoundException>(() => Manifest.LoadDoc(path, mustExist: true));
    }

    [Fact]
    public void SaveAndLoadRoundTripsSortedByKey()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vault-mani-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "manifest.json");
        try
        {
            var doc = new ManifestDoc();
            doc.Vars.Add(new ManifestVar { Key = "ZED", Secret = true });
            doc.Vars.Add(new ManifestVar { Key = "ALPHA", Secret = false, Required = true, Category = "C" });
            Manifest.SaveDoc(path, doc);

            var back = Manifest.LoadDoc(path);
            Assert.Equal(new[] { "ALPHA", "ZED" }, back.Vars.Select(v => v.Key).ToArray()); // sorted
            var alpha = back.Vars.First(v => v.Key == "ALPHA");
            Assert.True(alpha.Required);
            Assert.False(alpha.Secret);
            Assert.Equal("C", alpha.Category);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
}

/// <summary>Decrypts the committed testvectors and asserts against expected.json (the cross-language contract).</summary>
public class ConformanceTests
{
    public static IEnumerable<object[]> Vectors()
    {
        var dir = Path.Combine(RepoRoot(), "testvectors");
        foreach (var d in Directory.EnumerateDirectories(dir))
            if (File.Exists(Path.Combine(d, "expected.json")))
                yield return new object[] { d };
    }

    [Theory]
    [MemberData(nameof(Vectors))]
    public void DecryptsToExpected(string vectorDir)
    {
        var key = Convert.FromBase64String(File.ReadAllText(Path.Combine(vectorDir, "key.txt")).Trim());
        using var expected = JsonDocument.Parse(File.ReadAllText(Path.Combine(vectorDir, "expected.json")));

        // 1. raw decrypt
        var vaultFile = new VaultFile(Path.Combine(vectorDir, "vault"), "local");
        var got = vaultFile.Read(key);
        foreach (var p in expected.RootElement.GetProperty("vault").EnumerateObject())
            Assert.Equal(p.Value.GetString(), got[p.Name]);
        Assert.Equal(expected.RootElement.GetProperty("vault").EnumerateObject().Count(), got.Count);

        // 2. resolution
        var manifest = Manifest.Load(Path.Combine(vectorDir, "vault", "manifest.json"));
        var res = expected.RootElement.GetProperty("resolved");
        var map = Resolve.ForPlatform(manifest, got, res.GetProperty("platform").GetString()!, res.GetProperty("profile").GetString()!);
        foreach (var p in res.GetProperty("map").EnumerateObject())
            Assert.Equal(p.Value.GetString(), map[p.Name]);
        Assert.Equal(res.GetProperty("map").EnumerateObject().Count(), map.Count);
    }

    [Theory]
    [MemberData(nameof(Vectors))]
    public void TamperingFails(string vectorDir)
    {
        var key = Convert.FromBase64String(File.ReadAllText(Path.Combine(vectorDir, "key.txt")).Trim());
        var line = File.ReadAllLines(Path.Combine(vectorDir, "vault", "local.enc")).First(l => l.Contains("=enc:"));
        var eq = line.IndexOf('=');
        var name = line[..eq];
        var raw = Convert.FromBase64String(line[(eq + 1 + Crypto.EncPrefix.Length)..]);
        raw[raw.Length / 2] ^= 0x01; // flip a ciphertext byte
        Assert.Throws<VaultCryptoException>(() => Crypto.DecryptValue(name, Crypto.EncPrefix + Convert.ToBase64String(raw), key));
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "FORMAT.md"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("Could not locate repo root (FORMAT.md).");
    }
}
