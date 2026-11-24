#Requires -Version 7.0
<#
.SYNOPSIS
Copies the existing Hoolheyak frames and Spine files into character and outfit folders.
.DESCRIPTION
The checkout containing this script receives the new folders. Set SourceRoot to
read the old pack and Ark-Models from a different checkout. Existing files are
preserved and a completed destination is reused on repeat runs.
.EXAMPLE
pwsh -File scripts/MigrateHoolheyakAssets.ps1
.EXAMPLE
pwsh -File scripts/MigrateHoolheyakAssets.ps1 -SourceRoot C:\project\art_pet
#>
param([string] $SourceRoot = (Split-Path -Parent $PSScriptRoot))

$ErrorActionPreference = 'Stop'
$sourceRoot = (Resolve-Path -LiteralPath $SourceRoot).ProviderPath
$localData = Join-Path (Split-Path -Parent $PSScriptRoot) 'local-data'
$characterFolder = '霍爾海雅'
$legacyCharacter = Join-Path $sourceRoot 'local-data/assets/hoolheyak'
$validator = Join-Path $PSScriptRoot 'ValidateHoolheyakPack.cs'
$packDestination = Join-Path $localData "assets/$characterFolder"
$modelDestination = Join-Path $localData "models/$characterFolder"

function Test-Pack([string] $Directory) {
    dotnet run --file $validator -- $Directory
    if ($LASTEXITCODE -ne 0) {
        throw "Hoolheyak pack validation failed for $Directory"
    }
}

function Install-CharacterDirectory([string] $Destination, [scriptblock] $Populate) {
    $parent = Split-Path -Parent $Destination
    foreach ($directory in $localData, $parent) {
        $folder = [IO.Directory]::CreateDirectory($directory)
        if ($folder.Attributes.HasFlag([IO.FileAttributes]::ReparsePoint)) {
            throw "Migration requires an ordinary destination directory: $directory"
        }
    }

    $temporary = Join-Path $parent ('.hoolheyak-' + [Guid]::NewGuid().ToString('N'))
    $temporary = [IO.Directory]::CreateDirectory($temporary).FullName
    if ([IO.Path]::GetDirectoryName($temporary) -ne $parent) {
        throw "Migration staging directory is outside its destination parent: $temporary"
    }

    try {
        & $Populate $temporary
        [IO.Directory]::Move($temporary, $Destination)
    }
    finally {
        if ([IO.Directory]::Exists($temporary)) {
            Remove-Item -LiteralPath $temporary -Recurse -Force
        }
    }
}

function Resolve-PackPath([string] $Pack, [string] $RelativePath) {
    $absolute = [IO.Path]::GetFullPath($RelativePath, $Pack)
    if ([IO.Path]::IsPathRooted($RelativePath) -or
        -not $absolute.StartsWith($Pack + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Asset path leaves the old pack: $RelativePath"
    }

    return $absolute
}

$needsPack = -not (Test-Path -LiteralPath $packDestination)
if ($needsPack) {
    $pack = Join-Path $legacyCharacter 'desktop-60'
    if (-not [IO.File]::Exists((Join-Path $pack 'character.json'))) {
        $pack = Join-Path $legacyCharacter 'desktop'
    }
} else {
    $pack = $packDestination
}
Test-Pack $pack

$manifest = [IO.File]::ReadAllText((Join-Path $pack 'character.json')) | ConvertFrom-Json -AsHashtable
$skinFolders = @{}
$usedNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($outfit in $manifest.outfits) {
    $name = [string] $outfit.displayName
    if (-not $outfit.id -or $skinFolders.ContainsKey($outfit.id) -or
        [string]::IsNullOrWhiteSpace($name) -or $name -ne $name.Trim().TrimEnd('.') -or
        $name.IndexOfAny([IO.Path]::GetInvalidFileNameChars()) -ge 0 -or -not $usedNames.Add($name)) {
        throw "An outfit needs a unique ID and a name usable as a folder: $name"
    }
    $skinFolders.Add($outfit.id, $name)
}

if ($needsPack) {
    Install-CharacterDirectory $packDestination {
        param($staging)

        foreach ($outfit in $manifest.outfits) {
            $skin = $skinFolders[$outfit.id]
            $skinDirectory = [IO.Directory]::CreateDirectory((Join-Path $staging $skin)).FullName
            foreach ($clipName in 'idle', 'touch', 'move', 'sit', 'sleep') {
                $clip = $outfit[$clipName]
                if ($null -eq $clip) {
                    continue
                }

                $oldClip = Resolve-PackPath $pack $clip.directory
                $newClip = [IO.Directory]::CreateDirectory((Join-Path $skinDirectory $clipName)).FullName
                for ($index = 0; $index -lt $clip.frameCount; $index++) {
                    $file = $index.ToString('D4', [Globalization.CultureInfo]::InvariantCulture) + '.png'
                    [IO.File]::Copy((Join-Path $oldClip $file), (Join-Path $newClip $file))
                }
                $clip.directory = "$skin/$clipName"
            }

            foreach ($image in 'preview', 'portrait') {
                if ([string]::IsNullOrWhiteSpace($outfit[$image])) {
                    continue
                }

                $oldImage = Resolve-PackPath $pack $outfit[$image]
                if ([IO.File]::Exists($oldImage)) {
                    $file = $image + [IO.Path]::GetExtension($oldImage)
                    [IO.File]::Copy($oldImage, (Join-Path $skinDirectory $file))
                    $outfit[$image] = "$skin/$file"
                } else {
                    $outfit[$image] = $null
                }
            }
        }

        $manifest | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath (Join-Path $staging 'character.json') -Encoding utf8
        Test-Pack $staging
    }
}
Write-Output "Frames: $packDestination"

if (-not (Test-Path -LiteralPath $modelDestination)) {
    $modelFiles = @{
        'default' = @{
            ArkFolder = '4027_heyak'
            Basename = 'build_char_4027_heyak'
        }
        'winds-of-time' = @{
            ArkFolder = '4027_heyak_ambiencesynesthesia#4'
            Basename = 'build_char_4027_heyak_ambienceSynesthesia#4'
        }
    }
    $sources = @{}
    foreach ($id in $modelFiles.Keys) {
        $model = $modelFiles[$id]
        $files = @('atlas', 'png', 'skel' | ForEach-Object { "$($model.Basename).$_" })
        foreach ($directory in (Join-Path $legacyCharacter $id), (Join-Path $sourceRoot "Ark-Models/models/$($model.ArkFolder)")) {
            $paths = @($files | ForEach-Object { Join-Path $directory $_ })
            if (-not @($paths | Where-Object { -not [IO.File]::Exists($_) }).Count) {
                $sources[$id] = $paths
                break
            }
        }
        if (-not $sources.ContainsKey($id)) {
            Write-Warning "Model files for '$id' were not found in the old assets or Ark-Models."
            return
        }
    }

    Install-CharacterDirectory $modelDestination {
        param($staging)
        foreach ($id in $sources.Keys) {
            $skinDirectory = [IO.Directory]::CreateDirectory((Join-Path $staging $skinFolders[$id])).FullName
            Copy-Item -LiteralPath $sources[$id] -Destination $skinDirectory
        }
    }
}
Write-Output "Models: $modelDestination"
