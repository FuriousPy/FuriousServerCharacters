using System;
using System.IO;

namespace ServerCharacters;

internal static class EmergencySignature
{
	// ZPackage byte arrays use an Int32 length followed by the bytes.
	internal static bool TryRead(byte[] signature, out byte[] encryptedHash, out byte[] iv,
		out long time, out byte[] baseline, out string reason)
	{
		encryptedHash = iv = baseline = Array.Empty<byte>();
		time = 0;
		reason = "incomplete or invalid signature";
		if (signature == null || signature.Length > 256) return false;
		using var stream = new MemoryStream(signature, false);
		using var reader = new BinaryReader(stream);
		bool ReadBytes(int length, out byte[] value)
		{
			value = Array.Empty<byte>();
			if (stream.Length - stream.Position < sizeof(int)) return false;
			if (reader.ReadInt32() != length || stream.Length - stream.Position < length) return false;
			value = reader.ReadBytes(length);
			return true;
		}
		if (!ReadBytes(80, out encryptedHash) || !ReadBytes(16, out iv) || stream.Length - stream.Position < sizeof(long)) return false;
		time = reader.ReadInt64();
		if (stream.Position == stream.Length)
		{
			reason = "legacy signature without a confirmed-profile fingerprint; backup retained, automatic recovery skipped";
			return false;
		}
		if (!ReadBytes(64, out baseline) || stream.Position != stream.Length) return false;
		reason = "";
		return true;
	}
}
