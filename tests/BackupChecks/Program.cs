using System.IO.Compression;
using ServerCharacters;

string root = Path.Combine(Path.GetTempPath(), "ServerCharacters-backup-checks-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
int passed = 0;
void Check(bool value, string label)
{
    if (!value) throw new Exception("FAILED: " + label);
    ++passed;
    Console.WriteLine("PASS: " + label);
}
byte[] BuildSignature(bool baseline)
{
    using var stream = new MemoryStream();
    using var writer = new BinaryWriter(stream);
    writer.Write(80); writer.Write(new byte[80]);
    writer.Write(16); writer.Write(new byte[16]); writer.Write(123L);
    if (baseline) { writer.Write(64); writer.Write(new byte[64]); }
    return stream.ToArray();
}
byte[] signature = BuildSignature(true);
Check(EmergencySignature.TryRead(signature, out var cipher, out var iv, out long timestamp, out var fingerprint, out _) && cipher.Length == 80 && iv.Length == 16 && timestamp == 123 && fingerprint.Length == 64, "current signature decoded");
Check(!EmergencySignature.TryRead(BuildSignature(false), out _, out _, out _, out _, out var legacyReason) && legacyReason.Contains("legacy"), "legacy signature rejected without an exception");
bool truncationsRejected = true;
for (int length = 0; length < signature.Length; length++)
    truncationsRejected &= !EmergencySignature.TryRead(signature[..length], out _, out _, out _, out _, out _);
Check(truncationsRejected, "every truncated signature rejected without an exception");
byte[] badLength = (byte[])signature.Clone();
Array.Copy(BitConverter.GetBytes(int.MaxValue), badLength, 4);
Check(!EmergencySignature.TryRead(badLength, out _, out _, out _, out _, out _), "malicious length rejected without allocation");
Check(!EmergencySignature.TryRead(signature.Concat(new byte[] { 1 }).ToArray(), out _, out _, out _, out _, out _), "trailing signature data rejected");
byte[] data = new byte[200003];
var restedData = new Dictionary<string, string>();
RestedState.Store(restedData, 123.75f, false);
Check(RestedState.Take(restedData, false, out float restedSeconds) && restedSeconds == 123.75f, "Rested preserves remaining duration");
Check(!RestedState.Take(restedData, false, out _), "Rested duration consumed only once");
foreach (float invalid in new[] { 0f, -1f, float.NaN, float.PositiveInfinity })
{
    RestedState.Store(restedData, invalid, false);
    Check(!RestedState.Take(restedData, false, out _), "expired or invalid Rested not persisted: " + invalid);
}
RestedState.Store(restedData, 500, true);
Check(!restedData.ContainsKey(RestedState.Key), "dead player cannot save Rested");
RestedState.Store(restedData, 500, false);
Check(!RestedState.Take(restedData, true, out _) && !restedData.ContainsKey(RestedState.Key), "dead player cannot restore Rested");
restedData[RestedState.Key] = "invalid";
Check(!RestedState.Take(restedData, false, out _), "malformed Rested ignored");
var buffData = new Dictionary<string, string>();
var originalBuffs = new List<BuffState.Entry>
{
    new() { Hash = 123, Remaining = 42.5f, Variant = 3 },
    new() { Hash = 456, Remaining = 17.25f, StateKind = BuffState.Kind.Shield, State = new[] { 200f, 75f } },
    new() { Hash = 789, Remaining = 9f, StateKind = BuffState.Kind.Stats, State = new[] { 1f, 2f, 3f, 4f, 5f, 6f, 7f, 8f } },
};
BuffState.Store(buffData, originalBuffs, false);
string v2Encoded = buffData[BuffState.Key];
Check(Convert.FromBase64String(v2Encoded)[0] == BuffState.CurrentVersion, "new buff saves use the current format");
byte[] v1Bytes = Convert.FromBase64String(v2Encoded);
v1Bytes[0] = 1;
var v1Data = new Dictionary<string, string> { [BuffState.Key] = Convert.ToBase64String(v1Bytes) };
Check(BuffState.Take(v1Data, false, out var v1Buffs) && v1Buffs.Count == 3, "1.4.56 buff format remains readable");
Check(BuffState.Take(buffData, false, out var restoredBuffs) && restoredBuffs.Count == 3, "player buffs round-trip");
Check(restoredBuffs[0].Hash == 123 && restoredBuffs[0].Remaining == 42.5f && restoredBuffs[0].Variant == 3, "generic buff state preserved");
Check(restoredBuffs[1].StateKind == BuffState.Kind.Shield && restoredBuffs[1].State.SequenceEqual(new[] { 200f, 75f }), "shield absorption preserved");
Check(restoredBuffs[2].StateKind == BuffState.Kind.Stats && restoredBuffs[2].State.SequenceEqual(new[] { 1f, 2f, 3f, 4f, 5f, 6f, 7f, 8f }), "potion runtime state preserved");
Check(!BuffState.Take(buffData, false, out _), "player buff snapshot consumed only once");
BuffState.Store(buffData, originalBuffs, true);
Check(!buffData.ContainsKey(BuffState.Key), "dead player cannot save buffs");
buffData[BuffState.Key] = "malformed";
Check(!BuffState.Take(buffData, false, out _) && !buffData.ContainsKey(BuffState.Key), "malformed buff snapshot ignored and consumed");
var legacyBuffData = new Dictionary<string, string>
{
    [RestedState.Key] = "123.5",
    [BuffState.LegacyPoisonDamageKey] = "18.25",
    [BuffState.LegacyPoisonDamageHitKey] = "2.5",
    [BuffState.LegacyPoisonTtlKey] = "14",
};
BuffState.LegacyEffects legacyBuffs = BuffState.TakeLegacy(legacyBuffData, false);
Check(legacyBuffs.HasRested && legacyBuffs.RestedRemaining == 123.5f, "legacy Rested state decoded");
Check(legacyBuffs.HasPoison && legacyBuffs.PoisonDamageLeft == 18.25f && legacyBuffs.PoisonDamagePerHit == 2.5f && legacyBuffs.PoisonRemaining == 14f, "legacy Poison state decoded");
Check(legacyBuffData.Count == 0, "legacy buff keys consumed during migration");
var migratedBuffs = new List<BuffState.Entry>();
BuffState.MergeLegacy(migratedBuffs, legacyBuffs, 1001, 1002, true);
Check(migratedBuffs.Count == 2 && migratedBuffs.Any(effect => effect.StateKind == BuffState.Kind.Rested) && migratedBuffs.Any(effect => effect.StateKind == BuffState.Kind.Poison), "legacy effects migrate into the unified snapshot");
var preferredBuffs = new List<BuffState.Entry> { new() { Hash = 1001, Remaining = 7, StateKind = BuffState.Kind.Rested } };
BuffState.MergeLegacy(preferredBuffs, legacyBuffs, 1001, 1002, true);
Check(preferredBuffs.Count == 2 && preferredBuffs.Single(effect => effect.Hash == 1001).Remaining == 7, "unified effects take precedence over legacy duplicates");
var fallbackData = new Dictionary<string, string> { [BuffState.Key] = "malformed", [RestedState.Key] = "55" };
Check(!BuffState.Take(fallbackData, false, out var fallbackBuffs), "malformed unified snapshot rejected before fallback");
BuffState.MergeLegacy(fallbackBuffs, BuffState.TakeLegacy(fallbackData, false), 1001, 1002, true);
Check(fallbackBuffs.Count == 1 && fallbackBuffs[0].Remaining == 55, "legacy state recovers a malformed unified snapshot");
var cleanedLegacyData = new Dictionary<string, string>
{
    [RestedState.Key] = "10",
    [BuffState.LegacyPoisonDamageKey] = "1",
    [BuffState.LegacyPoisonDamageHitKey] = "1",
    [BuffState.LegacyPoisonTtlKey] = "1",
};
BuffState.Store(cleanedLegacyData, migratedBuffs, false);
Check(cleanedLegacyData.Count == 1 && cleanedLegacyData.ContainsKey(BuffState.Key), "new saves write only the unified buff format");
new Random(1234).NextBytes(data);
using (var left = new ShortReads(data, 113))
using (var right = new ShortReads(data, 701))
    Check(ProfileBackupStore.StreamsHaveEqualContent(left, right), "equal content with different partial-read sizes");
using (var left = new ShortReads(data, 113))
using (var right = new ShortReads(data[..^1], 701))
    Check(!ProfileBackupStore.StreamsHaveEqualContent(left, right), "different lengths detected on nonseekable streams");
byte[] changed = (byte[])data.Clone();
changed[^1] ^= 1;
using (var left = new ShortReads(data, 113))
using (var right = new ShortReads(changed, 701))
    Check(!ProfileBackupStore.StreamsHaveEqualContent(left, right), "last-byte difference detected");

DateTimeOffset start = new(2026, 9, 17, 12, 0, 0, TimeSpan.Zero);
string zip = Path.Combine(root, "player.zip");
Check(ProfileBackupStore.Update(zip, "player", () => data, start), "first backup");
byte[] originalZip = File.ReadAllBytes(zip);
Check(!ProfileBackupStore.Update(zip, "player", () => throw new Exception("source should not be read"), start.AddMinutes(10)), "cooldown avoids source reads");
Check(originalZip.SequenceEqual(File.ReadAllBytes(zip)), "cooldown leaves ZIP bytes unchanged");
Check(!ProfileBackupStore.Update(zip, "player", () => data, start.AddMinutes(31)), "identical backup omitted");
Check(originalZip.SequenceEqual(File.ReadAllBytes(zip)), "identical backup leaves ZIP bytes unchanged");
Check(ProfileBackupStore.Update(zip, "player", () => changed, start.AddMinutes(31)), "changed backup accepted");
byte[] third = data.Concat(new byte[] { 1, 2, 3 }).ToArray();
Check(ProfileBackupStore.Update(zip, "player", () => third, start.AddMinutes(62)), "third generation accepted");
using (var archive = ZipFile.OpenRead(zip))
{
    Check(archive.Entries.Count == 2, "only two backups retained");
    using var stream = archive.Entries.Last().Open();
    using var expected = new MemoryStream(third);
    Check(ProfileBackupStore.StreamsHaveEqualContent(stream, expected), "newest archive data verified");
}
string restarted = Path.Combine(root, "restarted.zip");
File.Copy(zip, restarted);
Check(!ProfileBackupStore.Update(restarted, "player", () => throw new Exception("source should not be read"), start.AddMinutes(65)), "persisted ZIP timestamp enforces cooldown without cache");
byte[] beforeFailure = File.ReadAllBytes(zip);
try
{
    ProfileBackupStore.Update(zip, "player", () => throw new IOException("simulated source failure"), start.AddMinutes(95));
    throw new Exception("Expected simulated failure");
}
catch (IOException) { }
Check(beforeFailure.SequenceEqual(File.ReadAllBytes(zip)), "source failure preserves previous ZIP");
// Force atomic publication to fail on Windows by holding a handle without delete sharing.
if (OperatingSystem.IsWindows())
{
    using (var locked = new FileStream(zip, FileMode.Open, FileAccess.Read, FileShare.Read))
    {
        try
        {
            ProfileBackupStore.Update(zip, "player", () => data, start.AddMinutes(95));
            throw new Exception("Expected locked publication failure");
        }
        catch (IOException) { }
    }
    Check(beforeFailure.SequenceEqual(File.ReadAllBytes(zip)), "failed atomic replacement preserves both previous backups");
    Check(Directory.GetFiles(root, "*.tmp").Length == 0, "failed replacement removes temporary output");
}
string legacy = Path.Combine(root, "legacy.zip");
using (var archive = ZipFile.Open(legacy, ZipArchiveMode.Create))
    for (int i = 0; i < 5; i++)
    {
        var entry = archive.CreateEntry("old-" + i + ".fch");
        entry.LastWriteTime = start.AddMinutes(i).ToLocalTime();
        using var stream = entry.Open();
        stream.Write(data);
    }
Check(!ProfileBackupStore.Update(legacy, "player", () => throw new Exception("source should not be read"), start.AddMinutes(5)), "legacy archive trimmed without new backup");
using (var archive = ZipFile.OpenRead(legacy))
    Check(archive.Entries.Count == 2 && archive.Entries.Any(e => e.Name == "old-4.fch"), "legacy retains newest two");
Console.WriteLine($"{passed} checks passed. Test artifacts: {root}");

sealed class ShortReads(byte[] data, int chunk) : Stream
{
    private int position;
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count)
    {
        int size = Math.Min(Math.Min(count, chunk), data.Length - position);
        Array.Copy(data, position, buffer, offset, size);
        position += size;
        return size;
    }
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
