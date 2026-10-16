using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace ServerCharacters;

// No Unity calls. Old backups are never opened or rewritten during rotation.
internal static class ProfileBackupStore
{
	private const string TimestampFormat = "yyyyMMdd'T'HHmmssfffffff'Z'";
	private static readonly object gate = new();
	private static readonly TimeSpan interval = TimeSpan.FromMinutes(30);

	private sealed class Backup
	{
		public string Path = "";
		public DateTimeOffset SourceModified;
		public DateTimeOffset Created;
	}

	public static bool Update(string legacyPath, string profileName, DateTime sourceModifiedUtc, Func<byte[]> readSource) =>
		Update(legacyPath, profileName, sourceModifiedUtc, readSource, DateTimeOffset.UtcNow);

	internal static bool Update(string legacyPath, string profileName, DateTime sourceModifiedUtc, Func<byte[]> readSource, DateTimeOffset now)
	{
		if (sourceModifiedUtc.Kind != DateTimeKind.Utc) throw new ArgumentException("Source timestamp must be UTC.", nameof(sourceModifiedUtc));
		lock (gate)
		{
			legacyPath = Path.GetFullPath(legacyPath);
			string directory = Path.GetDirectoryName(legacyPath)!;
			string prefix = Path.GetFileNameWithoutExtension(legacyPath) + "_old-";
			DateTimeOffset sourceModified = new(sourceModifiedUtc);
			now = now.ToUniversalTime();
			List<Backup> backups = ReadBackups(directory, prefix);
			Backup? latest = backups.FirstOrDefault();
			// A legacy ZIP has no source timestamp. Keep it intact until two new backups exist.
			// Its filesystem timestamp supplies only the initial cooldown, without opening it.
			DateTimeOffset lastCreated = latest?.Created ??
				(File.Exists(legacyPath) ? new DateTimeOffset(File.GetLastWriteTimeUtc(legacyPath)) : DateTimeOffset.MinValue);
			if (now - lastCreated < interval || (latest != null && sourceModified <= latest.SourceModified))
			{
				Prune(backups, legacyPath);
				return false;
			}

			byte[] candidate = readSource();
			Directory.CreateDirectory(directory);
			string destination = Path.Combine(directory, prefix + sourceModified.ToString(TimestampFormat, CultureInfo.InvariantCulture) +
				"_backup-" + now.ToString(TimestampFormat, CultureInfo.InvariantCulture) + ".zip");
			string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
			try
			{
				using (FileStream output = new(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
				{
					using (ZipArchive archive = new(output, ZipArchiveMode.Create, true))
					{
						ZipArchiveEntry entry = archive.CreateEntry(profileName + ".fch", CompressionLevel.Fastest);
						entry.LastWriteTime = now;
						using Stream target = entry.Open();
						target.Write(candidate, 0, candidate.Length);
					}
					output.Flush(true);
				}
				// Verify only the newly created archive before publication.
				using (FileStream checkFile = File.OpenRead(temporary))
				using (ZipArchive check = new(checkFile, ZipArchiveMode.Read))
				{
					if (check.Entries.Count != 1) throw new InvalidDataException("Unexpected backup entry count.");
					using Stream stored = check.Entries[0].Open();
					using MemoryStream expected = new(candidate, false);
					if (!StreamsHaveEqualContent(expected, stored))
						throw new InvalidDataException("The new backup failed content verification.");
				}
				File.Move(temporary, destination);
				backups.Insert(0, new Backup { Path = destination, SourceModified = sourceModified, Created = now });
				// Deletion happens only after the new file is fully verified and published.
				Prune(backups, legacyPath);
				return true;
			}
			finally
			{
				if (File.Exists(temporary))
				{
					try { File.Delete(temporary); }
					catch (IOException) { }
					catch (UnauthorizedAccessException) { }
				}
			}
		}
	}

	private static List<Backup> ReadBackups(string directory, string prefix)
	{
		List<Backup> backups = new();
		if (!Directory.Exists(directory)) return backups;
		foreach (string path in Directory.EnumerateFiles(directory, prefix + "*.zip"))
		{
			string name = Path.GetFileNameWithoutExtension(path);
			if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
			string[] dates = name.Substring(prefix.Length).Split(new[] { "_backup-" }, StringSplitOptions.None);
			if (dates.Length != 2 ||
				!DateTimeOffset.TryParseExact(dates[0], TimestampFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTimeOffset source) ||
				!DateTimeOffset.TryParseExact(dates[1], TimestampFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTimeOffset created)) continue;
			backups.Add(new Backup { Path = path, SourceModified = source, Created = created });
		}
		return backups.OrderByDescending(b => b.Created).ThenByDescending(b => b.SourceModified).ToList();
	}

	private static void Prune(List<Backup> backups, string legacyPath)
	{
		// A failed deletion leaves extra recovery files and is retried on the next update.
		foreach (Backup backup in backups.Skip(2)) File.Delete(backup.Path);
		if (backups.Count >= 2 && File.Exists(legacyPath)) File.Delete(legacyPath);
	}

	internal static bool StreamsHaveEqualContent(Stream left, Stream right)
	{
		if (left.CanSeek && right.CanSeek && left.Length - left.Position != right.Length - right.Position) return false;
		byte[] leftBuffer = new byte[81920];
		byte[] rightBuffer = new byte[81920];
		while (true)
		{
			int leftRead = ReadBlock(left, leftBuffer);
			int rightRead = ReadBlock(right, rightBuffer);
			if (leftRead != rightRead) return false;
			if (leftRead == 0) return true;
			for (int i = 0; i < leftRead; ++i)
				if (leftBuffer[i] != rightBuffer[i]) return false;
		}
	}

	private static int ReadBlock(Stream stream, byte[] buffer)
	{
		int total = 0;
		while (total < buffer.Length)
		{
			int count = stream.Read(buffer, total, buffer.Length - total);
			if (count == 0) break;
			total += count;
		}
		return total;
	}
}
