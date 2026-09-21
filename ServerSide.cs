using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using JetBrains.Annotations;
using UnityEngine;
using System.IO.Compression;
using System.Security.Cryptography;
using Splatform;

namespace ServerCharacters;

public static class ServerSide
{
	private static readonly Dictionary<Utils.ProfileName, byte[]> Inventories = new();
	private static readonly Dictionary<Utils.ProfileName, byte[]> DisconnectProtectionSnapshots = new();
	private static readonly Dictionary<Utils.ProfileName, float> pendingDisconnectSaves = new();
	private static readonly Dictionary<ZNetPeer, Utils.ProfileName> peerProfileNameMap = new();
	private static readonly Dictionary<string, float> saveErrorNotifications = new();
	private static readonly HashSet<long> shutdownSavePending = new();
	private const float SaveErrorNotificationCooldown = 60f;
	private const float SaveErrorNotificationRetention = 600f;
	private const float SaveErrorNotificationCleanupInterval = 300f;
	private const float ShutdownSaveTimeout = 10f;
	private static bool shutdownSaveInProgress;
	private static bool shutdownApproved;
	private static string shutdownRequest = "";
	private static float nextSaveErrorNotificationCleanup;

	public static bool OnApplicationWantsToQuit()
	{
		try
		{
			if (shutdownApproved || ZNet.instance?.IsServer() != true || ServerCharacters.selfReference == null)
			{
				return true;
			}

			if (!shutdownSaveInProgress)
			{
				bool connected = ZNet.instance.GetPeers().Any(peer => peer?.m_rpc != null && peer.m_uid != 0 && peer.m_socket?.IsConnected() == true);
				if (!connected && pendingDisconnectSaves.Count == 0)
				{
					shutdownApproved = true;
					return true;
				}
				shutdownSaveInProgress = true;
				ServerCharacters.selfReference.StartCoroutine(SaveConnectedPlayersBeforeShutdown());
			}
			return false;
		}
		catch (Exception e)
		{
			shutdownApproved = true;
			shutdownSaveInProgress = false;
			ServerCharacters.logger.LogError($"Could not coordinate final character saves; allowing shutdown safely: {e}");
			return true;
		}
	}

	[HarmonyPatch(typeof(Game), nameof(Game.Shutdown))]
	private static class PatchGameShutdownBeforeNetworkStops
	{
		[UsedImplicitly]
		private static bool Prefix() => OnApplicationWantsToQuit();
	}

	private static IEnumerator SaveConnectedPlayersBeforeShutdown()
	{
		// Finish rejecting the original quit request before requesting another one.
		yield return null;
		List<ZNetPeer> peers = new();
		try
		{
			peers = ZNet.instance?.GetPeers()
				.Where(peer => peer?.m_rpc != null && peer.m_uid != 0 && peer.m_socket?.IsConnected() == true)
				.ToList() ?? new List<ZNetPeer>();
		}
		catch (Exception e)
		{
			ServerCharacters.logger.LogError($"Could not enumerate connected players for final saves: {e}");
		}

		try
		{
			shutdownSavePending.Clear();
			shutdownRequest = Guid.NewGuid().ToString("N");
			foreach (ZNetPeer peer in peers)
			{
				shutdownSavePending.Add(peer.m_uid);
			}

			if (shutdownSavePending.Count > 0)
			{
				SendChatMessageToPeers("El servidor se esta cerrando. Guardando personajes conectados...", Talker.Type.Shout);
				foreach (ZNetPeer peer in peers)
				{
					try
					{
						peer.m_rpc.Invoke("ServerCharacters PrepareShutdownSave", shutdownRequest);
						Utils.Log($"Requested a final character save from {peer.m_playerName} ({peer.m_uid}).");
					}
					catch (Exception e)
					{
						ServerCharacters.logger.LogError($"Could not request a final save from peer {peer.m_uid}: {e}");
					}
				}

				var wait = System.Diagnostics.Stopwatch.StartNew();
				while (shutdownSavePending.Count > 0 && wait.Elapsed.TotalSeconds < ShutdownSaveTimeout)
				{
					yield return null;
				}

				if (shutdownSavePending.Count > 0)
				{
					ServerCharacters.logger.LogWarning($"Server shutdown continued after waiting {ShutdownSaveTimeout:0} seconds. Final character saves were not confirmed for peer UID(s): {string.Join(", ", shutdownSavePending)}");
				}
				else
				{
					Utils.Log("Final character saves were confirmed for all connected players.");
				}
			}
		}
		finally
		{
			try
			{
				foreach (Utils.ProfileName name in pendingDisconnectSaves.Keys.ToArray())
				{
					if (!TryFinalizeDisconnectedProfile(name))
						ServerCharacters.logger.LogError($"Shutdown could not persist the retained profile for {name.id}/{name.name}; data is still only in memory.");
				}
			}
			finally
			{
				shutdownSavePending.Clear();
				shutdownApproved = true;
				shutdownSaveInProgress = false;
				Application.Quit();
			}
		}
	}

	private static void MarkShutdownProfileSaved(ZRpc peerRpc, string request)
	{
		if (!shutdownSaveInProgress || request != shutdownRequest) return;
		try
		{
			ZNetPeer? peer = ZNet.instance?.GetPeer(peerRpc);
			if (peer != null && shutdownSavePending.Remove(peer.m_uid))
			{
				Utils.Log($"Confirmed final character save for {peer.m_playerName} ({peer.m_uid}).");
			}
		}
		catch (Exception e)
		{
			ServerCharacters.logger.LogError($"Could not mark a shutdown character save as completed: {e}");
		}
	}

	private static void NotifySaveError(PlayerProfile profile)
	{
		try
		{
			string profileName = string.IsNullOrWhiteSpace(profile.GetName()) ? profile.m_filename : profile.GetName();
			string notificationKey = string.IsNullOrWhiteSpace(profile.m_filename) ? profileName : profile.m_filename;
			float now = Time.realtimeSinceStartup;
			if (now >= nextSaveErrorNotificationCleanup)
			{
				nextSaveErrorNotificationCleanup = now + SaveErrorNotificationCleanupInterval;
				foreach (string expiredKey in saveErrorNotifications
					.Where(entry => now - entry.Value >= SaveErrorNotificationRetention)
					.Select(entry => entry.Key)
					.ToList())
				{
					saveErrorNotifications.Remove(expiredKey);
				}
			}
			if (saveErrorNotifications.TryGetValue(notificationKey, out float lastNotification) && now - lastNotification < SaveErrorNotificationCooldown)
			{
				return;
			}

			saveErrorNotifications[notificationKey] = now;
			string message = $"ERROR al guardar el personaje '{SanitizeChatText(profileName, 80)}'. Es posible que el progreso reciente no se haya guardado. Avise a un administrador.";
			SendChatMessageToPeers(message, Talker.Type.Shout);
		}
		catch (Exception e)
		{
			ServerCharacters.logger.LogError($"Could not broadcast the character save error: {e}");
		}
	}

	private static void SendChatMessageToPeers(string message, Talker.Type type)
	{
		try
		{
			if (ZRoutedRpc.instance == null || ZNet.instance == null || string.IsNullOrWhiteSpace(message))
			{
				return;
			}

			string safeMessage = SanitizeChatText(message, 420);
			foreach (ZNetPeer peer in ZNet.instance.GetPeers().ToList())
			{
				try
				{
					if (peer == null || !TryResolvePlatformUserId(peer, out PlatformUserID platformUserId))
					{
						continue;
					}

					UserInfo sender = new() { Name = "Server Characters", UserId = platformUserId };
					ZRoutedRpc.instance.InvokeRoutedRPC(peer.m_uid, "ChatMessage", new object[] { Vector3.zero, (int)type, sender, safeMessage });
				}
				catch (Exception e)
				{
					ServerCharacters.logger.LogError($"Could not send a Server Characters chat message to peer {peer?.m_uid}: {e.Message}");
				}
			}
		}
		catch (Exception e)
		{
			ServerCharacters.logger.LogError($"Could not enumerate peers for a Server Characters chat message: {e}");
		}
	}

	private static string SanitizeChatText(string value, int maxLength)
	{
		if (string.IsNullOrEmpty(value)) return "";
		return new string(value.Where(c => !char.IsControl(c) && c != '<' && c != '>').Take(maxLength).ToArray()).Trim();
	}

	private static bool TryResolvePlatformUserId(ZNetPeer peer, out PlatformUserID platformUserId)
	{
		platformUserId = PlatformUserID.None;
		try
		{
			string? hostName = peer?.m_socket?.GetHostName();
			if (!string.IsNullOrWhiteSpace(hostName) && PlatformUserID.TryParse(hostName, out platformUserId) && platformUserId.IsValid)
			{
				return true;
			}

			string? numericHost = hostName?.StartsWith("Steam_", StringComparison.OrdinalIgnoreCase) == true ? hostName.Substring("Steam_".Length) : hostName;
			if (ulong.TryParse(numericHost, out ulong steamId) && steamId != 0)
			{
				platformUserId = new PlatformUserID("Steam", steamId, false);
				return platformUserId.IsValid;
			}
		}
		catch (Exception e)
		{
			ServerCharacters.logger.LogError($"Could not resolve the platform ID for peer {peer?.m_uid}: {e.Message}");
		}

		platformUserId = PlatformUserID.None;
		return false;
	}

	private static void TryBackupProfile(PlayerProfile profile)
	{
		try
		{
			backupProfile(profile);
		}
		catch (Exception e)
		{
			ServerCharacters.logger.LogError($"Could not back up player profile '{profile.m_filename}': {e}");
		}
	}

	private static bool SaveProfileOrNotify(PlayerProfile profile)
	{
		try
		{
			if (profile.SavePlayerToDisk())
			{
				return true;
			}

			ServerCharacters.logger.LogError($"Saving player profile '{profile.m_filename}' returned false.");
		}
		catch (Exception e)
		{
			ServerCharacters.logger.LogError($"Could not save player profile '{profile.m_filename}': {e}");
		}

		NotifySaveError(profile);
		return false;
	}

	[HarmonyPatch(typeof(ZNet), nameof(ZNet.OnNewConnection))]
	private static class PatchZNetOnNewConnection
	{
		private sealed class ReceiveState
		{
			public long Profile, Inventory, Snapshot;
			public bool AcceptInventory(long revision) => revision > Math.Max(Profile, Math.Max(Inventory, Snapshot));
			public bool AcceptSnapshot(long revision) => revision > Math.Max(Profile, Snapshot);
			public bool CoversInventory(long revision) => Inventory <= revision;
			public bool CoversSnapshot(long revision) => Snapshot <= revision;
		}
		private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ZRpc, ReceiveState> received = new();
		private static Action<ZRpc, ZPackage> ReceiveUpdate(Action<ZRpc, byte[], long, string> handler, bool compressed = true) =>
			Shared.receiveCompressedFromPeer((rpc, bytes) =>
			{
				ZPackage package = new(bytes);
				long revision = package.ReadLong();
				string request = package.ReadString();
				if (revision <= 0) throw new InvalidDataException("Invalid character revision.");
				handler(rpc, package.ReadByteArray(), revision, request);
			}, compressed);
		private static byte[] deriveKey(long time)
		{
			Rfc2898DeriveBytes encryptionKey = new(ServerCharacters.serverKey.Value, BitConverter.GetBytes(time), 1000);
			return encryptionKey.GetBytes(32);
		}

		[UsedImplicitly]
		private static void Postfix(ZNet __instance, ZNetPeer peer)
		{
			try
			{
				if (!__instance.IsServer()) return;

				peer.m_rpc.Register("ServerCharacters PlayerProfile", ReceiveUpdate((rpc, data, revision, request) => onReceivedProfile(rpc, data, revision, request)));
				peer.m_rpc.Register("ServerCharacters CheckSignature", Shared.receiveCompressedFromPeer(onReceivedSignature));
				peer.m_rpc.Register("ServerCharacters PlayerInventory", ReceiveUpdate((rpc, data, revision, request) => onReceivedInventory(rpc, data, revision), compressed: false));
				peer.m_rpc.Register("ServerCharacters PlayerSnapshot", ReceiveUpdate((rpc, data, revision, request) => onReceivedSnapshot(rpc, data, revision)));
				peer.m_rpc.Register("ServerCharacters PlayerDied", ReceiveUpdate(onPlayerDied));
				long time = DateTime.Now.Ticks;
				byte[] key = deriveKey(time);

				ZPackage package = new();
				package.Write(key);
				package.Write(time);
				peer.m_rpc.Invoke("ServerCharacters KeyExchange", package);

				if (ZNet.m_onlineBackend != OnlineBackendType.Steamworks)
				{
					ServerCharacters.logger.LogFatal("This server is not running on Steam, which is required for this mod to function. Please disable PlayFab (Crossplay) and restart the server.");
				}
			}
			catch (Exception e)
			{
				ServerCharacters.logger.LogError($"Could not initialize Server Characters RPCs for peer {peer?.m_uid}; disconnecting that peer safely: {e}");
				try { __instance.Disconnect(peer); }
				catch (Exception disconnectError) { ServerCharacters.logger.LogError($"Could not disconnect peer after RPC initialization failed: {disconnectError}"); }
			}
		}

		private static void onPlayerDied(ZRpc peerRpc, byte[] profileData, long revision, string request)
		{
			PlayerProfile? profile = onReceivedProfile(peerRpc, profileData, revision, request);
			if (profile is not null)
			{
				string profilePath = Utils.CharacterSavePath + Path.DirectorySeparatorChar + profile.m_filename + ".fch";
				File.Delete(profilePath + ".old");
				File.Move(profilePath, profilePath + ".old");
			}
		}

		private static PlayerProfile? onReceivedProfile(ZRpc peerRpc, byte[] profileData, long revision, string request)
		{
			ReceiveState state = received.GetOrCreateValue(peerRpc);
			if (revision <= state.Profile) return null;
			ZNetPeer? activePeer = ZNet.instance?.GetPeer(peerRpc);
			if (activePeer == null) return null;
			PlayerProfile profile = new(fileSource: FileHelpers.FileSource.Local);
			if (!profile.LoadPlayerProfileFromBytes(profileData))
			{
				Utils.Log($"Encountered invalid data for bytes from ID {Utils.GetPlayerID(peerRpc.m_socket.GetHostName())}");
				// invalid data ...
				return null;
			}

			if (Shared.CharacterNameIsForbidden(profile.GetName()) || !string.Equals(profile.GetName(), activePeer.m_playerName, StringComparison.OrdinalIgnoreCase))
			{
				peerRpc.Invoke("Error", ServerCharacters.CharacterNameDisconnectMagic);
				ZNet.instance?.Disconnect(activePeer);
				Utils.Log($"Client {Utils.GetPlayerID(peerRpc.m_socket.GetHostName())} tried to connect with a bad profile name '{profile.GetName()}' and got disconnected");
				return null;
			}

			profile = new PlayerProfile(Utils.GetPlayerID(peerRpc.m_socket.GetHostName()) + "_" + profile.GetName().ToLower(), FileHelpers.FileSource.Local);
			profile.LoadPlayerProfileFromBytes(profileData);
			
			if (!SaveProfileOrNotify(profile))
			{
				ZNetPeer? failedPeer = ZNet.instance?.GetPeer(peerRpc);
				if (failedPeer != null && revision > state.Snapshot)
				{
					DisconnectProtectionSnapshots[Utils.ProfileName.fromPeer(failedPeer)] = profileData.ToArray();
					state.Snapshot = revision;
					if (state.CoversInventory(revision)) Inventories.Remove(Utils.ProfileName.fromPeer(failedPeer));
				}
				return null;
			}
			ZNetPeer? savedPeer = ZNet.instance?.GetPeer(peerRpc);
			if (savedPeer != null)
			{
				Utils.ProfileName name = Utils.ProfileName.fromPeer(savedPeer);
				if (state.CoversSnapshot(revision)) DisconnectProtectionSnapshots.Remove(name);
				if (state.CoversInventory(revision)) Inventories.Remove(name);
				pendingDisconnectSaves.Remove(name);
			}
			state.Profile = revision;
			MarkShutdownProfileSaved(peerRpc, request);
			try
			{
				ZPackage ack = new();
				ack.Write(revision);
				byte[] savedBytes = profile.LoadPlayerDataFromDisk()?.GetArray() ?? throw new IOException("Saved profile could not be read for acknowledgement.");
				using (SHA512 hash = SHA512.Create()) ack.Write(hash.ComputeHash(savedBytes));
				peerRpc.Invoke("ServerCharacters ProfileSaved", ack);
			}
			catch (Exception e) { ServerCharacters.logger.LogWarning($"Profile saved but acknowledgement failed: {e}"); }
			Utils.Log($"Saved player profile data for {profile.m_filename}");

			return profile;
		}

		private static void onReceivedInventory(ZRpc peerRpc, byte[] inventoryData, long revision)
		{
			ReceiveState state = received.GetOrCreateValue(peerRpc);
			ZNetPeer? peer = ZNet.instance?.GetPeer(peerRpc);
			if (peer == null || !state.AcceptInventory(revision))
			{
				return;
			}
			Inventories[Utils.ProfileName.fromPeer(peer)] = inventoryData;
			state.Inventory = revision;
		}

		private static void onReceivedSnapshot(ZRpc peerRpc, byte[] profileData, long revision)
		{
			try
			{
				ZNetPeer? peer = ZNet.instance?.GetPeer(peerRpc);
				ReceiveState state = received.GetOrCreateValue(peerRpc);
				if (!state.AcceptSnapshot(revision)) return;
				if (peer == null || profileData == null || profileData.Length == 0)
				{
					return;
				}

				PlayerProfile profile = new(fileSource: FileHelpers.FileSource.Local);
				if (!profile.LoadPlayerProfileFromBytes(profileData) || Shared.CharacterNameIsForbidden(profile.GetName()) || !string.Equals(profile.GetName(), peer.m_playerName, StringComparison.OrdinalIgnoreCase))
				{
					ServerCharacters.logger.LogWarning($"Rejected an invalid disconnect-protection snapshot from peer {peer.m_uid}.");
					return;
				}

				DisconnectProtectionSnapshots[Utils.ProfileName.fromPeer(peer)] = profileData.ToArray();
				state.Snapshot = revision;
				if (state.CoversInventory(revision)) Inventories.Remove(Utils.ProfileName.fromPeer(peer));
			}
			catch (Exception e)
			{
				ServerCharacters.logger.LogError($"Could not cache a disconnect-protection snapshot: {e}");
			}
		}

		private static void onReceivedSignature(ZRpc peerRpc, byte[] signedProfile)
		{
			ZPackage signedProfilePackage = new(signedProfile);
			byte[] profileData = signedProfilePackage.ReadByteArray();
			byte[] signature = signedProfilePackage.ReadByteArray();

			if (!VerifySignature(profileData, signature, out byte[] baseline))
			{
				Utils.Log($"Client {Utils.GetPlayerID(peerRpc.m_socket.GetHostName())} tried to restore an emergency backup, but signature was invalid. Skipping.");
				return;
			}

			PlayerProfile profile = new();
			if (!profile.LoadPlayerProfileFromBytes(profileData) || Shared.CharacterNameIsForbidden(profile.GetName()))
			{
				// invalid data ...
				Utils.Log($"Client {Utils.GetPlayerID(peerRpc.m_socket.GetHostName())} tried to restore an emergency backup, but the profile data is corrupted.");
				return;
			}

			profile = new PlayerProfile(Utils.GetPlayerID(peerRpc.m_socket.GetHostName()) + "_" + profile.GetName().ToLower(), FileHelpers.FileSource.Local);
			profile.LoadPlayerProfileFromBytes(profileData);

			string profilePath = Utils.CharacterSavePath + Path.DirectorySeparatorChar + profile.m_filename + ".fch";
			FileInfo profileFileInfo = new(profilePath);
			if (!profileFileInfo.Exists)
			{
				Utils.Log($"Client {Utils.GetPlayerID(peerRpc.m_socket.GetHostName())} tried to restore an emergency backup for the character '{profile.m_filename}' that does not belong to this server. Skipping.");
				return;
			}

			byte[]? currentBytes = profile.LoadPlayerDataFromDisk()?.GetArray();
			using SHA512 baselineHasher = SHA512.Create();
			if (currentBytes == null || !baselineHasher.ComputeHash(currentBytes).SequenceEqual(baseline))
			{
				Utils.Log($"Emergency backup for {profile.m_filename} conflicts with the current server profile; automatic restore was skipped.");
				return;
			}

			if (!Inventories.TryGetValue(Utils.ProfileName.fromPeer(ZNet.instance.GetPeer(peerRpc)), out byte[] inventory))
			{
				PlayerProfile oldProfile = new(profile.m_filename);
				oldProfile.LoadPlayerFromDisk();
				inventory = ReadInventoryFromProfile(oldProfile);
			}

			PatchPlayerProfileInventory(profile, inventory);

			if (!SaveProfileOrNotify(profile))
			{
				return;
			}
			Utils.Log($"Client {Utils.GetPlayerID(peerRpc.m_socket.GetHostName())} succesfully restored an emergency backup for {profile.m_filename}.");
			peerRpc.Invoke("ServerCharacters EmergencyRestored");
		}

		private static bool VerifySignature(byte[] profileData, byte[] signature, out byte[] baseline)
		{
			if (!EmergencySignature.TryRead(signature, out byte[] encryptedHash, out byte[] iv, out long time, out baseline, out string reason))
			{
				ServerCharacters.logger.LogWarning($"Emergency recovery skipped: {reason}. No character files were changed.");
				return false;
			}
			ZPackage signedContent = new();
			signedContent.Write(profileData);
			signedContent.Write(baseline);
			using SHA512 hasher = SHA512.Create();
			byte[] profileHash = hasher.ComputeHash(signedContent.GetArray());
			byte[] key = deriveKey(time);

			using Aes aes = Aes.Create();
			aes.Key = key;
			aes.IV = iv;
			using MemoryStream inputStream = new(encryptedHash);
			using CryptoStream cryptoStream = new(inputStream, aes.CreateDecryptor(), CryptoStreamMode.Read);
			using MemoryStream outputStream = new();
			try { cryptoStream.CopyTo(outputStream); }
			catch (CryptographicException)
			{
				ServerCharacters.logger.LogWarning("Emergency recovery skipped: signature could not be authenticated. Backup retained.");
				return false;
			}
			byte[] decryptedHash = outputStream.ToArray();

			return profileHash.SequenceEqual(decryptedHash);
		}
	}

	private static bool TryFinalizeDisconnectedProfile(Utils.ProfileName name)
	{
		PlayerProfile? profile = null;
		try
		{
			Inventories.TryGetValue(name, out byte[]? inventory);
			DisconnectProtectionSnapshots.TryGetValue(name, out byte[]? snapshot);
			profile = new PlayerProfile(name.id + "_" + name.name.ToLower(), FileHelpers.FileSource.Local);
			bool loaded = snapshot != null ? profile.LoadPlayerProfileFromBytes(snapshot) : profile.LoadPlayerFromDisk();
			if (!loaded)
			{
				if (snapshot == null && inventory == null) return true;
				throw new InvalidDataException("Could not load the retained profile while finalizing a disconnect.");
			}
			FileInfo file = new(profile.GetPath());
			DateTime previousTime = file.Exists ? file.LastWriteTime : DateTime.MinValue;
			if (inventory != null) PatchPlayerProfileInventory(profile, inventory);
			if (!SaveProfileOrNotify(profile)) return false;

			// Remove recovery data only after the disk save succeeded.
			Inventories.Remove(name);
			DisconnectProtectionSnapshots.Remove(name);
			pendingDisconnectSaves.Remove(name);
			if (snapshot == null && previousTime != DateTime.MinValue)
			{
				try { File.SetLastWriteTime(file.FullName, previousTime); }
				catch (Exception e) { ServerCharacters.logger.LogWarning($"Profile saved, but its previous timestamp could not be restored: {e}"); }
			}
			Utils.Log($"Saved the retained profile for disconnected player {name.name}.");
			return true;
		}
		catch (Exception e)
		{
			ServerCharacters.logger.LogError($"Could not finalize {name.id}/{name.name}; recovery data remains in memory for retry: {e}");
			if (profile != null) NotifySaveError(profile);
			return false;
		}
	}

	// Called from the existing one-second tick; retry at most one profile per tick.
	public static void RetryDisconnectedSaves()
	{
		try
		{
			if (ZNet.instance?.IsServer() != true) return;
			float now = Time.realtimeSinceStartup;
			foreach (var pending in pendingDisconnectSaves.ToArray())
			{
				if (pending.Value > now) continue;
				if (TryFinalizeDisconnectedProfile(pending.Key)) pendingDisconnectSaves.Remove(pending.Key);
				else pendingDisconnectSaves[pending.Key] = now + 30;
				break;
			}
		}
		catch (Exception e) { ServerCharacters.logger.LogError($"Could not retry a retained character save: {e}"); }
	}

	[HarmonyPatch(typeof(ZNet), nameof(ZNet.Disconnect))]
	private class PatchZNetDisconnect
	{
		private static void Prefix(ZNetPeer peer)
		{
			try
			{
				if (peer != null && ZNet.instance?.IsServer() == true && peerProfileNameMap.TryGetValue(peer, out Utils.ProfileName name))
				{
					if (!TryFinalizeDisconnectedProfile(name)) pendingDisconnectSaves[name] = Time.realtimeSinceStartup + 30;
				}
			}
			catch (Exception e) { ServerCharacters.logger.LogError($"Could not finalize the disconnect: {e}"); }
			finally { if (peer != null) peerProfileNameMap.Remove(peer); }
		}
	}

	[HarmonyPatch(typeof(ZNet), nameof(ZNet.RPC_PeerInfo))]
	private class SendConfigsAfterLogin
	{
		private class BufferingSocket : ISocket
		{
			public volatile bool finished = false;
			public volatile int versionMatchQueued = -1;
			public readonly List<ZPackage> Package = new();
			public readonly ISocket Original;

			public BufferingSocket(ISocket original)
			{
				Original = original;
			}

			public bool IsConnected() => Original.IsConnected();
			public ZPackage Recv() => Original.Recv();
			public int GetSendQueueSize() => Original.GetSendQueueSize();
			public int GetCurrentSendRate() => Original.GetCurrentSendRate();
			public bool IsHost() => Original.IsHost();
			public void Dispose() => Original.Dispose();
			public bool GotNewData() => Original.GotNewData();
			public void Close() => Original.Close();
			public string GetEndPointString() => Original.GetEndPointString();
			public void GetAndResetStats(out int totalSent, out int totalRecv) => Original.GetAndResetStats(out totalSent, out totalRecv);
			public void GetConnectionQuality(out float localQuality, out float remoteQuality, out int ping, out float outByteSec, out float inByteSec) => Original.GetConnectionQuality(out localQuality, out remoteQuality, out ping, out outByteSec, out inByteSec);
			public ISocket Accept() => Original.Accept();
			public int GetHostPort() => Original.GetHostPort();
			public bool Flush() => Original.Flush();
			public string GetHostName() => Original.GetHostName();

			public void VersionMatch()
			{
				if (finished)
				{
					Original.VersionMatch();
				}
				else
				{
					versionMatchQueued = Package.Count;
				}
			}

			public void Send(ZPackage pkg)
			{
				int oldPos = pkg.GetPos();
				pkg.SetPos(0);
				int methodHash = pkg.ReadInt();
				if ((methodHash == "PeerInfo".GetStableHashCode() || methodHash == "RoutedRPC".GetStableHashCode() || methodHash == "ZDOData".GetStableHashCode()) && !finished)
				{
					ZPackage newPkg = new(pkg.GetArray());
					newPkg.SetPos(oldPos);
					Package.Add(newPkg); // the original ZPackage gets reused, create a new one
				}
				else
				{
					pkg.SetPos(oldPos);
					Original.Send(pkg);
				}
			}
		}

		[HarmonyPriority(Priority.First)]
		private static void Prefix(ref BufferingSocket? __state, ZNet __instance, ZRpc rpc)
		{
			if (__instance.IsServer())
			{
				__state = new BufferingSocket(rpc.GetSocket());
				rpc.m_socket = __state;
				if (ZNet.instance.GetPeer(rpc) is { } peer && ZNet.m_onlineBackend != OnlineBackendType.Steamworks)
				{
					peer.m_socket = __state;
				}
			}
		}

		private static void Postfix(BufferingSocket __state, ZNet __instance, ZRpc rpc)
		{
			if (!__instance.IsServer())
			{
				return;
			}

			ZNetPeer peer = __instance.GetPeer(rpc);

			void restoreOriginalSocket()
			{
				try
				{
					if (rpc.GetSocket() is BufferingSocket bufferingSocket)
					{
						rpc.m_socket = bufferingSocket.Original;
						if (peer != null) peer.m_socket = bufferingSocket.Original;
					}
				}
				catch (Exception e)
				{
					ServerCharacters.logger.LogError($"Could not restore the original socket after the Server Characters handshake: {e}");
				}
			}

			try
			{
				if (peer == null)
				{
					restoreOriginalSocket();
					ServerCharacters.logger.LogError("Could not continue the Server Characters handshake because the connecting peer was unavailable.");
					return;
				}
				Utils.ProfileName name = Utils.ProfileName.fromPeer(peer);
				if (pendingDisconnectSaves.ContainsKey(name))
				{
					if (!TryFinalizeDisconnectedProfile(name))
					{
						restoreOriginalSocket();
						__instance.Disconnect(peer);
						return;
					}
					pendingDisconnectSaves.Remove(name);
				}
				peerProfileNameMap[peer] = name;
			}
			catch (Exception e)
			{
				restoreOriginalSocket();
				ServerCharacters.logger.LogError($"Could not identify the connecting peer for the Server Characters handshake: {e}");
				return;
			}

			IEnumerator sendAsync()
			{
				if (peer.m_uid != 0)
				{
					PlayerProfile playerProfile = new(Utils.GetPlayerID(peer.m_socket.GetHostName()) + "_" + peer.m_playerName.ToLower(), FileHelpers.FileSource.Local);
					byte[] playerProfileData = playerProfile.LoadPlayerDataFromDisk()?.GetArray() ?? Array.Empty<byte>();

					if (playerProfileData.Length == 0 && ServerCharacters.singleCharacterMode.GetToggle() && !__instance.ListContainsId(__instance.m_adminList, peer.m_rpc.GetSocket().GetHostName()) && Utils.HasCharacterForPlayer(Utils.GetPlayerID(peer.m_rpc.GetSocket().GetHostName())))
					{
						peer.m_rpc.Invoke("Error", ServerCharacters.SingleCharacterModeDisconnectMagic);
						Utils.Log($"Non-admin client {Utils.GetPlayerID(peer.m_rpc.GetSocket().GetHostName())} tried to create a second character and got disconnected");
						__instance.Disconnect(peer);
						yield break;
					}

					if (!ServerCharacters.backupOnlyMode.GetToggle())
					{
						bool profileSent = false;
						foreach (bool sending in Shared.sendCompressedDataToPeer(peer, "ServerCharacters PlayerProfile", playerProfileData, ok => profileSent = ok))
						{
							if (!sending)
							{
								yield return null;
							}
						}
						if (!profileSent) throw new IOException("Could not deliver the server profile during login.");
					}
				}

				if (rpc.GetSocket() is BufferingSocket bufferingSocket)
				{
					rpc.m_socket = bufferingSocket.Original;
					peer.m_socket = bufferingSocket.Original;
				}

				bufferingSocket = __state;
				bufferingSocket.finished = true;

				for (int i = 0; i < bufferingSocket.Package.Count; ++i)
				{
					if (i == bufferingSocket.versionMatchQueued)
					{
						bufferingSocket.Original.VersionMatch();
					}
					bufferingSocket.Original.Send(bufferingSocket.Package[i]);
				}
				if (bufferingSocket.Package.Count == bufferingSocket.versionMatchQueued)
				{
					bufferingSocket.Original.VersionMatch();
				}
			}

			IEnumerator sendSafely()
			{
				IEnumerator operation = sendAsync();
				while (true)
				{
					bool hasNext;
					object? current;
					Exception? handshakeError = null;
					try
					{
						hasNext = operation.MoveNext();
						current = hasNext ? operation.Current : null;
					}
					catch (Exception e)
					{
						hasNext = false;
						current = null;
						handshakeError = e;
					}

					if (handshakeError != null)
					{
						ServerCharacters.logger.LogError($"Server Characters handshake failed for peer {peer?.m_uid}; restoring its socket so the server thread can continue: {handshakeError}");
						restoreOriginalSocket();
						try { __instance.Disconnect(peer); }
						catch (Exception e) { ServerCharacters.logger.LogError($"Could not disconnect the failed login: {e}"); }
						yield break;
					}

					if (!hasNext) yield break;
					yield return current;
				}
			}

			try
			{
				__instance.StartCoroutine(sendSafely());
			}
			catch (Exception e)
			{
				restoreOriginalSocket();
				ServerCharacters.logger.LogError($"Could not start the Server Characters handshake for peer {peer?.m_uid}: {e}");
			}
		}
	}

	[HarmonyPatch(typeof(ZNet), nameof(ZNet.InternalKick), typeof(ZNetPeer))]
	private static class PatchZNetKick
	{
		private static readonly MethodInfo DisconnectSender = AccessTools.DeclaredMethod(typeof(ZNet), nameof(ZNet.SendDisconnect), new[] { typeof(ZNetPeer) });

		[UsedImplicitly]
		private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
		{
			List<CodeInstruction> instructionList = instructions.ToList();
			foreach (CodeInstruction instruction in instructionList)
			{
				yield return instruction;
				if (instruction.opcode == OpCodes.Call && instruction.OperandIs(DisconnectSender))
				{
					yield return instructionList.Last(); // ret opcode
					// Skip this.Disconnect() call
					yield break;
				}
			}
		}

		[UsedImplicitly]
		private static void Postfix(ZNet __instance, ZNetPeer peer)
		{
			if (!peer.m_socket.IsConnected())
			{
				__instance.Disconnect(peer);
				return;
			}

			int endTime = ServerCharacters.monotonicCounter + 30;

			IEnumerator shutdownAfterSave()
			{
				yield return new WaitWhile(() => peer.m_socket.IsConnected() && endTime > ServerCharacters.monotonicCounter);
				__instance.Disconnect(peer);
			}

			__instance.StartCoroutine(shutdownAfterSave());
		}
	}

	[HarmonyPatch(typeof(PlayerProfile), nameof(PlayerProfile.SavePlayerToDisk))]
	private static class PatchPlayerProfileSave_Server
	{
		private static void Postfix(PlayerProfile __instance, bool __result)
		{
			if (__result && ZNet.instance?.IsServer() == true)
			{
				TryBackupProfile(__instance);
			}
		}
	}

	private static void backupProfile(PlayerProfile profile)
	{
		string[] parts = profile.m_filename.Split('_');
		Utils.Cache.profiles[new Utils.ProfileName { id = parts.Length > 1 ? $"{parts[0]}_{parts[1]}" : parts[0], name = profile.GetName().ToLower() }] = profile;
		string saveFile = SaveSystem.GetCharacterFolderPath(profile.m_fileSource) + profile.m_filename + ".fch.old";
		if (!FileHelpers.Exists(saveFile, profile.m_fileSource)) return;
		string zipPath = Path.Combine(Utils.CharacterSavePath, "backups", profile.m_filename + ".zip");
		bool created = ProfileBackupStore.Update(zipPath, profile.m_filename, () =>
		{
			FileReader reader = new(saveFile, profile.m_fileSource);
			try
			{
				Stream source = reader.m_stream?.BaseStream ?? reader.m_binary.BaseStream;
				if (source.CanSeek) source.Position = 0;
				using MemoryStream data = new();
				source.CopyTo(data);
				return data.ToArray();
			}
			finally { reader.Dispose(); }
		});
		if (created) Utils.Log($"Created a character backup in '{zipPath}' (maximum two copies, minimum interval 30 minutes).");
	}

	public static void generateServerKey()
	{
		if (ServerCharacters.serverKey.Value == "")
		{
			byte[] key = new byte[32];
			using RNGCryptoServiceProvider rngCsp = new();
			rngCsp.GetBytes(key);

			ServerCharacters.serverKey.Value = Convert.ToBase64String(key);
		}
	}

	private static byte[] ReadInventoryFromProfile(PlayerProfile profile)
	{
		ZPackage playerPackage = new(profile.m_playerData);
		ConsumePlayerSaveUntilInventory(playerPackage);
		int startPos = playerPackage.GetPos();
		new Inventory("Inventory", null, 8, 4).Load(playerPackage);
		int endPos = playerPackage.GetPos();

		return profile.m_playerData.Skip(startPos).Take(endPos - startPos).ToArray();
	}

	private static void PatchPlayerProfileInventory(PlayerProfile profile, byte[] inventoryData)
	{
		ZPackage playerPackage = new(profile.m_playerData);
		ConsumePlayerSaveUntilInventory(playerPackage);
		int startPos = playerPackage.GetPos();
		new Inventory("Inventory", null, 8, 4).Load(playerPackage);
		int endPos = playerPackage.GetPos();

		byte[] newData = new byte[startPos + (profile.m_playerData.LongLength - endPos) + inventoryData.Length];
		Array.Copy(profile.m_playerData, newData, startPos);
		Array.Copy(inventoryData, 0, newData, startPos, inventoryData.Length);
		Array.Copy(profile.m_playerData, endPos, newData, startPos + inventoryData.Length, profile.m_playerData.LongLength - endPos);

		profile.m_playerData = newData;
	}

	public static void ConsumePlayerSaveUntilInventory(ZPackage pkg)
	{
		throw new NotImplementedException("Was not patched ...");
	}

	// This transpiler removes all manipulation on the Player object, leaving only the bare calls to Read*() functions on the ZPackage, up to the Inventory.Load() or Skills.Load call. All arguments of operations on Player objects are popped away and the return value replaced by a dummy value.
	// We can use this to observe how far Player.Load() reads into the ZPackage before reading the inventory, allowing us to splice it in and out from raw profile player data.
	private static IEnumerable<CodeInstruction> PlayerProfileConsumeUntil(ILGenerator ilGenerator, MethodInfo endOperand)
	{
		List<CodeInstruction> instructions = PatchProcessor.GetOriginalInstructions(AccessTools.DeclaredMethod(typeof(Player), nameof(Player.Load)), ilGenerator).ToList();
		for (int i = 0; i < instructions.Count; ++i)
		{
			if (instructions[i].opcode == OpCodes.Ldarg_0)
			{
				if (instructions[i + 1].opcode == OpCodes.Ldfld)
				{
					if (instructions[i + 1].operand is FieldInfo { FieldType.IsClass: true } field)
					{
						if (field.FieldType == typeof(Inventory))
						{
							yield return new CodeInstruction(OpCodes.Ldnull)
							{
								labels = instructions[i++].labels,
							};
							yield return new CodeInstruction(OpCodes.Ldnull);
							yield return new CodeInstruction(OpCodes.Ldc_I4_0);
							yield return new CodeInstruction(OpCodes.Ldc_I4_0);
							yield return new CodeInstruction(OpCodes.Newobj, typeof(Inventory).GetConstructors()[0]);
						}
						else
						{
							yield return new CodeInstruction(OpCodes.Ldtoken, field.FieldType)
							{
								labels = instructions[i++].labels,
							};
							yield return new CodeInstruction(OpCodes.Call, AccessTools.DeclaredMethod(typeof(Type), nameof(Type.GetTypeFromHandle)));
							yield return new CodeInstruction(OpCodes.Call, AccessTools.DeclaredMethod(typeof(AccessTools), nameof(AccessTools.CreateInstance), new[] { typeof(Type) }));
						}
					}
					else
					{
						yield return new CodeInstruction(OpCodes.Ldc_I4_0)
						{
							labels = instructions[i++].labels,
						};
					}

					continue;
				}
			}

			if (instructions[i].opcode == OpCodes.Stfld)
			{
				yield return new CodeInstruction(OpCodes.Pop);
				yield return new CodeInstruction(OpCodes.Pop);
				continue;
			}

			if (instructions[i].opcode == OpCodes.Ldarg_1)
			{
				instructions[i].opcode = OpCodes.Ldarg_0;
				yield return instructions[i];
				continue;
			}

			if (instructions[i].opcode == OpCodes.Callvirt && instructions[i].OperandIs(endOperand))
			{
				yield return new CodeInstruction(OpCodes.Pop);
				yield return new CodeInstruction(OpCodes.Pop);
				yield return new CodeInstruction(OpCodes.Ret) { labels = instructions[i + 1].labels };
				break;
			}

			if ((instructions[i].opcode == OpCodes.Callvirt || instructions[i].opcode == OpCodes.Call) && instructions[i].operand is MethodInfo method && (method.DeclaringType?.IsAssignableFrom(typeof(Player)) == true || method.DeclaringType?.IsAssignableFrom(typeof(ZLog)) == true) && method.Name != "op_Equality")
			{
				for (int j = method.IsStatic ? 0 : -1; j < method.GetParameters().Length; ++j)
				{
					yield return new CodeInstruction(OpCodes.Pop);
				}

				if (method.ReturnType != typeof(void))
				{
					if (method.ReturnType == typeof(float))
					{
						yield return new CodeInstruction(OpCodes.Ldc_R4, 0f);
					}
					else if (method.ReturnType == typeof(int))
					{
						yield return new CodeInstruction(OpCodes.Ldc_I4_0);
					}
					else
					{
						yield return new CodeInstruction(OpCodes.Ldnull);
					}
				}

				continue;
			}

			yield return instructions[i];
		}
	}

	[HarmonyPatch(typeof(ServerSide), nameof(ConsumePlayerSaveUntilInventory))]
	private static class PlayerProfileConsumptionStartInventory
	{
		[UsedImplicitly]
		private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator ilGenerator)
		{
			MethodInfo inventorySave = AccessTools.DeclaredMethod(typeof(Inventory), nameof(Inventory.Load), new[] { typeof(ZPackage) });
			return PlayerProfileConsumeUntil(ilGenerator, inventorySave);
		}
	}

}
