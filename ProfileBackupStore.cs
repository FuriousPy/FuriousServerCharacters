using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace ServerCharacters;

// Contains no Unity calls: archive publication is complete before the old ZIP is replaced.
internal static class ProfileBackupStore
{
	private sealed class Stamp
	{
		public long Length;
		public DateTime Modified;
		public DateTimeOffset LastBackup;
	}

	private static readonly Dictionary<string, Stamp> cache = new(StringComparer.OrdinalIgnoreCase);
	private static readonly object gate = new();
	private static readonly TimeSpan interval = TimeSpan.FromMinutes(30);

	public static bool Update(string path, string profileName, Func<byte[]> readSource) =>
		Update(path, profileName, readSource, DateTimeOffset.Now);

	internal static bool Update(string path, string profileName, Func<byte[]> readSource, DateTimeOffset now)
	{
		lock (gate)
		{
			FileInfo info = new(path);
			if (info.Exists && cache.TryGetValue(path, out Stamp? stamp) &&
				stamp.Length == info.Length && stamp.Modified == info.LastWriteTimeUtc &&
				now - stamp.LastBackup < interval) return false;

			using FileStream? existingFile = info.Exists ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete) : null;
			using ZipArchive? existing = existingFile != null ? new ZipArchive(existingFile, ZipArchiveMode.Read) : null;
			List<ZipArchiveEntry> entries = existing?.Entries.OrderByDescending(e => e.LastWriteTime).ToList() ?? new();
			ZipArchiveEntry? latest = entries.FirstOrDefault();
			// ZIP timestamps have two-second precision; round conservatively after a restart.
			DateTimeOffset lastBackup = latest?.LastWriteTime.AddSeconds(2) ?? DateTimeOffset.MinValue;
			byte[]? candidate = null;
			bool create = latest == null || now - lastBackup >= interval;
			if (create)
			{
				candidate = readSource();
				if (latest != null)
				{
					using Stream previous = latest.Open();
					using MemoryStream current = new(candidate, false);
					create = !StreamsHaveEqualContent(current, previous);
				}
			}

			if (!create && entries.Count <= 2)
			{
				Remember(path, lastBackup);
				return false;
			}

			Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
			string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
			try
			{
				using (FileStream output = new(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
				{
					using (ZipArchive replacement = new(output, ZipArchiveMode.Create, true))
					{
						foreach (ZipArchiveEntry entry in entries.Take(create ? 1 : 2).Reverse())
						{
							ZipArchiveEntry copy = replacement.CreateEntry(entry.FullName, CompressionLevel.Optimal);
							copy.LastWriteTime = entry.LastWriteTime;
							using Stream input = entry.Open();
							using Stream target = copy.Open();
							input.CopyTo(target);
						}
						if (create)
						{
							ZipArchiveEntry added = replacement.CreateEntry(profileName + "-" + now.ToString("yyyy-MM-ddTHH-mm-ss") + ".fch", CompressionLevel.Optimal);
							added.LastWriteTime = now.ToLocalTime();
							using Stream target = added.Open();
							target.Write(candidate!, 0, candidate!.Length);
						}
					}
					output.Flush(true);
				}

				using (FileStream checkFile = File.OpenRead(temporary))
				using (ZipArchive check = new(checkFile, ZipArchiveMode.Read))
				{
					if (check.Entries.Count != Math.Min(entries.Count + (create ? 1 : 0), 2))
						throw new InvalidDataException("Unexpected number of entries in the replacement backup ZIP.");
					if (create)
					{
						using Stream stored = check.Entries.Last().Open();
						using MemoryStream expected = new(candidate!, false);
						if (!StreamsHaveEqualContent(expected, stored))
							throw new InvalidDataException("The replacement backup failed content verification.");
					}
				}

				// Existing readers allow delete sharing; no truncate of the original ZIP.
				if (info.Exists) File.Replace(temporary, path, null);
				else File.Move(temporary, path);
				Remember(path, create ? now : lastBackup);
				return create;
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

	private static void Remember(string path, DateTimeOffset lastBackup)
	{
		FileInfo file = new(path);
		if (file.Exists) cache[path] = new Stamp { Length = file.Length, Modified = file.LastWriteTimeUtc, LastBackup = lastBackup };
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
