using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using JetBrains.Annotations;
using UnityEngine;
using CompressionLevel = System.IO.Compression.CompressionLevel;

namespace ServerCharacters;

public static class Shared
{
	private static long packageCounter = 1;

	public static IEnumerable<bool> sendCompressedDataToPeer(ZNetPeer peer, string eventname, byte[] packageArray, Action<bool>? completed = null, double timeoutSeconds = 30, bool compressed = true)
	{
		IEnumerator<bool>? sender = null;
		try
		{
			sender = sendCompressedDataToPeerUnsafe(peer, eventname, packageArray, timeoutSeconds, compressed).GetEnumerator();
			while (true)
			{
				bool hasNext;
				bool current;
				Exception? sendError = null;
				try
				{
					hasNext = sender.MoveNext();
					current = hasNext && sender.Current;
				}
				catch (Exception e)
				{
					hasNext = false;
					current = false;
					sendError = e;
				}

				if (sendError != null)
				{
					completed?.Invoke(false);
					ServerCharacters.logger.LogError($"Could not send data for event '{eventname}' to peer {peer?.m_uid}; aborting this transfer without propagating the error: {sendError}");
					yield break;
				}

				if (!hasNext)
				{
					completed?.Invoke(true);
					yield break;
				}

				yield return current;
			}
		}
		finally
		{
			if (sender != null)
			{
				try { sender.Dispose(); }
				catch (Exception e) { ServerCharacters.logger.LogError($"Could not clean up a compressed transfer for event '{eventname}': {e}"); }
			}
		}
	}

	private static IEnumerable<bool> sendCompressedDataToPeerUnsafe(ZNetPeer peer, string eventname, byte[] packageArray, double timeoutSeconds, bool compressed)
	{
		var timeout = System.Diagnostics.Stopwatch.StartNew();
		byte[] data = packageArray;
		if (compressed)
		{
			using MemoryStream output = new();
			using (DeflateStream deflateStream = new(output, CompressionLevel.Optimal, true))
				deflateStream.Write(packageArray, 0, packageArray.Length);
			data = output.ToArray();
		}
		if (data.Length > MaximumCompressedSize) throw new InvalidDataException("Transfer exceeds the allowed wire size.");

		const int packageSliceSize = 250000;
		const int maximumSendQueueSize = 20000;

		IEnumerable<bool> waitForQueue()
		{
			while (peer.m_socket.GetSendQueueSize() > maximumSendQueueSize)
			{
				if (!peer.m_socket.IsConnected()) throw new IOException("Peer disconnected during transfer.");
				if (timeout.Elapsed.TotalSeconds >= timeoutSeconds)
					throw new TimeoutException($"Send queue did not drain within {timeoutSeconds} seconds.");

				yield return false;
			}
		}

		void SendPackage(ZPackage pkg)
		{
			peer.m_rpc.Invoke(eventname, pkg);
		}

		int fragments = (int)(1 + (data.LongLength - 1) / packageSliceSize);
		long packageIdentifier = ++packageCounter;
		for (int fragment = 0; fragment < fragments; ++fragment)
		{
			if (timeout.Elapsed.TotalSeconds >= timeoutSeconds)
				throw new TimeoutException("The profile transfer exceeded its total deadline.");
			foreach (bool wait in waitForQueue())
			{
				yield return wait;
			}

			if (!peer.m_socket.IsConnected())
			{
				throw new IOException("Peer disconnected before the profile transfer completed.");
			}

			ZPackage fragmentedPackage = new();
			fragmentedPackage.Write(packageIdentifier);
			fragmentedPackage.Write(fragment);
			fragmentedPackage.Write(fragments);
			int fragmentOffset = packageSliceSize * fragment;
			int fragmentLength = Math.Min(packageSliceSize, data.Length - fragmentOffset);
			byte[] fragmentData = new byte[fragmentLength];
			Buffer.BlockCopy(data, fragmentOffset, fragmentData, 0, fragmentLength);
			fragmentedPackage.Write(fragmentData);
			SendPackage(fragmentedPackage);

			if (fragment != fragments - 1)
			{
				yield return true;
			}
		}
	}

	private readonly record struct TransferKey(ZRpc Peer, Action<ZRpc, byte[]> Handler, long Id);
	private static readonly Dictionary<TransferKey, SortedDictionary<int, byte[]>> profileCache = new();
	private static readonly Dictionary<TransferKey, long> profileCacheSizes = new();
	private static readonly Dictionary<TransferKey, int> profileCacheCounts = new();
	private static readonly List<KeyValuePair<long, TransferKey>> cacheExpirations = new();
	private const int MaximumFragments = 4096;
	private const int MaximumCompressedSize = 16 * 1024 * 1024;
	private const int MaximumDecompressedSize = 64 * 1024 * 1024;

	public static Action<ZRpc, ZPackage> receiveCompressedFromPeer(Action<ZRpc, byte[]> onReceived, bool compressed = true) => (sender, package) =>
	{
		try
		{
			cacheExpirations.RemoveAll(kv =>
			{
				if (kv.Key < DateTimeOffset.Now.Ticks)
				{
					profileCache.Remove(kv.Value);
					profileCacheSizes.Remove(kv.Value);
					profileCacheCounts.Remove(kv.Value);
					return true;
				}

				return false;
			});

			long uniqueIdentifier = package.ReadLong();
			TransferKey cacheKey = new(sender, onReceived, uniqueIdentifier);
			int fragment = package.ReadInt();
			int fragments = package.ReadInt();
			if (fragments <= 0 || fragments > MaximumFragments || fragment < 0 || fragment >= fragments)
			{
				throw new InvalidDataException($"Invalid fragment {fragment} of {fragments}.");
			}

			if (!profileCache.TryGetValue(cacheKey, out SortedDictionary<int, byte[]> dataFragments))
			{
				dataFragments = new SortedDictionary<int, byte[]>();
				profileCache[cacheKey] = dataFragments;
				profileCacheSizes[cacheKey] = 0;
				profileCacheCounts[cacheKey] = fragments;
				cacheExpirations.Add(new(DateTimeOffset.Now.AddSeconds(60).Ticks, cacheKey));
			}
			if (profileCacheCounts[cacheKey] != fragments)
				throw new InvalidDataException("Fragment count changed within a transfer.");

			byte[] receivedFragment = package.ReadByteArray();
			long previousFragmentSize = dataFragments.TryGetValue(fragment, out byte[] previousFragment) ? previousFragment.LongLength : 0;
			long compressedSize = profileCacheSizes[cacheKey] - previousFragmentSize + receivedFragment.LongLength;
			dataFragments[fragment] = receivedFragment;
			profileCacheSizes[cacheKey] = compressedSize;
			if (compressedSize > MaximumCompressedSize)
			{
				profileCache.Remove(cacheKey);
				profileCacheSizes.Remove(cacheKey);
				profileCacheCounts.Remove(cacheKey);
					throw new InvalidDataException("Transfer exceeds the allowed wire size.");
			}

			if (dataFragments.Count < fragments)
			{
				return;
			}

			profileCache.Remove(cacheKey);
			profileCacheSizes.Remove(cacheKey);
			profileCacheCounts.Remove(cacheKey);
			cacheExpirations.RemoveAll(entry => entry.Value.Equals(cacheKey));

			byte[] compressedData = new byte[checked((int)compressedSize)];
			int compressedOffset = 0;
			foreach (byte[] dataFragment in dataFragments.Values)
			{
				Buffer.BlockCopy(dataFragment, 0, compressedData, compressedOffset, dataFragment.Length);
				compressedOffset += dataFragment.Length;
			}

			if (!compressed)
			{
				onReceived(sender, compressedData);
				return;
			}
			using MemoryStream input = new(compressedData);
			using MemoryStream output = new();
			using (DeflateStream deflateStream = new(input, CompressionMode.Decompress))
			{
				byte[] buffer = new byte[81920];
				int read;
				while ((read = deflateStream.Read(buffer, 0, buffer.Length)) > 0)
				{
					if (output.Length + read > MaximumDecompressedSize)
					{
						throw new InvalidDataException("Decompressed profile exceeds the allowed size.");
					}
					output.Write(buffer, 0, read);
				}
			}

			onReceived(sender, output.ToArray());
		}
		catch (Exception e)
		{
			ServerCharacters.logger.LogError($"Rejected or failed to process data from peer: {e}");
		}
	};

	// Reuses the original serializer, excluding storage setup, file writes and callbacks.
	public static byte[] SerializeProfileInMemory(PlayerProfile profile)
	{
		throw new NotImplementedException("The in-memory profile serializer was not patched.");
	}

	[HarmonyPatch(typeof(Shared), nameof(SerializeProfileInMemory))]
	private static class ProfileMemorySerializer
	{
		private static IEnumerable<CodeInstruction> Transpiler(ILGenerator generator)
		{
			List<CodeInstruction> original = PatchProcessor.GetOriginalInstructions(
				AccessTools.DeclaredMethod(typeof(PlayerProfile), nameof(PlayerProfile.SavePlayerToDisk)), generator).ToList();
			ConstructorInfo constructor = AccessTools.DeclaredConstructor(typeof(ZPackage), Type.EmptyTypes);
			MethodInfo hash = AccessTools.DeclaredMethod(typeof(ZPackage), nameof(ZPackage.GenerateHash));
			int start = original.FindIndex(i => i.opcode == OpCodes.Newobj && Equals(i.operand, constructor));
			int end = original.FindIndex(i => i.Calls(hash));
			if (start < 0 || end <= start)
				throw new InvalidOperationException("Could not isolate Valheim's in-memory profile serialization.");

			List<CodeInstruction> result = original.GetRange(start, end - start);
			if (result[0].blocks.Count != 0)
				throw new InvalidOperationException("Unexpected exception boundary at the profile serializer start.");
			result.Add(new CodeInstruction(OpCodes.Callvirt, AccessTools.DeclaredMethod(typeof(ZPackage), nameof(ZPackage.GetArray))));
			result.Add(new CodeInstruction(OpCodes.Ret));

			HashSet<Label> labels = new(result.SelectMany(i => i.labels));
			int exceptionDepth = 0;
			foreach (CodeInstruction instruction in result)
			{
				foreach (ExceptionBlock block in instruction.blocks)
				{
					if (block.blockType == ExceptionBlockType.BeginExceptionBlock) ++exceptionDepth;
					if (block.blockType == ExceptionBlockType.EndExceptionBlock) --exceptionDepth;
					if (exceptionDepth < 0) throw new InvalidOperationException("Unbalanced serializer exception blocks.");
				}
				if (instruction.operand is Label target && !labels.Contains(target) ||
					instruction.operand is Label[] targets && targets.Any(label => !labels.Contains(label)))
					throw new InvalidOperationException("The profile serializer branches outside its memory-only slice.");
				if (instruction.operand is MethodBase called &&
					(called.DeclaringType == typeof(FileWriter) || called.DeclaringType == typeof(FileHelpers) ||
					 called.DeclaringType == typeof(SaveSystem) || called.DeclaringType?.Namespace == "System.IO"))
					throw new InvalidOperationException("Storage operation found in the in-memory profile serializer.");
			}
			if (exceptionDepth != 0) throw new InvalidOperationException("Unbalanced serializer exception blocks.");
			return result;
		}
	}

	public static bool LoadPlayerProfileFromBytes(this PlayerProfile profile, byte[] data)
	{
		throw new NotImplementedException("Was not patched ...");
	}

	[HarmonyPatch(typeof(Shared), nameof(LoadPlayerProfileFromBytes))]
	private static class LoadPlayerProfileLoader
	{
		// Reuse Valheim's complete profile parser, but replace its disk read with
		// the profile bytes received from the client.
		[UsedImplicitly]
		private static IEnumerable<CodeInstruction> Transpiler(ILGenerator ilGenerator)
		{
			MethodInfo diskLoader = AccessTools.DeclaredMethod(typeof(PlayerProfile), nameof(PlayerProfile.LoadPlayerDataFromDisk));
			ConstructorInfo packageConstructor = AccessTools.DeclaredConstructor(typeof(ZPackage), new[] { typeof(byte[]) });
			List<CodeInstruction> instructions = PatchProcessor.GetOriginalInstructions(
				AccessTools.DeclaredMethod(typeof(PlayerProfile), nameof(PlayerProfile.LoadPlayerFromDisk)), ilGenerator).ToList();

			int loaderCall = instructions.FindIndex(instruction => instruction.Calls(diskLoader));
			if (loaderCall <= 0 || instructions[loaderCall - 1].opcode != OpCodes.Ldarg_0)
			{
				throw new InvalidOperationException("Could not locate PlayerProfile.LoadPlayerDataFromDisk in Valheim's profile loader.");
			}

			CodeInstruction loadData = new(OpCodes.Ldarg_1)
			{
				labels = instructions[loaderCall - 1].labels,
				blocks = instructions[loaderCall - 1].blocks,
			};
			instructions[loaderCall - 1] = loadData;
			instructions[loaderCall] = new CodeInstruction(OpCodes.Newobj, packageConstructor);
			return instructions;
		}
	}

	[HarmonyPatch(typeof(FejdStartup), nameof(FejdStartup.Awake))]
	private static class SetAutoSaveInterval
	{
		private static void Postfix()
		{
			Game.m_saveInterval = ServerCharacters.autoSaveInterval.Value * 60;
		}
	}

	public static bool CharacterNameIsForbidden(string characterName)
	{
		return characterName.Length < 3 || characterName.Any(c => c != ' ' && c != '\'' && !char.IsLetter(c));
	}

	[HarmonyPatch(typeof(SaveSystem), nameof(SaveSystem.GetSaveInfo))]
	private static class FilterServerCharacterFiles
	{
		private static bool Prefix(string path, ref bool __result)
		{
			if (path.EndsWith(".signature", StringComparison.Ordinal) || path.EndsWith(".serverbackup", StringComparison.Ordinal))
			{
				__result = false;
				return false;
			}

			return true;
		}
	}
}
