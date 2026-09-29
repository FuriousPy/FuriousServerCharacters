$ErrorActionPreference = 'Stop'
$taskBin = Join-Path (Split-Path $PSScriptRoot -Parent) 'bin\Release'
$resolver = [System.ResolveEventHandler] {
    param($sender, $args)
    $candidate = Join-Path $taskBin ((New-Object System.Reflection.AssemblyName($args.Name)).Name + '.dll')
    if (Test-Path -LiteralPath $candidate) { return [System.Reflection.Assembly]::LoadFrom($candidate) }
    return $null
}
[AppDomain]::CurrentDomain.add_AssemblyResolve($resolver)
try {
    foreach ($dependency in @('netstandard.dll', 'UnityEngine.CoreModule.dll', 'assembly_utils.dll', 'assembly_valheim.dll', 'FuriousServerCharacters.dll')) {
        [System.Reflection.Assembly]::LoadFrom((Join-Path $taskBin $dependency)) | Out-Null
    }
    Add-Type -ReferencedAssemblies @((Join-Path $taskBin 'assembly_valheim.dll'), (Join-Path $taskBin 'assembly_utils.dll'), (Join-Path $taskBin 'FuriousServerCharacters.dll'), (Join-Path $taskBin 'UnityEngine.CoreModule.dll'), (Join-Path $taskBin 'netstandard.dll'), 'System.Core.dll', 'System.IO.Compression.dll') -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using ServerCharacters;
public static class TransferProtocolChecks {
    static int checks;
    static void Check(bool value, string name) {
        if (!value) throw new Exception(name);
        System.Console.WriteLine("PASS: " + name); ++checks;
    }
    static byte[] Compress(byte[] data) {
        using (var output = new MemoryStream()) {
            using (var zip = new DeflateStream(output, CompressionMode.Compress, true)) zip.Write(data, 0, data.Length);
            return output.ToArray();
        }
    }
    static ZPackage Part(byte[] data, int index, long id) {
        var pkg = new ZPackage(); int split = data.Length / 2;
        pkg.Write(id); pkg.Write(index); pkg.Write(2);
        pkg.Write(index == 0 ? data.Take(split).ToArray() : data.Skip(split).ToArray());
        pkg.SetPos(0); return pkg;
    }
    public static void Run() {
        var a = (ZRpc)FormatterServices.GetUninitializedObject(typeof(ZRpc));
        var b = (ZRpc)FormatterServices.GetUninitializedObject(typeof(ZRpc));
        byte[] first = Enumerable.Range(0, 4000).Select(i => (byte)(i % 251)).ToArray();
        byte[] second = Enumerable.Range(0, 6000).Select(i => (byte)(i % 179)).ToArray();
        byte[] ca = Compress(first), cb = Compress(second);
        var output = new Dictionary<ZRpc, byte[]>();
        var receiver = Shared.receiveCompressedFromPeer((rpc, data) => output[rpc] = data);
        receiver(a, Part(ca, 0, 42)); receiver(b, Part(cb, 0, 42));
        receiver(a, Part(ca, 1, 42)); receiver(b, Part(cb, 1, 42));
        Check(output[a].SequenceEqual(first) && output[b].SequenceEqual(second), "same transfer ID on two peers stays isolated");
        byte[] eventA = null, eventB = null;
        var receiverA = Shared.receiveCompressedFromPeer((rpc, data) => eventA = data);
        var receiverB = Shared.receiveCompressedFromPeer((rpc, data) => eventB = data);
        receiverA(a, Part(ca, 0, 43)); receiverB(a, Part(cb, 0, 43));
        receiverB(a, Part(cb, 1, 43)); receiverA(a, Part(ca, 1, 43));
        Check(eventA.SequenceEqual(first) && eventB.SequenceEqual(second), "RPC handlers stay isolated on the same peer");
        receiver(a, Part(cb, 1, 44)); receiver(a, Part(cb, 0, 44));
        Check(output[a].SequenceEqual(second), "out-of-order fragments reconstruct correctly");
        receiver(a, Part(ca, 0, 45)); receiver(a, Part(ca, 0, 45)); receiver(a, Part(ca, 1, 45));
        Check(output[a].SequenceEqual(first), "duplicate fragments do not corrupt size accounting");
        byte[] rawInventory = null;
        var rawReceiver = Shared.receiveCompressedFromPeer((rpc, data) => rawInventory = data, false);
        rawReceiver(a, Part(first, 0, 46)); rawReceiver(a, Part(first, 1, 46));
        Check(rawInventory.SequenceEqual(first), "uncompressed inventory fragments round trip without inflation");
        rawReceiver(a, Part(second, 1, 47)); rawReceiver(a, Part(second, 0, 47));
        Check(rawInventory.SequenceEqual(second), "uncompressed inventory supports out-of-order fragments");
        var flags = BindingFlags.NonPublic | BindingFlags.Static;
		var shutdownRoutine = (System.Collections.IEnumerator)typeof(ServerSide).GetMethod("SaveConnectedPlayersBeforeShutdown", flags).Invoke(null, null);
		Check(shutdownRoutine.MoveNext() && shutdownRoutine.Current == null, "shutdown coordination yields before any nested quit request");
		((IDisposable)shutdownRoutine).Dispose();
        var stateType = typeof(ServerSide).GetNestedType("PatchZNetOnNewConnection", BindingFlags.NonPublic).GetNestedType("ReceiveState", BindingFlags.NonPublic);
        object state = Activator.CreateInstance(stateType);
        stateType.GetField("Profile").SetValue(state, 10L);
        stateType.GetField("Inventory").SetValue(state, 15L);
        stateType.GetField("Snapshot").SetValue(state, 12L);
        Check(!(bool)stateType.GetMethod("CoversInventory").Invoke(state, new object[] { 13L }), "older completed profile retains newer inventory");
        Check((bool)stateType.GetMethod("CoversInventory").Invoke(state, new object[] { 15L }), "new profile clears only inventory it covers");
        Check(!(bool)stateType.GetMethod("AcceptInventory").Invoke(state, new object[] { 14L }), "late inventory cannot replace a newer inventory");
        Check(!(bool)stateType.GetMethod("CoversSnapshot").Invoke(state, new object[] { 11L }), "older completed profile retains newer snapshot");
        Check(!(bool)stateType.GetMethod("AcceptSnapshot").Invoke(state, new object[] { 11L }), "late snapshot cannot replace a newer snapshot");
        Check((bool)stateType.GetMethod("AcceptSnapshot").Invoke(state, new object[] { 13L }), "newer profile snapshot can retain an even newer inventory");
        var wrap = typeof(ClientSide).GetMethod("WrapUpdate", flags);
        var p1 = new ZPackage((byte[])wrap.Invoke(null, new object[] { first, "shutdown-token" }));
        var p2 = new ZPackage((byte[])wrap.Invoke(null, new object[] { second, "" }));
        long r1 = p1.ReadLong(), r2 = p2.ReadLong();
        Check(r2 > r1 && p1.ReadString() == "shutdown-token" && p1.ReadByteArray().SequenceEqual(first), "capture revisions and shutdown token survive the envelope");
        Check(p2.ReadString() == "", "ordinary update has no shutdown confirmation token");
        var serverReceiverType = typeof(ServerSide).GetNestedType("PatchZNetOnNewConnection", BindingFlags.NonPublic);
        long receivedRevision = 0; byte[] receivedInventory = null;
        Action<ZRpc, byte[], long, string> receiveInventory = (rpc, bytes, revision, request) => { receivedRevision = revision; receivedInventory = bytes; };
        var serverRawReceiver = (Action<ZRpc, ZPackage>)serverReceiverType.GetMethod("ReceiveUpdate", flags).Invoke(null, new object[] { receiveInventory, false });
        byte[] rawUpdate = (byte[])wrap.Invoke(null, new object[] { first, "" });
        serverRawReceiver(a, Part(rawUpdate, 0, 48)); serverRawReceiver(a, Part(rawUpdate, 1, 48));
        Check(receivedRevision > r2 && receivedInventory.SequenceEqual(first), "server decodes raw inventory revision envelope correctly");
        byte[] baseline = SHA512.Create().ComputeHash(second), key = new byte[32];
        typeof(ClientSide).GetField("recoveryBaseline", flags).SetValue(null, baseline);
        typeof(ClientSide).GetField("serverEncryptionTime", flags).SetValue(null, 123L);
        byte[] signature = (byte[])typeof(ClientSide).GetMethod("generateProfileSignature", flags).Invoke(null, new object[] { first, key });
        var signaturePackage = new ZPackage(signature);
        byte[] ciphertext = signaturePackage.ReadByteArray(), iv = signaturePackage.ReadByteArray();
        Check(signaturePackage.ReadLong() == 123L && signaturePackage.ReadByteArray().SequenceEqual(baseline), "recovery signature carries the confirmed baseline");
        var content = new ZPackage(); content.Write(first); content.Write(baseline);
        using (var aes = Aes.Create()) {
            aes.Key = key; aes.IV = iv;
            byte[] actual = aes.CreateDecryptor().TransformFinalBlock(ciphertext, 0, ciphertext.Length);
            Check(actual.SequenceEqual(SHA512.Create().ComputeHash(content.GetArray())), "signature authenticates both profile and baseline");
        }
        typeof(ServerSide).GetField("shutdownSaveInProgress", flags).SetValue(null, true);
        typeof(ServerSide).GetField("shutdownRequest", flags).SetValue(null, "current-request");
        var pending = (HashSet<long>)typeof(ServerSide).GetField("shutdownSavePending", flags).GetValue(null);
        pending.Add(77);
        var mark = typeof(ServerSide).GetMethod("MarkShutdownProfileSaved", flags);
        mark.Invoke(null, new object[] { a, "" });
        mark.Invoke(null, new object[] { a, "previous-request" });
        Check(pending.Contains(77), "ordinary or previous saves cannot acknowledge the current shutdown");
        System.Console.WriteLine(checks + " protocol checks passed. Native Unity integration is not exercised.");
    }
}
'@
    [TransferProtocolChecks]::Run()
} finally {
    [AppDomain]::CurrentDomain.remove_AssemblyResolve($resolver)
}
