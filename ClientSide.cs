using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using System.Threading;
using HarmonyLib;
using JetBrains.Annotations;
using Steamworks;
using UnityEngine;
using YamlDotNet.Serialization;
using Random = UnityEngine.Random;

namespace ServerCharacters;

public static class ClientSide
{
	public static bool serverCharacter = false;
	private static bool currentlySaving = false;
	private static Coroutine? profileSendCoroutine;
	private static int clientSession;
	private static PendingProfileSend? pendingProfileSend;
	private static long captureRevision;
	private static string shutdownRequest = "";
	private static string finalProfileEvent = "";
	private static byte[] recoveryBaseline = Array.Empty<byte>();
	private static byte[] loadedServerProfileHash = Array.Empty<byte>();
	private static byte[] obsoleteBackupProfileHash = Array.Empty<byte>();
	private static string obsoleteBackupCharacter = "";
	private static long acknowledgedRevision;

	private static byte[] WrapUpdate(byte[] data, string request = "")
	{
		ZPackage package = new();
		package.Write(++captureRevision);
		package.Write(request);
		package.Write(data);
		return package.GetArray();
	}

	private sealed class PendingProfileSend
	{
		public readonly ZNetPeer Peer;
		public readonly byte[] Data;
		public readonly string Event;
		public readonly bool IsSnapshot;
		public PendingProfileSend(ZNetPeer peer, byte[] data, string eventName, bool snapshot)
		{
			Peer = peer; Data = data; Event = eventName; IsSnapshot = snapshot;
		}
	}

	private static void StopProfileSender()
	{
		++clientSession;
		try
		{
			if (profileSendCoroutine != null && ServerCharacters.selfReference != null)
				ServerCharacters.selfReference.StopCoroutine(profileSendCoroutine);
		}
		finally
		{
			profileSendCoroutine = null;
			pendingProfileSend = null;
			currentlySaving = false;
		}
	}

	private static void ResetClientSession()
	{
		StopProfileSender();
		MonitorPlayerActivity.Stop();
		serverCharacter = false;
		forceSynchronousSaving = false;
		doEmergencyBackup = false;
		acquireCharacterFromTemplate = false;
		playerSnapShotLast = playerSnapShotNew = null;
		serverEncryptionKey = null;
		serverEncryptionTime = 0;
		captureRevision = acknowledgedRevision = 0;
		shutdownRequest = "";
		finalProfileEvent = "";
		recoveryBaseline = Array.Empty<byte>();
		loadedServerProfileHash = Array.Empty<byte>();
		obsoleteBackupProfileHash = Array.Empty<byte>();
		obsoleteBackupCharacter = "";
		nextDisconnectProtectionSnapshot = 0;
		PatchInventoryChanged.Reset();
	}

	private static void QueueProfile(byte[] data, string eventName, bool snapshot = false)
	{
		ZNetPeer? peer = ZNet.instance?.GetServerPeer();
		if (!serverCharacter || peer?.IsReady() != true) return;
		if (!snapshot && eventName == "ServerCharacters PlayerProfile" && shutdownRequest.Length == 0 && finalProfileEvent.Length != 0)
			eventName = finalProfileEvent;
		data = WrapUpdate(data, snapshot ? "" : shutdownRequest);
		PendingProfileSend packet = new(peer, data, eventName, snapshot);
		if (forceSynchronousSaving)
		{
			// Discard unfinished older transfers before submitting the final, newer profile.
			StopProfileSender();
			bool sent = false;
			foreach (bool ready in Shared.sendCompressedDataToPeer(peer, eventName, data, ok => sent = ok, 5))
				if (!ready) Thread.Sleep(10);
			if (!sent) ServerCharacters.logger.LogError("The final character transfer failed; the local save remains available.");
			return;
		}
		// A newer durable save subsumes the previous pending save. Snapshots never replace it.
		if (snapshot && (currentlySaving || pendingProfileSend != null)) return;
		pendingProfileSend = packet;
		if (currentlySaving) return;
		currentlySaving = true;
		int session = clientSession;
		try
		{
			Coroutine started = ServerCharacters.selfReference.StartCoroutine(DrainProfileSends(session));
			if (currentlySaving && session == clientSession) profileSendCoroutine = started;
		}
		catch
		{
			currentlySaving = false;
			throw;
		}
	}

	private static IEnumerator DrainProfileSends(int session)
	{
		try
		{
			while (session == clientSession && pendingProfileSend != null)
			{
				PendingProfileSend packet = pendingProfileSend;
				pendingProfileSend = null;
				bool sent = false;
				foreach (bool ready in Shared.sendCompressedDataToPeer(packet.Peer, packet.Event, packet.Data, ok => sent = ok))
				{
					if (session != clientSession) yield break;
					if (!ready) yield return null;
				}
				if (!sent && !packet.IsSnapshot && session == clientSession)
				{
					pendingProfileSend ??= packet;
					if (packet.Peer.m_socket?.IsConnected() != true) yield break;
					yield return new WaitForSecondsRealtime(5);
				}
			}
		}
		finally
		{
			if (session == clientSession)
			{
				currentlySaving = false;
				profileSendCoroutine = null;
			}
		}
	}
	private static bool forceSynchronousSaving = false;
	private static float nextDisconnectProtectionSnapshot;
	private static bool acquireCharacterFromTemplate = false;
	private static bool doEmergencyBackup = false;
	private static bool iDied = false;
	private static PlayerSnapshot? playerSnapShotLast;
	private static PlayerSnapshot? playerSnapShotNew;

	private static byte[]? serverEncryptionKey;
	private static long serverEncryptionTime;

	private static string? connectionError;

	[HarmonyPatch(typeof(PlayerProfile), nameof(PlayerProfile.SavePlayerData))]
	private static class PatchPlayerProfilePlayerSave
	{
		[UsedImplicitly]
		private static void Prefix()
		{
			if (serverCharacter && doEmergencyBackup && playerSnapShotLast != null && Player.m_localPlayer is { } player)
			{
				player.m_inventory.m_inventory = playerSnapShotLast.inventory;
				Utils.OverwriteDict(playerSnapShotLast.knownStations, player.m_knownStations);
				Utils.OverwriteDict(playerSnapShotLast.knownTexts, player.m_knownTexts);
			}
		}
	}

	[HarmonyPatch(typeof(PlayerProfile), nameof(PlayerProfile.SavePlayerToDisk))]
	private static class PatchPlayerProfileSave_Client
	{
		private static byte[] SaveCharacterToServer(byte[] packageArray, PlayerProfile profile)
		{
			try
			{
				if (serverCharacter && doEmergencyBackup && serverEncryptionKey != null)
				{
					File.WriteAllBytes(Utils.CharacterSavePath + Path.DirectorySeparatorChar + profile.m_filename + ".fch.signature", generateProfileSignature(packageArray, serverEncryptionKey));
					File.WriteAllBytes(Utils.CharacterSavePath + Path.DirectorySeparatorChar + profile.m_filename + ".fch.serverbackup", packageArray);
					doEmergencyBackup = false;
				}
				QueueProfile(packageArray, iDied && ServerCharacters.hardcoreMode.GetToggle() ? "ServerCharacters PlayerDied" : "ServerCharacters PlayerProfile");
			}
			catch (Exception e)
			{
				ServerCharacters.logger.LogError($"Could not send or create an emergency character backup; allowing the local save to continue: {e}");
			}
			return packageArray;
		}

		private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
		{
			MethodInfo writer = AccessTools.DeclaredMethod(typeof(BinaryWriter), nameof(BinaryWriter.Write), new[] { typeof(byte[]) });
			MethodInfo sender = AccessTools.DeclaredMethod(typeof(PatchPlayerProfileSave_Client), nameof(SaveCharacterToServer));
			List<CodeInstruction> result = instructions.ToList();
			int index = result.FindIndex(i => i.Calls(writer));
			if (index < 0) throw new InvalidOperationException("Could not locate the profile byte-array write.");
			CodeInstruction first = new(OpCodes.Ldarg_0);
			first.labels.AddRange(result[index].labels);
			result[index].labels.Clear();
			result.InsertRange(index, new[] { first, new CodeInstruction(OpCodes.Call, sender) });
			return result;
		}
	}

	[HarmonyPatch]
	private class EnableSocketLinger
	{
		private static void dummy() { }

		private static MethodInfo TargetMethod() => Type.GetType(nameof(ZSteamSocket) + ", assembly_valheim") is { } steamSocket ? AccessTools.DeclaredMethod(steamSocket, nameof(ZSteamSocket.Close)) : AccessTools.DeclaredMethod(typeof(EnableSocketLinger), nameof(dummy));

		private static MethodInfo socketClose => AccessTools.Method(typeof(SteamNetworkingSockets), nameof(SteamNetworkingSockets.CloseConnection));

		private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> codeInstructions)
		{
			foreach (CodeInstruction instruction in codeInstructions)
			{
				if (instruction.opcode == OpCodes.Call && instruction.OperandIs(socketClose))
				{
					yield return new CodeInstruction(OpCodes.Pop);
					yield return new CodeInstruction(OpCodes.Ldc_I4_1);
				}
				yield return instruction;
			}
		}
	}

	[HarmonyPatch(typeof(Game), nameof(Game.Shutdown))]
	private static class PatchGameShutdown
	{
		[UsedImplicitly]
		private static void Prefix()
		{
			if (finalProfileEvent.Length == 0) finalProfileEvent = "ServerCharacters PlayerProfileLogout";
			forceSynchronousSaving = true;
		}

		[UsedImplicitly]
		private static void Finalizer()
		{
			ResetClientSession();
		}
	}

	[HarmonyPatch(typeof(Player), nameof(Player.Save))]
	private static class StoreRested
	{
		private static void Prefix(Player __instance)
		{
			if (!serverCharacter) return;
			try
			{
				StatusEffect? rested = __instance.m_seman.GetStatusEffect("Rested".GetStableHashCode());
				RestedState.Store(__instance.m_customData, rested == null ? 0 : rested.m_ttl - rested.m_time, __instance.IsDead());
			}
			catch (Exception e) { ServerCharacters.logger.LogError($"Could not capture Rested; character saving will continue: {e}"); }
		}
	}

	[HarmonyPatch(typeof(Player), nameof(Player.Load))]
	private static class RestoreRested
	{
		private static void Postfix(Player __instance)
		{
			if (!serverCharacter) return;
			try
			{
				if (__instance.m_nview?.m_zdo == null || !__instance.m_nview.IsOwner()) return;
				if (!RestedState.Take(__instance.m_customData, __instance.IsDead(), out float remaining)) return;
				StatusEffect? effect = __instance.m_seman.GetStatusEffect("Rested".GetStableHashCode()) ??
					__instance.m_seman.AddStatusEffect("Rested".GetStableHashCode());
				if (effect == null)
				{
					ServerCharacters.logger.LogWarning("Could not restore Rested: status effect was unavailable.");
					return;
				}
				// Setup computes a fresh comfort duration; replace it with the saved remainder.
				effect.m_ttl = remaining;
				effect.m_time = 0;
			}
			catch (Exception e) { ServerCharacters.logger.LogError($"Could not restore Rested; character loading will continue: {e}"); }
		}
	}

	[HarmonyPatch(typeof(Player), nameof(Player.OnDeath))]
	private static class ClearSavedRestedOnDeath
	{
		private static void Prefix(Player __instance)
		{
			try
			{
				__instance.m_customData.Remove(RestedState.Key);
				__instance.m_customData.Remove(BuffState.Key);
			}
			catch (Exception e) { ServerCharacters.logger.LogWarning($"Could not clear saved status effects on death: {e}"); }
		}
	}

	private static readonly HashSet<int> RecalculatedStatusEffects = new(new[]
	{
		"Rested", "Encumbered", "SoftDeath", "Wet", "Shelter", "CampFire", "Resting", "Cold", "Freezing",
		"Burning", "Frost", "Lightning", "Poison", "Smoked", "Spirit", "Tared", "CorpseRun", "NoSkillDrain",
	}.Select(name => name.GetStableHashCode()));

	private static float FiniteOrZero(float value) => BuffState.Valid(value) ? value : 0;

	private static float[] CaptureStatsState(SE_Stats stats)
	{
		float staminaDuration = Math.Max(0, stats.m_staminaOverTimeDuration - stats.m_time);
		float staminaAmount = stats.m_staminaOverTimeDuration > 0
			? stats.m_staminaOverTime * staminaDuration / stats.m_staminaOverTimeDuration : 0;
		float eitrDuration = Math.Max(0, stats.m_eitrOverTimeDuration - stats.m_time);
		float eitrAmount = stats.m_eitrOverTimeDuration > 0
			? stats.m_eitrOverTime * eitrDuration / stats.m_eitrOverTimeDuration : 0;
		return new[]
		{
			FiniteOrZero(stats.m_tickTimer), FiniteOrZero(stats.m_healthOverTimeTimer),
			FiniteOrZero(stats.m_healthOverTimeTicks), FiniteOrZero(stats.m_healthOverTimeTickHP),
			FiniteOrZero(staminaAmount), FiniteOrZero(staminaDuration),
			FiniteOrZero(eitrAmount), FiniteOrZero(eitrDuration),
		};
	}

	private static BuffState.Entry CaptureBuff(StatusEffect effect, float remaining)
	{
		BuffState.Entry saved = new()
		{
			Hash = effect.NameHash(),
			Remaining = remaining,
			Variant = effect.m_hitVariant,
		};
		switch (effect)
		{
			case SE_Shield shield:
				saved.StateKind = BuffState.Kind.Shield;
				saved.State = new[] { FiniteOrZero(shield.m_totalAbsorbDamage), FiniteOrZero(shield.m_damage) };
				break;
			case SE_Puke puke:
				saved.StateKind = BuffState.Kind.Puke;
				saved.State = new[] { FiniteOrZero(puke.m_removeTimer) };
				break;
			case SE_Stats stats:
				saved.StateKind = BuffState.Kind.Stats;
				saved.State = CaptureStatsState(stats);
				break;
			case SE_React react:
				saved.StateKind = BuffState.Kind.React;
				saved.State = new[] { (float)react.m_itemLevel };
				break;
		}
		return saved;
	}

	[HarmonyPatch(typeof(Player), nameof(Player.Save))]
	private static class StorePlayerBuffs
	{
		private static void Prefix(Player __instance)
		{
			if (!serverCharacter) return;
			try
			{
				List<BuffState.Entry> saved = new();
				foreach (StatusEffect effect in __instance.m_seman.GetStatusEffects())
				{
					float remaining = effect.m_ttl - effect.m_time;
					// Indefinite effects generally come from equipment. The named finite effects are environmental or native death state.
					if (effect.m_ttl <= 0 || remaining <= 0 || !BuffState.Valid(remaining) ||
						RecalculatedStatusEffects.Contains(effect.NameHash())) continue;
					saved.Add(CaptureBuff(effect, remaining));
				}
				BuffState.Store(__instance.m_customData, saved, __instance.IsDead());
			}
			catch (Exception e) { ServerCharacters.logger.LogError($"Could not capture player buffs; character saving will continue: {e}"); }
		}
	}

	private static StatusEffect? AddRestoredBuff(Player player, BuffState.Entry saved)
	{
		StatusEffect? current = player.m_seman.GetStatusEffect(saved.Hash);
		if (current != null) return current;
		StatusEffect? template = ObjectDB.instance?.GetStatusEffect(saved.Hash);
		if (template == null) return null;
		// Cloning the template lets us suppress one-shot potion healing during restoration.
		StatusEffect source = template.Clone();
		if (source is SE_Stats stats)
		{
			stats.m_healthUpFront = 0;
			stats.m_staminaUpFront = 0;
			stats.m_eitrUpFront = 0;
			stats.m_adrenalineUpFront = 0;
		}
		try { return player.m_seman.AddStatusEffect(source, false, 0, 0, saved.Variant); }
		finally { UnityEngine.Object.Destroy(source); }
	}

	private static void RestoreSpecificState(StatusEffect effect, BuffState.Entry saved)
	{
		float[] state = saved.State;
		if (saved.StateKind == BuffState.Kind.Shield && effect is SE_Shield shield && state.Length >= 2)
		{
			shield.m_totalAbsorbDamage = state[0];
			shield.m_damage = state[1];
		}
		else if (saved.StateKind == BuffState.Kind.Stats && effect is SE_Stats stats && state.Length >= 8)
		{
			stats.m_tickTimer = state[0];
			stats.m_healthOverTimeTimer = state[1];
			stats.m_healthOverTimeTicks = state[2];
			stats.m_healthOverTimeTickHP = state[3];
			stats.m_staminaOverTime = state[4];
			stats.m_staminaOverTimeDuration = state[5];
			stats.m_eitrOverTime = state[6];
			stats.m_eitrOverTimeDuration = state[7];
		}
		else if (saved.StateKind == BuffState.Kind.React && effect is SE_React react && state.Length >= 1)
			react.m_itemLevel = (int)state[0];
		else if (saved.StateKind == BuffState.Kind.Puke && effect is SE_Puke puke && state.Length >= 1)
			puke.m_removeTimer = state[0];
	}

	[HarmonyPatch(typeof(Player), nameof(Player.Load))]
	private static class RestorePlayerBuffs
	{
		private static void Postfix(Player __instance)
		{
			if (!serverCharacter) return;
			try
			{
				if (__instance.m_nview?.m_zdo == null || !__instance.m_nview.IsOwner() ||
					!BuffState.Take(__instance.m_customData, __instance.IsDead(), out List<BuffState.Entry> saved)) return;
				foreach (BuffState.Entry entry in saved)
				{
					if (RecalculatedStatusEffects.Contains(entry.Hash)) continue;
					StatusEffect? effect = AddRestoredBuff(__instance, entry);
					if (effect == null)
					{
						ServerCharacters.logger.LogWarning($"Could not restore status effect {entry.Hash}: it is unavailable.");
						continue;
					}
					effect.m_ttl = entry.Remaining;
					effect.m_time = 0;
					RestoreSpecificState(effect, entry);
				}
			}
			catch (Exception e) { ServerCharacters.logger.LogError($"Could not restore player buffs; character loading will continue: {e}"); }
		}
	}

	[HarmonyPatch(typeof(Player), nameof(Player.Save))]
	private static class StorePoisonDebuff
	{
		private static void Prefix(Player __instance)
		{
			if (ServerCharacters.storePoison.GetToggle())
			{
				if (__instance.m_seman.GetStatusEffect("Poison".GetStableHashCode()) is SE_Poison poison && !__instance.IsDead())
				{
					__instance.m_customData["ServerCharacters PoisonDamage"] = poison.m_damageLeft.ToString(CultureInfo.InvariantCulture);
					__instance.m_customData["ServerCharacters PoisonDamageHit"] = poison.m_damagePerHit.ToString(CultureInfo.InvariantCulture);
					__instance.m_customData["ServerCharacters PoisonTTL"] = poison.m_ttl.ToString(CultureInfo.InvariantCulture);
				}
				else
				{
					__instance.m_customData.Remove("ServerCharacters PoisonDamage");
					__instance.m_customData.Remove("ServerCharacters PoisonDamageHit");
					__instance.m_customData.Remove("ServerCharacters PoisonTTL");
				}
			}
		}
	}

	[HarmonyPatch(typeof(Player), nameof(Player.Load))]
	private static class LoadPoisonDebuff
	{
		private static void Postfix(Player __instance)
		{
			if (__instance.m_nview.m_zdo is not null && ServerCharacters.storePoison.GetToggle() && __instance.m_customData.TryGetValue("ServerCharacters PoisonDamage", out string poisonString) && poisonString != "")
			{
				SE_Poison poison = (SE_Poison)__instance.m_seman.AddStatusEffect("Poison".GetStableHashCode());
				poison.m_damageLeft = float.Parse(__instance.m_customData["ServerCharacters PoisonDamage"], CultureInfo.InvariantCulture);
				poison.m_damagePerHit = float.Parse(__instance.m_customData["ServerCharacters PoisonDamageHit"], CultureInfo.InvariantCulture);
				poison.m_ttl = float.Parse(__instance.m_customData["ServerCharacters PoisonTTL"], CultureInfo.InvariantCulture);
			}
		}
	}

	[HarmonyPatch(typeof(ZNet), nameof(ZNet.OnNewConnection))]
	private static class PatchZNetOnNewConnection
	{
		[UsedImplicitly]
		private static void Postfix(ZNet __instance, ZNetPeer peer)
		{
			try
			{
				if (ZNet.instance.IsServer()) return;
				ResetClientSession();
				iDied = false;

				peer.m_rpc.Register("ServerCharacters PlayerProfile", Shared.receiveCompressedFromPeer(onReceivedProfile));
				peer.m_rpc.Register<ZPackage>("ServerCharacters KeyExchange", receiveEncryptionKeyFromServer);
				peer.m_rpc.Register<string>("ServerCharacters PrepareShutdownSave", onPrepareShutdownSave);
				peer.m_rpc.Register("ServerCharacters EmergencyRestored", rpc => cleanEmergencyBackup());
				peer.m_rpc.Register<ZPackage>("ServerCharacters EmergencyObsolete", (rpc, notice) =>
				{
					try
					{
						string character = notice.ReadString();
						byte[] hash = notice.ReadByteArray();
						if (hash.Length != 64) return;
						obsoleteBackupCharacter = character;
						obsoleteBackupProfileHash = hash;
						CleanObsoleteEmergencyBackup();
					}
					catch (Exception e) { ServerCharacters.logger.LogWarning($"Could not process obsolete emergency-backup notice: {e}"); }
				});
				peer.m_rpc.Register<ZPackage>("ServerCharacters ProfileSaved", (rpc, ack) =>
				{
					try
					{
						long revision = ack.ReadLong();
						byte[] hash = ack.ReadByteArray();
						if (revision > acknowledgedRevision && hash.Length == 64)
						{ acknowledgedRevision = revision; recoveryBaseline = hash; }
					}
					catch (Exception e) { ServerCharacters.logger.LogError($"Invalid profile acknowledgement: {e}"); }
				});

				string signatureFilePath = Utils.CharacterSavePath + Path.DirectorySeparatorChar + Game.instance.m_playerProfile.m_filename + ".fch.signature";
				string backupFilePath = Utils.CharacterSavePath + Path.DirectorySeparatorChar + Game.instance.m_playerProfile.m_filename + ".fch.serverbackup";

				if (File.Exists(signatureFilePath) && File.Exists(backupFilePath))
				{
					Utils.Log($"Found emergency backup and signature for character '{Game.instance.m_playerProfile.m_filename}'. Trying to restore the backup.");

					ZPackage package = new();
					package.Write(File.ReadAllBytes(backupFilePath));
					package.Write(File.ReadAllBytes(signatureFilePath));
					foreach (bool sending in Shared.sendCompressedDataToPeer(peer, "ServerCharacters CheckSignature", package.GetArray()))
					{
						if (!sending)
						{
							Thread.Sleep(10); // busy loop, force waiting before continuing...
						}
					}
				}
			}
			catch (Exception e)
			{
				ServerCharacters.logger.LogError($"Could not initialize Server Characters for the server connection; continuing without emergency-backup restoration: {e}");
			}
		}

		private static void onPrepareShutdownSave(ZRpc peerRpc, string request)
		{
			bool previousForceSynchronousSaving = forceSynchronousSaving;
			try
			{
				Utils.Log("Received the final character save request from the server.");
				if (!serverCharacter || Game.instance?.m_playerProfile == null)
				{
					ServerCharacters.logger.LogWarning("Final shutdown save request was ignored because no active server character profile exists.");
					return;
				}

				forceSynchronousSaving = true;
				shutdownRequest = request;
				Game.instance.SavePlayerProfile(true);
				Utils.Log("Sent the final character save requested by the server shutdown.");
			}
			catch (Exception e)
			{
				ServerCharacters.logger.LogError($"Could not send the final character save during server shutdown: {e}");
			}
			finally
			{
				forceSynchronousSaving = previousForceSynchronousSaving;
				shutdownRequest = "";
			}
		}

		private static void onReceivedProfile(ZRpc peerRpc, byte[] profileData)
		{
			if (profileData.Length == 0)
			{
				if (Game.instance.m_playerProfile.m_worldData.Count != 0)
				{
					Game.instance.Logout();
					ZNet.m_connectionStatus = ZNet.ConnectionStatus.ErrorConnectFailed;
					connectionError = "Please create a new character, before connecting to this server, to avoid loss of data.";
				}
				else
				{
					serverCharacter = true;
					acquireCharacterFromTemplate = true;
				}

				return;
			}

			PlayerProfile profile = new(Game.instance.m_playerProfile.m_filename);
			if (!profile.LoadPlayerProfileFromBytes(profileData) || Shared.CharacterNameIsForbidden(profile.m_playerName))
			{
				Game.instance.Logout();
				ZNet.m_connectionStatus = ZNet.ConnectionStatus.ErrorConnectFailed;
				connectionError = "The saved data on the server was corrupt, please contact your server admin or create a new character.";
				return;
			}

			serverCharacter = true;
			Game.instance.m_playerProfile = profile;
			using (SHA512 hash = SHA512.Create()) recoveryBaseline = hash.ComputeHash(profileData);

			loadedServerProfileHash = recoveryBaseline;
			CleanObsoleteEmergencyBackup();
		}
	}

	private static void CleanObsoleteEmergencyBackup()
	{
		// Either RPC may arrive first. Only discard after loading the exact validated server profile.
		if (loadedServerProfileHash.Length == 64 && obsoleteBackupProfileHash.Length == 64 &&
		    string.Equals(Game.instance.m_playerProfile.GetName(), obsoleteBackupCharacter, StringComparison.OrdinalIgnoreCase) &&
		    loadedServerProfileHash.SequenceEqual(obsoleteBackupProfileHash))
		{
			cleanEmergencyBackup();
		}
	}

	private static void cleanEmergencyBackup()
	{
		try
		{
			string signatureFilePath = Utils.CharacterSavePath + Path.DirectorySeparatorChar + Game.instance.m_playerProfile.m_filename + ".fch.signature";
			string backupFilePath = Utils.CharacterSavePath + Path.DirectorySeparatorChar + Game.instance.m_playerProfile.m_filename + ".fch.serverbackup";

			if (File.Exists(backupFilePath) && File.Exists(signatureFilePath))
			{
				File.Delete(signatureFilePath);
				File.Delete(backupFilePath);
				Utils.Log($"Deleted emergency backup from {backupFilePath}");
			}
		}
		catch (Exception e)
		{
			ServerCharacters.logger.LogError($"Could not delete the local emergency-backup files; they will be retried later: {e}");
		}
	}

	[HarmonyPatch(typeof(ZNet), nameof(ZNet.RPC_PeerInfo))]
	private static class DetectBackupOnlyMode
	{
		[UsedImplicitly]
		private static void Postfix(ZNet __instance)
		{
			if (ServerCharacters.backupOnlyMode.GetToggle() && !__instance.IsServer())
			{
				serverCharacter = true;
			}
		}
	}

	[HarmonyPatch(typeof(Game), nameof(Game.SpawnPlayer))]
	private class InitializePlayerFromTemplate
	{
		[UsedImplicitly]
		private static void Postfix()
		{
			if (ServerCharacters.backupOnlyMode.GetToggle())
			{
				cleanEmergencyBackup();
			}

			if (ServerCharacters.backupOnlyMode.GetToggle() ? Game.instance.GetPlayerProfile().HaveLogoutPoint() || Game.instance.GetPlayerProfile().HaveCustomSpawnPoint() : !acquireCharacterFromTemplate)
			{
				return;
			}
			acquireCharacterFromTemplate = false;

			Player.m_localPlayer.m_inventory.RemoveAll();
			Player.m_localPlayer.m_skills.m_skillData.Clear();
			Player.m_localPlayer.m_knownMaterial.Clear();
			Player.m_localPlayer.m_knownRecipes.Clear();
			Player.m_localPlayer.m_knownStations.Clear();
			Player.m_localPlayer.m_knownTexts.Clear();
			Player.m_localPlayer.m_uniques.Clear();
			Player.m_localPlayer.m_trophies.Clear();
			Player.m_localPlayer.m_customData.Clear();
			Player.m_localPlayer.GiveDefaultItems();

			try
			{
				PlayerTemplate? template = new DeserializerBuilder().IgnoreFields().Build().Deserialize<PlayerTemplate?>(ServerCharacters.playerTemplate.Value);
				if (template != null)
				{
					foreach (KeyValuePair<string, float> skillKv in template.skills)
					{
						Player.m_localPlayer.GetSkills().CheatRaiseSkill(skillKv.Key, skillKv.Value);
					}

					Inventory inventory = Player.m_localPlayer.m_inventory;
					foreach (KeyValuePair<string, int> item in template.items)
					{
						inventory.AddItem(item.Key, item.Value, 1, 0, 0, "", false, false);
					}

					if (template.spawn is { Count: > 0 } spawnPos)
					{
						Random.State oldState = Random.state;
						Random.InitState(UserInfo.GetLocalUser().UserId.GetHashCode());
						int index = Random.Range(0, spawnPos.Count - 1);
						Random.state = oldState;
						Player.m_localPlayer.transform.position = new Vector3(spawnPos[index].x, spawnPos[index].y, spawnPos[index].z);
					}
				}
			}
			catch (SerializationException)
			{
			}

			Game.instance.SavePlayerProfile(true);

			if (ServerCharacters.firstLoginMessage.Value != "")
			{
				foreach (Player p in Player.GetAllPlayers())
				{
					p.Message(MessageHud.MessageType.Center, ServerCharacters.firstLoginMessage.Value.Replace("{name}", Player.m_localPlayer.GetHoverName()));
				}
			}
		}
	}

	[HarmonyPatch(typeof(Valkyrie), nameof(Valkyrie.Awake))]
	private class ChangeValkyrieTarget
	{
		private static void Prefix(Valkyrie __instance)
		{
			if (!__instance.GetComponent<ZNetView>().IsOwner())
			{
				return;
			}

			try
			{
				PlayerTemplate? template = new DeserializerBuilder().IgnoreFields().Build().Deserialize<PlayerTemplate?>(ServerCharacters.playerTemplate.Value);
				if (template == null)
				{
					return;
				}

				if (template.spawn is { Count: > 0 } spawnPos)
				{
					Random.State oldState = Random.state;
					Random.InitState(UserInfo.GetLocalUser().UserId.GetHashCode());
					int index = Random.Range(0, spawnPos.Count - 1);
					Random.state = oldState;
					Player.m_localPlayer.transform.position = new Vector3(spawnPos[index].x, spawnPos[index].y, spawnPos[index].z);
				}
			}
			catch (SerializationException)
			{
			}
		}
	}

	[HarmonyPatch(typeof(Player), nameof(Player.OnSpawned))]
	private static class DisableValkyrieAndIntro
	{
		private static void Postfix(bool spawnValkyrie)
		{
			if (spawnValkyrie)
			{
				switch (ServerCharacters.newCharacterIntro.Value)
				{
					case Intro.Disabled:
						Game.instance.SkipIntro();
						break;
					case Intro.Valkyrie:
						Game.instance.m_inIntro = Game.instance.m_queuedIntro = false;
						TextViewer.instance.HideIntro();
						break;
				}
			}
		}
	}

	[HarmonyPatch(typeof(Game), nameof(Game.FindSpawnPoint))]
	private class ReplaceSpawnPoint
	{
		private static bool CheckCustomSpawnPoint(out Vector3 pos)
		{
			try
			{
				PlayerTemplate? template = new DeserializerBuilder().IgnoreFields().Build().Deserialize<PlayerTemplate?>(ServerCharacters.playerTemplate.Value);
				if (template == null)
				{
					pos = Vector3.zero;
					return false;
				}

				if (template.spawn is { Count: > 0 } spawnPos)
				{
					Random.State oldState = Random.state;
					Random.InitState(UserInfo.GetLocalUser().UserId.GetHashCode());
					int index = Random.Range(0, spawnPos.Count - 1);
					Random.state = oldState;
					pos = new Vector3(spawnPos[index].x, spawnPos[index].y, spawnPos[index].z);
					return true;
				}
			}
			catch (SerializationException)
			{
			}

			pos = Vector3.zero;
			return false;
		}

		private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> _instructions, ILGenerator ilg)
		{
			MethodInfo LocationIconGetter = AccessTools.DeclaredMethod(typeof(ZoneSystem), nameof(ZoneSystem.GetLocationIcon));
			FieldInfo startLocation = AccessTools.DeclaredField(typeof(Game), nameof(Game.m_StartLocation));

			List<CodeInstruction> instructions = _instructions.ToList();
			for (int i = 0; i < instructions.Count; ++i)
			{
				if (i < instructions.Count - 4 && instructions[i].opcode == OpCodes.Callvirt && instructions[i].OperandIs(LocationIconGetter) && instructions[i - 2].opcode == OpCodes.Ldfld && instructions[i - 2].OperandIs(startLocation))
				{
					int callStart = i;
					MethodInfo zoneSystemGetter = AccessTools.DeclaredPropertyGetter(typeof(ZoneSystem), nameof(ZoneSystem.instance));
					while (instructions[callStart].opcode != OpCodes.Call || !instructions[callStart].OperandIs(zoneSystemGetter))
					{
						--callStart;
					}

					CodeInstruction afterCondition = instructions.Skip(i).SkipWhile(instr => instr.opcode.FlowControl != FlowControl.Cond_Branch).Skip(1).First();
					Label label = ilg.DefineLabel();
					afterCondition.labels.Add(label);

					List<Label> callStartLabels = instructions[callStart].labels;

					instructions.InsertRange(callStart, new[]
					{
						new CodeInstruction(OpCodes.Nop) { labels = new List<Label>(callStartLabels) },
						instructions[i - 1], // location save target
						new CodeInstruction(OpCodes.Call, AccessTools.DeclaredMethod(typeof(ReplaceSpawnPoint), nameof(CheckCustomSpawnPoint))),
						new CodeInstruction(OpCodes.Brtrue, label),
					});

					callStartLabels.Clear();

					break;
				}
			}

			return instructions;
		}
	}

	[HarmonyPatch(typeof(Player), nameof(Player.CreateTombStone))]
	private class PreserveItemsOnHardcoreModeDeath
	{
		private static ZPackage? playerSave = null;

		[HarmonyPriority(Priority.VeryHigh)]
		private static void Prefix(Player __instance)
		{
			if (ServerCharacters.hardcoreMode.GetToggle() && iDied)
			{
				playerSave = new ZPackage();
				__instance.Save(playerSave);
				playerSave.SetPos(0);
			}
		}

		[HarmonyPriority(Priority.VeryLow)]
		private static void Postfix(Player __instance)
		{
			if (playerSave is not null && ServerCharacters.hardcoreMode.GetToggle() && iDied)
			{
				__instance.m_knownTexts.Clear();
				__instance.m_knownStations.Clear();
				__instance.Load(playerSave);
				playerSave = null;
			}
		}
	}

	[HarmonyPatch(typeof(Game), nameof(Game.UpdateRespawn))]
	private static class ChangeSpawnShoutMessage
	{
		private static string ReplaceMessage(string original) => ServerCharacters.loginMessage.Value == "I have arrived!" ? original : ServerCharacters.loginMessage.Value;

		private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator ilg)
		{
			MethodInfo sendText = AccessTools.DeclaredMethod(typeof(Chat), nameof(Chat.SendText));
			foreach (CodeInstruction instruction in instructions)
			{
				if (instruction.Calls(sendText))
				{
					Label skipLabel = ilg.DefineLabel();
					Label endLabel = ilg.DefineLabel();
					yield return new CodeInstruction(OpCodes.Call, AccessTools.DeclaredMethod(typeof(ChangeSpawnShoutMessage), nameof(ReplaceMessage)));
					yield return new CodeInstruction(OpCodes.Dup);
					yield return new CodeInstruction(OpCodes.Ldstr, "");
					yield return new CodeInstruction(OpCodes.Call, AccessTools.DeclaredMethod(typeof(string), nameof(string.Equals), new[] { typeof(string), typeof(string) }));
					yield return new CodeInstruction(OpCodes.Brtrue, skipLabel);
					yield return instruction;
					yield return new CodeInstruction(OpCodes.Br, endLabel);
					yield return new CodeInstruction(OpCodes.Pop) { labels = new List<Label> { skipLabel } };
					yield return new CodeInstruction(OpCodes.Pop);
					yield return new CodeInstruction(OpCodes.Pop);
					yield return new CodeInstruction(OpCodes.Nop) { labels = new List<Label> { endLabel } };
				}
				else
				{
					yield return instruction;
				}
			}
		}
	}

	[HarmonyPatch(typeof(FejdStartup), nameof(FejdStartup.ShowConnectError))]
	private class ShowConnectionError
	{
		private static void Postfix(FejdStartup __instance)
		{
			if ((int)ZNet.GetConnectionStatus() == ServerCharacters.CharacterNameDisconnectMagic)
			{
				__instance.m_connectionFailedError.text = "Your character name contains illegal characters. Please choose a different name.";
			}
			if ((int)ZNet.GetConnectionStatus() == ServerCharacters.SingleCharacterModeDisconnectMagic)
			{
				__instance.m_connectionFailedError.text = "You are not allowed to create more than one character on this server.";
			}
			if (__instance.m_connectionFailedPanel.activeSelf && connectionError != null)
			{
				__instance.m_connectionFailedError.text += "\n" + connectionError;
				connectionError = null;
			}
			if (iDied && ServerCharacters.hardcoreMode.GetToggle())
			{
				__instance.m_connectionFailedError.text = "You died on a hardcore server. You can continue to use your character in singleplayer, but will have to create a new one to connect to the server.";
				__instance.m_connectionFailedPanel.SetActive(true);
				iDied = false;
			}
		}
	}

	[HarmonyPatch(typeof(ZNet), nameof(ZNet.RPC_Disconnect))]
	private class PatchZNetRPC_Disconnect
	{
		[UsedImplicitly]
		private static void Prefix(ZNet __instance)
		{
			if (__instance.IsServer())
			{
				return;
			}

			if (serverCharacter)
			{
				if (finalProfileEvent.Length == 0) finalProfileEvent = "ServerCharacters PlayerProfileLogout";
				try
				{
					forceSynchronousSaving = true;
					Game.instance.SavePlayerProfile(true);
				}
				catch (Exception e)
				{
					ServerCharacters.logger.LogError($"Could not perform the final client character save during disconnect: {e}");
				}
				finally
				{
					forceSynchronousSaving = false;
				}
			}
		}
	}

	[HarmonyPatch(typeof(Game), nameof(Game.OnApplicationQuit))]
	private class PatchGameOnApplicationQuit
	{
		[UsedImplicitly]
		private static void Prefix()
		{
			finalProfileEvent = "ServerCharacters PlayerProfileQuit";
			forceSynchronousSaving = true;
		}
	}

	[HarmonyPatch(typeof(Menu), nameof(Menu.QuitGame))]
	private static class ForceSaveOnQuit
	{
		private static void Prefix()
		{
			finalProfileEvent = "ServerCharacters PlayerProfileQuit";
			try
			{
				if (ZNet.m_onlineBackend == OnlineBackendType.PlayFab)
				{
					ZNet.instance.m_haveStoped = false;
					forceSynchronousSaving = false;
					Game.instance.SavePlayerProfile(true);
				}
			}
			catch (Exception e)
			{
				ServerCharacters.logger.LogError($"Could not save the client character while quitting: {e}");
			}
		}
	}

	[HarmonyPatch(typeof(Game), nameof(Game.SavePlayerProfile))]
	private class ForceSavingPosition
	{
		private static bool originalValue = false;

		[UsedImplicitly]
		private static void Prefix(Game __instance, ref bool setLogoutPoint, out bool __state)
		{
			__state = ZNet.instance.m_haveStoped;
			if (__instance.m_shuttingDown)
			{
				// Ensure PlayFab connections do *not* push the sending of player save onto the background compressing queue, but directly send it
				ZNet.instance.m_haveStoped = true;
			}

			if (ZNet.m_world == null || __instance.m_playerProfile.HaveLogoutPoint())
			{
				originalValue = true;
				return;
			}

			originalValue = setLogoutPoint;
			setLogoutPoint = true;
		}

		[UsedImplicitly]
		private static void Postfix(Game __instance)
		{
			if (!originalValue)
			{
				__instance.m_playerProfile.ClearLoguoutPoint();
			}
		}

		[UsedImplicitly]
		private static void Finalizer(bool __state)
		{
			ZNet.instance.m_haveStoped = __state;
		}
	}

	private static byte[] generateProfileSignature(byte[] profileData, byte[] key)
	{
		ZPackage signedContent = new();
		signedContent.Write(profileData);
		signedContent.Write(recoveryBaseline);
		byte[] profileHash = SHA512.Create().ComputeHash(signedContent.GetArray());

		Aes aes = Aes.Create();
		aes.Key = key;
		MemoryStream outputStream = new();
		CryptoStream cryptoStream = new(outputStream, aes.CreateEncryptor(), CryptoStreamMode.Write);
		cryptoStream.Write(profileHash, 0, profileHash.Length);
		cryptoStream.FlushFinalBlock();
		cryptoStream.Close();

		ZPackage package = new();
		package.Write(outputStream.ToArray());
		package.Write(aes.IV);
		package.Write(serverEncryptionTime);
		package.Write(recoveryBaseline);

		return package.GetArray();
	}

	private static void receiveEncryptionKeyFromServer(ZRpc peerRpc, ZPackage keyPackage)
	{
		serverEncryptionKey = keyPackage.ReadByteArray();
		serverEncryptionTime = keyPackage.ReadLong();
	}

	[HarmonyPatch(typeof(Game), nameof(Game.Logout))]
	private class PatchGameLogout
	{
		[UsedImplicitly]
		private static void Prefix()
		{
			if (finalProfileEvent.Length == 0) finalProfileEvent = "ServerCharacters PlayerProfileLogout";
			doEmergencyBackup = ZNet.GetConnectionStatus() != ZNet.ConnectionStatus.Connecting && ZNet.GetConnectionStatus() != ZNet.ConnectionStatus.Connected && !Game.instance.IsShuttingDown();
			if (doEmergencyBackup)
			{
				Utils.Log("Lost connection to the server. Preparing for emergency backup of profile data.");
			}
		}
	}

	private class PlayerSnapshot
	{
		public List<ItemDrop.ItemData> inventory = null!;
		public Dictionary<string, int> knownStations = null!;
		public Dictionary<string, string> knownTexts = null!;
	}

	public static void snapShotProfile()
	{
		try
		{
			if (ZNet.instance?.IsServer() == false && Player.m_localPlayer != null)
			{
				if (ZNet.instance.GetServerPing() < 1.5f)
				{
					playerSnapShotLast = playerSnapShotNew;
				}

				playerSnapShotNew = new PlayerSnapshot
				{
					inventory = Player.m_localPlayer.GetInventory().m_inventory.Select(d => d.Clone()).ToList(),
					knownStations = Player.m_localPlayer.m_knownStations.ToDictionary(t => t.Key, t => t.Value),
					knownTexts = Player.m_localPlayer.m_knownTexts.ToDictionary(t => t.Key, t => t.Value),
				};
			}
		}
		catch (Exception e)
		{
			ServerCharacters.logger.LogError($"Could not cache the local character snapshot; keeping the previous snapshot: {e}");
		}
	}

	public static void SendDisconnectProtectionSnapshot()
	{
		try
		{
			if (!serverCharacter || currentlySaving || doEmergencyBackup || Game.instance?.m_playerProfile == null ||
				Player.m_localPlayer == null || ZNet.instance?.GetServerPeer()?.IsReady() != true) return;

			float now = Time.realtimeSinceStartup;
			if (nextDisconnectProtectionSnapshot <= 0f)
			{
				nextDisconnectProtectionSnapshot = now + ServerCharacters.disconnectProtectionInterval.Value;
				return;
			}
			if (now < nextDisconnectProtectionSnapshot) return;
			nextDisconnectProtectionSnapshot = now + ServerCharacters.disconnectProtectionInterval.Value;

			PlayerProfile profile = Game.instance.m_playerProfile;
			profile.SavePlayerData(Player.m_localPlayer);
			Minimap.instance?.SaveMapData();
			profile.SaveLogoutPoint();
			QueueProfile(Shared.SerializeProfileInMemory(profile), "ServerCharacters PlayerSnapshot", true);
		}
		catch (Exception e)
		{
			nextDisconnectProtectionSnapshot = Time.realtimeSinceStartup + 10f;
			ServerCharacters.logger.LogError($"Could not serialize/send an in-memory disconnect-protection snapshot: {e}");
		}
	}

	[HarmonyPatch(typeof(Inventory), nameof(Inventory.Changed))]
	private class PatchInventoryChanged
	{
		private static bool dirty;
		private static Coroutine? sender;
		private static int generation;
		private static double nextSend;
		private static double Now => System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;

		public static void Reset()
		{
			++generation;
			try
			{
				if (sender != null && ServerCharacters.selfReference != null) ServerCharacters.selfReference.StopCoroutine(sender);
			}
			finally { sender = null; dirty = false; nextSend = 0; }
		}

		private static IEnumerator SendPending(Inventory inventory, ZNetPeer peer, int run)
		{
			// Inventory.Changed is intercepted before its callbacks finish.
			yield return null;
			try
			{
				while (dirty && run == generation)
				{
					if (!serverCharacter || inventory != Player.m_localPlayer?.m_inventory ||
						ZNet.instance?.GetServerPeer() != peer || peer.m_socket?.IsConnected() != true) yield break;
					double delay = nextSend - Now;
					if (delay > 0) { yield return new WaitForSecondsRealtime((float)delay); continue; }
					dirty = false;
					nextSend = Now + 2;
					byte[]? update = null;
					try
					{
						ZPackage package = new();
						inventory.Save(package);
						update = WrapUpdate(package.GetArray());
					}
					catch (Exception e)
					{
						dirty = true;
						ServerCharacters.logger.LogError($"Could not serialize the changed client inventory: {e}");
					}
					if (update == null) continue;
					bool sent = false;
					foreach (bool ready in Shared.sendCompressedDataToPeer(peer, "ServerCharacters PlayerInventory", update, ok => sent = ok, compressed: false))
					{
						if (run != generation) yield break;
						if (!ready) yield return null;
					}
					// Also space retries after a slow transfer; never accumulate old copies.
					nextSend = Now + 2;
					if (!sent) dirty = true;
				}
			}
			finally { if (run == generation) sender = null; }
		}

		private static void Prefix(Inventory __instance)
		{
			try
			{
				if (serverCharacter && __instance == Player.m_localPlayer?.m_inventory && ZNet.instance?.GetServerPeer() is { } serverPeer)
				{
					dirty = true;
					if (sender == null) sender = ServerCharacters.selfReference.StartCoroutine(SendPending(__instance, serverPeer, generation));
				}
			}
			catch (Exception e)
			{
				sender = null;
				ServerCharacters.logger.LogError($"Could not queue the changed client inventory for synchronization: {e}");
			}
		}
	}

	[HarmonyPatch(typeof(Player), nameof(Player.OnDeath))]
	private class KickPlayerOnDeath
	{
		private static void Prefix()
		{
			if (ServerCharacters.hardcoreMode.GetToggle())
			{
				iDied = true;
				Game.instance.Invoke(nameof(Game.Logout), 1);
			}
		}
	}

	[HarmonyPatch]
	private static class MonitorPlayerActivityCheck
	{
		private static IEnumerable<MethodInfo> TargetMethods() => new[]
		{
			AccessTools.DeclaredMethod(typeof(ZInput), nameof(ZInput.GetButton)),
			AccessTools.DeclaredMethod(typeof(ZInput), nameof(ZInput.GetButtonDown)),
		};

		private static void Postfix(ref bool __result)
		{
			if (__result)
			{
				MonitorPlayerActivity.RecordActivity();
			}
		}
	}

	[HarmonyPatch(typeof(Player), nameof(Player.SetLocalPlayer))]
	private static class MonitorPlayerActivity
	{
		private const float PositionActivityThreshold = 0.05f;
		private const float MouseActivityThreshold = 2f;
		private static Coroutine? activityCoroutine;
		private static Player? activityPlayer;
		private static long lastActivity;
		private static int generation;
		private static Vector3 lastWorldPosition;
		private static Vector3 lastMousePosition;
		private static bool hasActivitySample;

		public static void RecordActivity() => lastActivity = System.Diagnostics.Stopwatch.GetTimestamp();

		public static void Stop()
		{
			++generation;
			try
			{
				if (activityCoroutine != null && activityPlayer != null) activityPlayer.StopCoroutine(activityCoroutine);
			}
		finally
		{
			activityCoroutine = null;
			activityPlayer = null;
			hasActivitySample = false;
		}
		}

		private static IEnumerator MeasureActivity(Player owner, int run)
		{
			while (run == generation && owner != null && owner == Player.m_localPlayer)
			{
				yield return new WaitForSecondsRealtime(1);
				if (run != generation || owner == null || owner != Player.m_localPlayer) yield break;
				Vector3 worldPosition = owner.transform.position;
				Vector3 mousePosition = UnityEngine.Input.mousePosition;
				if (!hasActivitySample || Vector3.Distance(worldPosition, lastWorldPosition) >= PositionActivityThreshold || (mousePosition - lastMousePosition).sqrMagnitude >= MouseActivityThreshold * MouseActivityThreshold)
					RecordActivity();
				if (owner.IsAttachedToShip() || owner.InNumShipVolumes > 0 || owner.InBed())
					RecordActivity();
				lastWorldPosition = worldPosition;
				lastMousePosition = mousePosition;
				hasActivitySample = true;
				int minutes = ServerCharacters.afkKickTimer.Value;
				if (minutes <= 0 || ZNet.m_isServer) { RecordActivity(); continue; }
				double elapsed = (System.Diagnostics.Stopwatch.GetTimestamp() - lastActivity) / (double)System.Diagnostics.Stopwatch.Frequency;
				if (elapsed < minutes * 60d) continue;
				try
				{
					Game.instance.Logout();
					ZNet.m_connectionStatus = ZNet.ConnectionStatus.ErrorDisconnected;
					connectionError = "You have been logged out due to inactivity.";
				}
				catch (Exception e)
				{
					ServerCharacters.logger.LogError($"Could not log out the inactive player safely: {e}");
				}
				RecordActivity();
			}
		}

		private static void Postfix(Player __instance)
		{
			try
			{
				Stop();
				RecordActivity();
				lastWorldPosition = __instance.transform.position;
				lastMousePosition = UnityEngine.Input.mousePosition;
				hasActivitySample = true;
				PatchInventoryChanged.Reset();
				activityPlayer = __instance;
				activityCoroutine = __instance.StartCoroutine(MeasureActivity(__instance, generation));
			}
			catch (Exception e)
			{
				ServerCharacters.logger.LogError($"Could not restart the AFK monitor: {e}");
			}
		}
	}
}
