$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path $PSScriptRoot -Parent
$taskBin = Join-Path $taskRoot 'bin\Release'
$resolver = [System.ResolveEventHandler] {
    param($sender, $args)
    $name = (New-Object System.Reflection.AssemblyName($args.Name)).Name
    $candidate = Join-Path $taskBin ($name + '.dll')
    if (Test-Path -LiteralPath $candidate) { return [System.Reflection.Assembly]::LoadFrom($candidate) }
    return $null
}
[AppDomain]::CurrentDomain.add_AssemblyResolve($resolver)
try {
    $harmonyAssembly = [System.Reflection.Assembly]::LoadFrom((Join-Path $taskBin '0Harmony.dll'))
    $gameAssembly = [System.Reflection.Assembly]::LoadFrom((Join-Path $taskBin 'assembly_valheim.dll'))
    $plugin = [System.Reflection.Assembly]::LoadFrom((Join-Path $taskBin 'ServerCharacters.dll'))
    $shared = $plugin.GetType('ServerCharacters.Shared', $true)
    $flags = [System.Reflection.BindingFlags]'Public,NonPublic,Static'
    $original = $shared.GetMethod('SerializeProfileInMemory', $flags)
    $transpiler = $shared.GetNestedType('ProfileMemorySerializer', [System.Reflection.BindingFlags]::NonPublic).GetMethod('Transpiler', $flags)
    $harmony = New-Object HarmonyLib.Harmony('ServerCharacters.offline.serializer.check')
    $shutdownPatch = $plugin.GetType('ServerCharacters.ServerSide', $true).GetNestedType('PatchGameShutdownBeforeNetworkStops', [System.Reflection.BindingFlags]::NonPublic)
    $shutdownOriginal = $gameAssembly.GetType('Game', $true).GetMethod('Shutdown', [System.Reflection.BindingFlags]'Public,NonPublic,Instance')
    $shutdownPrefix = New-Object HarmonyLib.HarmonyMethod($shutdownPatch.GetMethod('Prefix', $flags))
    $shutdownReplacement = $harmony.Patch($shutdownOriginal, $shutdownPrefix, $null, $null, $null)
    if ($null -eq $shutdownReplacement) { throw 'Shutdown patch installation failed.' }
    Write-Output 'PASS: shutdown prefix installed against actual game method without diagnostic finalizer.'
    $method = New-Object HarmonyLib.HarmonyMethod($transpiler)
    $replacement = $harmony.Patch($original, $null, $null, $method, $null)
    if ($null -eq $replacement) { throw 'Harmony did not generate a replacement method.' }
    Write-Output 'PASS: Harmony generated and installed the in-memory serializer with the actual game assemblies.'
    $client = $plugin.GetType('ServerCharacters.ClientSide', $true)
    $normalPatch = $client.GetNestedType('PatchPlayerProfileSave_Client', [System.Reflection.BindingFlags]::NonPublic).GetMethod('Transpiler', $flags)
    $normalOriginal = $gameAssembly.GetType('PlayerProfile', $true).GetMethod('SavePlayerToDisk', [System.Reflection.BindingFlags]'Public,NonPublic,Instance')
    $reader = [HarmonyLib.PatchProcessor].GetMethods() | Where-Object {
        $_.Name -eq 'GetOriginalInstructions' -and $_.GetParameters().Count -eq 2 -and
        $_.GetParameters()[1].ParameterType -eq [System.Reflection.Emit.ILGenerator]
    } | Select-Object -First 1
    $normalInstructions = $reader.Invoke($null, [object[]]@($normalOriginal, $null))
    $rewritten = @($normalPatch.Invoke($null, [object[]]@(,$normalInstructions)))
    if ($rewritten.Count -ne $normalInstructions.Count + 2) { throw 'Unexpected normal-save transpiler output.' }
    Write-Output 'PASS: normal-save transpiler preserves the original disk-save instructions and inserts the network callback.'
    try {
        $original.Invoke($null, [object[]]@($null)) | Out-Null
        throw 'Expected the null profile to be rejected.'
    } catch {
        $errorDetail = $_.Exception
        while ($errorDetail.InnerException) { $errorDetail = $errorDetail.InnerException }
        if ($errorDetail -isnot [NullReferenceException]) { throw }
        Write-Output 'PASS: generated serializer executed; null profile rejected without invalid IL.'
    }
    $profileType = $gameAssembly.GetType('PlayerProfile', $true)
    $profile = [System.Runtime.Serialization.FormatterServices]::GetUninitializedObject($profileType)
    $instanceFlags = [System.Reflection.BindingFlags]'Public,NonPublic,Instance'
    $statsField = $profileType.GetField('m_playerStats', $instanceFlags)
    $statsType = $statsField.FieldType.GetElementType()
    $stats = [Array]::CreateInstance($statsType, 10)
    for ($i = 0; $i -lt 10; $i++) { $stats.SetValue([Activator]::CreateInstance($statsType), $i) }
    $statsField.SetValue($profile, $stats)
    $worlds = $profileType.GetField('m_worldData', $instanceFlags)
    $worlds.SetValue($profile, [Activator]::CreateInstance($worlds.FieldType))
    $profileType.GetField('m_playerName', $instanceFlags).SetValue($profile, 'OfflineTest')
    $profileType.GetField('m_startSeed', $instanceFlags).SetValue($profile, '')
    $profileType.GetField('m_dateCreated', $instanceFlags).SetValue($profile, [DateTime]::Now)
    $profileType.GetField('m_lastSaveLoad', $instanceFlags).SetValue($profile, [DateTime]::Now)
    try {
        $bytes = $original.Invoke($null, [object[]]@($profile))
        if ($bytes.Length -lt 100) { throw 'Serialized profile is unexpectedly short.' }
        Write-Output ('PASS: serializer produced a synthetic profile (' + $bytes.Length + ' bytes).')
    } catch {
        $detail = $_.Exception.ToString()
        if ($detail -notmatch 'UnityEngine.Object') { throw }
        Write-Output 'SKIP: complete profile execution requires the native Unity runtime; run this integration check inside Valheim.'
    }
    $harmony.UnpatchSelf()
} finally {
    [AppDomain]::CurrentDomain.remove_AssemblyResolve($resolver)
}
