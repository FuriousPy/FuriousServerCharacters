using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using BepInEx.Configuration;
using JetBrains.Annotations;
using UnityEngine;

namespace ServerCharacters;

public static class Utils
{
	public static bool GetToggle(this ConfigEntry<Toggle> toggle)
	{
		return toggle.Value == Toggle.On;
	}

	public static void Log(string message)
	{
		ServerCharacters.logger.LogMessage(message);
	}

	public static string CharacterSavePath => SaveSystem.GetCharacterFolderPath(FileHelpers.FileSource.Local);

	public static bool IsServerCharactersFilePattern(string file) => file.Split('_').Length >= 3 && file.EndsWith(".fch", StringComparison.Ordinal) && !file.Contains("_backup_");

	public record struct ProfileName
	{
		[UsedImplicitly] public string id;
		[UsedImplicitly] public string name;

		public static ProfileName fromPeer(ZNetPeer peer) => new() { id = GetPlayerID(peer.m_socket.GetHostName()), name = peer.m_playerName };
	}

	public static class Cache
	{
		public static readonly Dictionary<ProfileName, PlayerProfile> profiles = new();

		public static PlayerProfile loadProfile(ProfileName name)
		{
			if (!profiles.TryGetValue(name, out PlayerProfile profile))
			{
				profile = new PlayerProfile($"{name.id}_{name.name}", FileHelpers.FileSource.Local);
				profile.LoadPlayerFromDisk();
				profiles[name] = profile;
			}

			return profile;
		}
	}

	public static bool HasCharacterForPlayer(string playerId)
	{
		foreach (string s in Directory.GetFiles(CharacterSavePath))
		{
			FileInfo file = new(s);
			if (IsServerCharactersFilePattern(file.Name))
			{
				string[] parts = file.Name.Split('_');
				if (string.Equals($"{parts[0]}_{parts[1]}", playerId, StringComparison.OrdinalIgnoreCase))
				{
					return true;
				}
			}
		}

		return false;
	}

	// Adds the Steam_ prefix to steam IDs. Xbox IDs are prefixed with Xbox_ automatically.
	public static string GetPlayerID(string player)
	{
		if (Regex.IsMatch(player, @"^\d+$"))
		{
			player = "Steam_" + player;
		}

		return player;
	}

	public static void OverwriteDict<K, V>(IDictionary<K, V> src, IDictionary<K, V> dst)
	{
		dst.Clear();
		foreach (KeyValuePair<K, V> kv in src)
		{
			dst.Add(kv.Key, kv.Value);
		}
	}
}
