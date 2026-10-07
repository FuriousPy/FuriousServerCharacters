using System;
using System.Collections.Generic;
using System.IO;

namespace ServerCharacters;

internal static class BuffState
{
	internal const string Key = "ServerCharacters PlayerBuffs";
	internal const int MaxEffects = 256;
	internal const int MaxStateValues = 16;

	internal enum Kind : byte
	{
		Generic,
		Shield,
		Stats,
		React,
		Puke,
	}

	internal sealed class Entry
	{
		internal int Hash;
		internal float Remaining;
		internal short Variant;
		internal Kind StateKind;
		internal float[] State = Array.Empty<float>();
	}

	internal static bool Valid(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

	internal static void Store(Dictionary<string, string> data, IReadOnlyList<Entry> effects, bool dead)
	{
		data.Remove(Key);
		if (dead || effects.Count == 0) return;
		using MemoryStream stream = new();
		using (BinaryWriter writer = new(stream))
		{
			writer.Write((byte)1);
			writer.Write((ushort)Math.Min(effects.Count, MaxEffects));
			for (int i = 0; i < effects.Count && i < MaxEffects; ++i)
			{
				Entry effect = effects[i];
				writer.Write(effect.Hash);
				writer.Write(effect.Remaining);
				writer.Write(effect.Variant);
				writer.Write((byte)effect.StateKind);
				int stateCount = Math.Min(effect.State.Length, MaxStateValues);
				writer.Write((byte)stateCount);
				for (int j = 0; j < stateCount; ++j) writer.Write(effect.State[j]);
			}
		}
		data[Key] = Convert.ToBase64String(stream.ToArray());
	}

	internal static bool Take(Dictionary<string, string> data, bool dead, out List<Entry> effects)
	{
		effects = new List<Entry>();
		if (!data.TryGetValue(Key, out string encoded)) return false;
		data.Remove(Key); // A saved snapshot is consumed once, including malformed data.
		if (dead || string.IsNullOrEmpty(encoded) || encoded.Length > 65536) return false;
		try
		{
			byte[] bytes = Convert.FromBase64String(encoded);
			using MemoryStream stream = new(bytes, false);
			using BinaryReader reader = new(stream);
			if (reader.ReadByte() != 1) return false;
			int count = reader.ReadUInt16();
			if (count > MaxEffects) return false;
			for (int i = 0; i < count; ++i)
			{
				Entry effect = new()
				{
					Hash = reader.ReadInt32(),
					Remaining = reader.ReadSingle(),
					Variant = reader.ReadInt16(),
					StateKind = (Kind)reader.ReadByte(),
				};
				int stateCount = reader.ReadByte();
				if (stateCount > MaxStateValues || !Enum.IsDefined(typeof(Kind), effect.StateKind)) return false;
				effect.State = new float[stateCount];
				for (int j = 0; j < stateCount; ++j)
				{
					effect.State[j] = reader.ReadSingle();
					if (!Valid(effect.State[j])) return false;
				}
				if (effect.Hash == 0 || effect.Remaining <= 0 || !Valid(effect.Remaining)) return false;
				effects.Add(effect);
			}
			return stream.Position == stream.Length && effects.Count > 0;
		}
		catch (Exception)
		{
			effects.Clear();
			return false;
		}
	}
}
