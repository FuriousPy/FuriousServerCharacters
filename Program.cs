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
DateTime Source(int minutes) => start.AddMinutes(minutes).UtcDateTime;
string zip = Path.Combine(root, "player.zip");
string[] Backups(string directory, string name = "player") => Directory.GetFiles(directory, name + "_old-*.zip").OrderBy(p => p, StringComparer.Ordinal).ToArray();
bool Save(int minutes, int sourceMinutes, Func<byte[]> read) =>
    ProfileBackupStore.Update(zip, "player", Source(sourceMinutes), read, start.AddMinutes(minutes));
byte[] MustNotRead() => throw new Exception("source should not be read");
Check(Save(0, -1, () => data), "first backup");
string firstZip = Backups(root).Single();
byte[] originalZip = File.ReadAllBytes(firstZip);
Check(firstZip.EndsWith("player_old-20260917T1159000000000Z_backup-20260917T1200000000000Z.zip"), "both UTC timestamps persisted in filename");
Check(!Save(29, 20, MustNotRead), "cooldown avoids reading source");
Check(!Save(30, -1, MustNotRead), "same source timestamp skipped at cooldown boundary");
Check(!Save(31, -2, MustNotRead), "older source timestamp skipped");
Check(Save(30, 20, () => data), "new timestamp accepted at exactly 30 minutes even with identical content");
Check(originalZip.SequenceEqual(File.ReadAllBytes(firstZip)), "old backup remains byte-for-byte unchanged");
string secondZip = Backups(root).Last();
byte[] secondBytes = File.ReadAllBytes(secondZip);
if (OperatingSystem.IsWindows())
{
    using var locked = new FileStream(secondZip, FileMode.Open, FileAccess.Read, FileShare.None);
    Check(Save(60, 50, () => changed), "rotation does not open previous backup even when exclusively locked");
}
else Check(Save(60, 50, () => changed), "third generation accepted");
Check(Backups(root).Length == 2 && !File.Exists(firstZip), "only newest two independent ZIPs retained");
Check(secondBytes.SequenceEqual(File.ReadAllBytes(secondZip)), "retained backup never rewritten");
using (var archive = ZipFile.OpenRead(Backups(root).Last()))
{
    Check(archive.Entries.Count == 1, "one profile per ZIP");
    using var stream = archive.Entries[0].Open();
    using var expected = new MemoryStream(changed);
    Check(ProfileBackupStore.StreamsHaveEqualContent(stream, expected), "new backup content verified");
}
string restartDir = Path.Combine(root, "restart");
Directory.CreateDirectory(restartDir);
foreach (string file in Backups(root))
{
    string copy = Path.Combine(restartDir, Path.GetFileName(file));
    File.Copy(file, copy);
    File.SetLastWriteTimeUtc(copy, Source(-10000));
}
string restarted = Path.Combine(restartDir, "player.zip");
Check(!ProfileBackupStore.Update(restarted, "player", Source(80), MustNotRead, start.AddMinutes(65)), "filename cooldown survives restart and filesystem timestamp changes");
Check(!ProfileBackupStore.Update(restarted, "player", Source(50), MustNotRead, start.AddMinutes(95)), "source timestamp survives restart without in-memory cache");
string[] beforeFailure = Backups(root);
try
{
    Save(95, 90, () => throw new IOException("simulated source failure"));
    throw new Exception("Expected simulated failure");
}
catch (IOException) { }
Check(beforeFailure.SequenceEqual(Backups(root)), "source failure preserves existing backups");
string blockedDestination = Path.Combine(root, "player_old-20260917T1330000000000Z_backup-20260917T1335000000000Z.zip");
Directory.CreateDirectory(blockedDestination);
try
{
    Save(95, 90, () => data);
    throw new Exception("Expected publication failure");
}
catch (IOException) { }
Check(beforeFailure.SequenceEqual(Backups(root)), "failed publication preserves both backups");
Check(Directory.GetFiles(root, "*.tmp").Length == 0, "failed publication removes temporary file");
Directory.Delete(blockedDestination);
string unrelated = Path.Combine(root, "player_old-invalid_backup-invalid.zip");
File.WriteAllText(unrelated, "not a managed backup");
Check(Save(95, 90, () => data), "successful publication after failure");
Check(File.Exists(unrelated), "unrecognized names are not deleted");
string legacy = Path.Combine(root, "legacy.zip");
using (var archive = ZipFile.Open(legacy, ZipArchiveMode.Create))
{
    var entry = archive.CreateEntry("older.fch");
    using var stream = entry.Open();
    stream.Write(data);
}
File.SetLastWriteTimeUtc(legacy, Source(0));
byte[] legacyBytes = File.ReadAllBytes(legacy);
Check(!ProfileBackupStore.Update(legacy, "legacy", Source(10), MustNotRead, start.AddMinutes(10)), "legacy cooldown uses filesystem metadata only");
Check(ProfileBackupStore.Update(legacy, "legacy", Source(20), () => data, start.AddMinutes(30)), "first new-format backup beside legacy ZIP");
Check(File.Exists(legacy) && legacyBytes.SequenceEqual(File.ReadAllBytes(legacy)), "legacy retained intact until two new backups exist");
Check(ProfileBackupStore.Update(legacy, "legacy", Source(50), () => changed, start.AddMinutes(60)), "second new-format backup created");
Check(!File.Exists(legacy) && Backups(root, "legacy").Length == 2, "legacy retired only after two verified backups");
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
