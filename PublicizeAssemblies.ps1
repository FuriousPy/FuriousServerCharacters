param(
    [Parameter(Mandatory = $true)]
    [string] $GamePath,

    [Parameter(Mandatory = $true)]
    [string] $OutputPath
)

$ErrorActionPreference = "Stop"

$managedPath = Join-Path $GamePath "valheim_Data\Managed"
$bepInExCorePath = Join-Path $GamePath "BepInEx\core"
$cecilPath = Join-Path $bepInExCorePath "Mono.Cecil.dll"

if (-not (Test-Path -LiteralPath $cecilPath)) {
    throw "Mono.Cecil.dll was not found in the BepInEx core folder: $cecilPath"
}

New-Item -ItemType Directory -Force -Path $OutputPath | Out-Null
$localCecilPath = Join-Path $OutputPath "Mono.Cecil.dll"
Copy-Item -LiteralPath $cecilPath -Destination $localCecilPath -Force
Unblock-File -LiteralPath $localCecilPath
Add-Type -Path $localCecilPath

$resolver = New-Object Mono.Cecil.DefaultAssemblyResolver
$resolver.AddSearchDirectory($managedPath)
$resolver.AddSearchDirectory($bepInExCorePath)

$readerParameters = New-Object Mono.Cecil.ReaderParameters
$readerParameters.AssemblyResolver = $resolver

$assemblyNames = @(
    "assembly_guiutils.dll",
    "assembly_utils.dll",
    "assembly_valheim.dll",
    "com.rlabrecque.steamworks.net.dll",
    "SoftReferenceableAssets.dll"
)

foreach ($assemblyName in $assemblyNames) {
    $sourcePath = Join-Path $managedPath $assemblyName
    $targetPath = Join-Path $OutputPath $assemblyName

    if (-not (Test-Path -LiteralPath $sourcePath)) {
        throw "Valheim assembly was not found: $sourcePath"
    }

    if ((Test-Path -LiteralPath $targetPath) -and
        (Get-Item -LiteralPath $targetPath).LastWriteTimeUtc -ge (Get-Item -LiteralPath $sourcePath).LastWriteTimeUtc) {
        continue
    }

    $assembly = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($sourcePath, $readerParameters)
    try {
        $types = New-Object 'System.Collections.Generic.Queue[Mono.Cecil.TypeDefinition]'
        foreach ($type in $assembly.MainModule.Types) {
            $types.Enqueue($type)
        }

        while ($types.Count -gt 0) {
            $type = $types.Dequeue()
            if ($type.IsNested) {
                $type.IsNestedPublic = $true
            } else {
                $type.IsPublic = $true
            }

            foreach ($field in $type.Fields) {
                $field.IsPublic = $true
            }
            foreach ($method in $type.Methods) {
                $method.IsPublic = $true
            }
            foreach ($nestedType in $type.NestedTypes) {
                $types.Enqueue($nestedType)
            }
        }

        $assembly.Write($targetPath)
        (Get-Item -LiteralPath $targetPath).LastWriteTimeUtc = (Get-Item -LiteralPath $sourcePath).LastWriteTimeUtc
    } finally {
        $assembly.Dispose()
    }
}
