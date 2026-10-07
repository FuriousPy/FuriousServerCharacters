using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace ServerCharacters;

internal static class BuffState
{
	internal const string Key = "ServerCharacters PlayerBuffs";
	internal const byte CurrentVersion = 2;
	internal const int MaxEffects = 256;
	internal const int MaxStateValues = 16;
	internal const string LegacyPoisonDamageKey = "ServerCharacters PoisonDamage";
	internal const string LegacyPoisonDamageHitKey = "ServerCharacters PoisonDamageHit";
	internal const string LegacyPoisonTtlKey = "ServerCharacters PoisonTTL";

	internal enum Kind : byte
	{
		Generic,
		Shield,
		Stats,
		React,
		Puke,
		Rested,
		Poison,
	}

	internal sealed class Entry
	{
		internal int Hash;
		internal float Remaining;
		internal short Variant;
		internal Kind StateKind;
		internal float[] State = Array.Empty<float>();
	}

	internal sealed class LegacyEffects
	{
		internal bool HasRested;
		internal float RestedRemaining;
		internal bool HasPoison;
		internal float PoisonRemaining;
		internal float PoisonDamageLeft;
		internal float PoisonDamagePerHit;
	}

	internal static bool Valid(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

	internal static void Store(Dictionary<string, string> data, IReadOnlyList<Entry> effects, bool dead)
	{
		data.Remove(Key);
		RemoveLegacy(data);
		if (dead || effects.Count == 0) return;
		using MemoryStream stream = new();
		using (BinaryWriter writer = new(stream))
		{
			writer.Write(CurrentVersion);
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
		if (!data.TryGetValue(Key, out string? encoded)) return false;
		data.Remove(Key); // A saved snapshot is consumed once, including malformed data.
		if (dead || string.IsNullOrEmpty(encoded) || encoded.Length > 65536) return false;
		try
		{
			byte[] bytes = Convert.FromBase64String(encoded);
			using MemoryStream stream = new(bytes, false);
			using BinaryReader reader = new(stream);
			byte version = reader.ReadByte();
			if (version != 1 && version != CurrentVersion) return false;
			int count = reader.ReadUInt16();
			if (count > MaxEffects) return false;
			List<Entry> decoded = new(count);
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
				if (stateCount > MaxStateValues || !Enum.IsDefined(typeof(Kind), effect.StateKind) ||
					(version == 1 && effect.StateKind > Kind.Puke)) return false;
				effect.State = new float[stateCount];
				for (int j = 0; j < stateCount; ++j)
				{
					effect.State[j] = reader.ReadSingle();
					if (!Valid(effect.State[j])) return false;
				}
				if (effect.Hash == 0 || effect.Remaining <= 0 || !Valid(effect.Remaining)) return false;
				decoded.Add(effect);
			}
			if (stream.Position != stream.Length || decoded.Count == 0) return false;
			effects = decoded;
			return true;
		}
		catch (Exception)
		{
			effects.Clear();
			return false;
		}
	}

	internal static LegacyEffects TakeLegacy(Dictionary<string, string> data, bool dead)
	{
		LegacyEffects legacy = new();
		legacy.HasRested = RestedState.Take(data, dead, out legacy.RestedRemaining);
		bool hasDamage = data.TryGetValue(LegacyPoisonDamageKey, out string? damage);
		bool hasDamagePerHit = data.TryGetValue(LegacyPoisonDamageHitKey, out string? damagePerHit);
		bool hasTtl = data.TryGetValue(LegacyPoisonTtlKey, out string? ttl);
		RemoveLegacyPoison(data);
		legacy.HasPoison = !dead && hasDamage && hasDamagePerHit && hasTtl &&
			float.TryParse(damage, NumberStyles.Float, CultureInfo.InvariantCulture, out legacy.PoisonDamageLeft) &&
			float.TryParse(damagePerHit, NumberStyles.Float, CultureInfo.InvariantCulture, out legacy.PoisonDamagePerHit) &&
			float.TryParse(ttl, NumberStyles.Float, CultureInfo.InvariantCulture, out legacy.PoisonRemaining) &&
			Valid(legacy.PoisonDamageLeft) && Valid(legacy.PoisonDamagePerHit) &&
			legacy.PoisonRemaining > 0 && Valid(legacy.PoisonRemaining);
		return legacy;
	}

	internal static void MergeLegacy(List<Entry> effects, LegacyEffects legacy, int restedHash, int poisonHash, bool includePoison)
	{
		bool hasRested = false;
		bool hasPoison = false;
		foreach (Entry effect in effects)
		{
			hasRested |= effect.Hash == restedHash;
			hasPoison |= effect.Hash == poisonHash;
		}
		if (legacy.HasRested && !hasRested)
		{
			effects.Add(new Entry
			{
				Hash = restedHash,
				Remaining = legacy.RestedRemaining,
				StateKind = Kind.Rested,
			});
		}
		if (includePoison && legacy.HasPoison && !hasPoison)
		{
			effects.Add(new Entry
			{
				Hash = poisonHash,
				Remaining = legacy.PoisonRemaining,
				StateKind = Kind.Poison,
				State = new[] { 0f, legacy.PoisonDamageLeft, legacy.PoisonDamagePerHit },
			});
		}
	}

	internal static void RemoveLegacy(Dictionary<string, string> data)
	{
		data.Remove(RestedState.Key);
		RemoveLegacyPoison(data);
	}

	private static void RemoveLegacyPoison(Dictionary<string, string> data)
	{
		data.Remove(LegacyPoisonDamageKey);
		data.Remove(LegacyPoisonDamageHitKey);
		data.Remove(LegacyPoisonTtlKey);
	}
}
