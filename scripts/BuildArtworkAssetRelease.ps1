#Requires -Version 7
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$')]
    [string] $AssetVersion,

    [Parameter(Mandatory)]
    [ValidatePattern('^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$')]
    [string] $MinAppVersion,

    [string] $CharacterDirectory = (Join-Path $PSScriptRoot '../local-data/release-characters'),
    [string] $ImageDirectory = (Join-Path $PSScriptRoot '../local-data/release-images'),
    [string] $VoiceDirectory = (Join-Path $PSScriptRoot '../local-data/voices'),
    [string] $AppIcon = (Join-Path $PSScriptRoot '../src/ArtPet.Windows/Media/ArtPet.ico'),
    [string] $OutputDirectory = (Join-Path $PSScriptRoot "../artifacts/assets-v$AssetVersion")
)

$ErrorActionPreference = 'Stop'
if ([version]$MinAppVersion -lt [version]'1.3.3') {
    throw 'Separate model and voice packages require ArtPet 1.3.3 or newer.'
}
$output = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDirectory)
if (Test-Path -LiteralPath $output) {
    throw 'The output directory must be new. Choose a different path from OutputDirectory.'
}

function Get-ReleaseFiles([string] $Directory, [string[]] $Globs) {
    Push-Location -LiteralPath $Directory
    try {
        $arguments = @('--files', '--hidden', '--no-ignore')
        foreach ($glob in $Globs) {
            $arguments += @('--iglob', $glob)
        }
        rg @arguments
        if ($LASTEXITCODE -gt 1) {
            throw "Unable to read artwork files from $Directory."
        }
    } finally {
        Pop-Location
    }
}

function Add-ReleaseFile([IO.Compression.ZipArchive] $Archive, [string] $File, [string] $Entry) {
    if ((Get-Item -LiteralPath $File) -isnot [IO.FileInfo]) {
        throw "Expected a file at $File."
    }
    [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
        $Archive, $File, $Entry.Replace('\', '/'), [IO.Compression.CompressionLevel]::Optimal)
}

function Add-VoiceFiles([IO.Compression.ZipArchive] $Archive, [hashtable] $VoiceArchives, [string] $Root, [string] $CharacterId) {
    $characters = @(Join-Path $Root $CharacterId)
    if ($CharacterId -ceq 'hoolheyak') {
        $characters += Join-Path $Root '霍爾海雅'
    }
    $included = @{}
    foreach ($language in @('ja', 'zh-CN', 'zh-TW')) {
        $folders = @($characters | ForEach-Object { Join-Path $_ $language } |
            Where-Object { Test-Path -LiteralPath $_ -PathType Container })
        if ($language -in @('ja', 'zh-CN')) {
            foreach ($folder in $folders) {
                $files = @(Get-ReleaseFiles $folder @('*.mp3') | Where-Object { $_ -match '^cn_[0-9]{3}\.mp3$' })
                if ($files.Count -eq 0) {
                    continue
                }
                foreach ($file in $files) {
                    $source = Join-Path $folder $file
                    $stream = [IO.File]::OpenRead($source)
                    try {
                        $first = $stream.ReadByte()
                        $second = $stream.ReadByte()
                        $third = $stream.ReadByte()
                        if ($stream.Length -lt 10 -or (-not (
                            ($first -eq 73 -and $second -eq 68 -and $third -eq 51) -or
                            ($first -eq 255 -and ($second -band 224) -eq 224)))) {
                            throw "Invalid MP3: $source"
                        }
                    } finally {
                        $stream.Dispose()
                    }
                    Add-ReleaseFile $VoiceArchives[$language] $source "voices/$CharacterId/$language/$($file.ToLowerInvariant())"
                }
                $included["audio/$language"] = $true
                break
            }
        }
        if ($language -in @('ja', 'zh-TW')) {
            $subtitle = $folders | ForEach-Object { Join-Path $_ 'subtitles.json' } |
                Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
            if ($subtitle) {
                if ((Get-Item -LiteralPath $subtitle).Length -gt 1MB) {
                    throw "Subtitle file is too large: $subtitle"
                }
                $lines = [IO.File]::ReadAllText($subtitle) | ConvertFrom-Json -AsHashtable
                if ($lines -isnot [System.Collections.IDictionary] -or $lines.Count -eq 0 -or
                    ($lines.GetEnumerator() | Where-Object { $_.Key -cnotmatch '^cn_[0-9]{3}$' -or
                        $_.Value -isnot [string] -or [string]::IsNullOrWhiteSpace($_.Value) } | Select-Object -First 1)) {
                    throw "Invalid subtitle file: $subtitle"
                }
                Add-ReleaseFile $Archive $subtitle "voices/$CharacterId/$language/subtitles.json"
                $included["subtitles/$language"] = $true
            }
        }
    }
    if ($CharacterId -ceq 'hoolheyak') {
        foreach ($required in @('audio/ja', 'audio/zh-CN', 'subtitles/ja', 'subtitles/zh-TW')) {
            if (-not $included.ContainsKey($required)) {
                throw "Hoolheyak is missing $required. Run scripts/ImportHoolheyakVoice.ps1 or supply a complete VoiceDirectory."
            }
        }
    }
}

$outputParent = [IO.Path]::GetDirectoryName($output)
$stagingName = '.artpet-artwork-' + [guid]::NewGuid().ToString('N')
$staging = Join-Path $outputParent $stagingName
$result = Join-Path $staging 'release'
[void][IO.Directory]::CreateDirectory($result)
$archivePath = Join-Path $result 'ArtPet-assets.zip'
$voicePaths = [ordered]@{
    ja = (Join-Path $result 'ArtPet-voice-ja.zip')
    'zh-CN' = (Join-Path $result 'ArtPet-voice-zh-CN.zip')
}
$manifestPath = Join-Path $result 'assets-manifest.json'
$imageGlobs = @('*.png', '*.jpg', '*.jpeg', '*.ico', '*.gif', '*.bmp', '*.tif', '*.tiff', '*.webp')
try {
    $sources = [ordered]@{}
    if (Test-Path -LiteralPath $CharacterDirectory -PathType Container) {
        $CharacterDirectory = (Resolve-Path -LiteralPath $CharacterDirectory).ProviderPath
        foreach ($entry in (Get-ReleaseFiles $CharacterDirectory @('character.json'))) {
            $id = [IO.Path]::GetDirectoryName($entry)
            if ($id -cnotmatch '\A[a-z0-9][a-z0-9-]{0,39}\z') {
                throw "Expected a character manifest in an immediate character ID folder: $entry"
            }
            if ($id -eq 'sprout') {
                continue
            }
            $sources[$id] = Join-Path $CharacterDirectory $id
        }
    }

    $legacyPath = $null
    if (-not $sources.Contains('hoolheyak')) {
        $legacyOutput = Join-Path $staging 'hoolheyak'
        & (Join-Path $PSScriptRoot 'BuildHoolheyakAssetRelease.ps1') -AssetVersion $AssetVersion `
            -MinAppVersion $MinAppVersion -OutputDirectory $legacyOutput | Out-Null
        $legacyPath = Join-Path $legacyOutput 'ArtPet-hoolheyak-assets.zip'
    }

    $archive = [IO.Compression.ZipFile]::Open($archivePath, [IO.Compression.ZipArchiveMode]::Create)
    $voiceArchives = @{}
    try {
        foreach ($language in $voicePaths.Keys) {
            $voiceArchives[$language] = [IO.Compression.ZipFile]::Open($voicePaths[$language], [IO.Compression.ZipArchiveMode]::Create)
        }
        Add-ReleaseFile $archive (Resolve-Path -LiteralPath $AppIcon).ProviderPath 'app.ico'
        $characters = [System.Collections.Generic.List[object]]::new()
        if ($legacyPath) {
            $legacy = [IO.Compression.ZipFile]::OpenRead($legacyPath)
            try {
                foreach ($entry in $legacy.Entries) {
                    $target = $archive.CreateEntry("characters/hoolheyak/$($entry.FullName)", [IO.Compression.CompressionLevel]::Optimal)
                    $inputStream = $entry.Open()
                    try {
                        $outputStream = $target.Open()
                        try { $inputStream.CopyTo($outputStream) } finally { $outputStream.Dispose() }
                    } finally {
                        $inputStream.Dispose()
                    }
                }
            } finally {
                $legacy.Dispose()
            }
            $characters.Add(@{ id = 'hoolheyak'; schemaVersion = 2 })
        }
        foreach ($source in $sources.GetEnumerator() | Sort-Object @{ Expression = { $_.Key -ne 'hoolheyak' } }, Key) {
            $id = $source.Key
            $folder = $source.Value
            $definitionPath = Join-Path $folder 'character.json'
            $definition = [IO.File]::ReadAllText($definitionPath) | ConvertFrom-Json
            if ($definition.id -cne $id -or $definition.schemaVersion -notin @(1, 2)) {
                throw "Invalid character ID or schema in $definitionPath."
            }
            $characters.Add(@{ id = $id; schemaVersion = $definition.schemaVersion })
            Add-ReleaseFile $archive $definitionPath "characters/$id/character.json"
            foreach ($file in (Get-ReleaseFiles $folder ($imageGlobs + @('*.skel', '*.atlas')))) {
                Add-ReleaseFile $archive (Join-Path $folder $file) "characters/$id/$file"
            }
        }

        if ($characters.Count -gt 1024) {
            throw 'The artwork package exceeds the 1024-character limit.'
        }
        $voiceRoot = (Resolve-Path -LiteralPath $VoiceDirectory).ProviderPath
        foreach ($character in $characters) {
            Add-VoiceFiles $archive $voiceArchives $voiceRoot $character.id
        }
        if (Test-Path -LiteralPath $ImageDirectory -PathType Container) {
            $ImageDirectory = (Resolve-Path -LiteralPath $ImageDirectory).ProviderPath
            foreach ($file in (Get-ReleaseFiles $ImageDirectory $imageGlobs)) {
                Add-ReleaseFile $archive (Join-Path $ImageDirectory $file) "images/$file"
            }
        }

        $writer = [IO.StreamWriter]::new($archive.CreateEntry('catalog.json').Open())
        try {
            $writer.Write((@{ characters = @($characters.ToArray()) } | ConvertTo-Json -Depth 4))
        } finally {
            $writer.Dispose()
        }
    } finally {
        $archive.Dispose()
        foreach ($voiceArchive in $voiceArchives.Values) {
            $voiceArchive.Dispose()
        }
    }

    foreach ($zipPath in (@($archivePath) + @($voicePaths.Values))) {
        $archive = [IO.Compression.ZipFile]::OpenRead($zipPath)
        try {
            $expanded = ($archive.Entries | Measure-Object -Property Length -Sum).Sum
            if ($archive.Entries.Count -gt 100000 -or $expanded -gt 4GB -or (Get-Item -LiteralPath $zipPath).Length -ge 2GB) {
                throw "The archive exceeds the updater file count or size limit: $zipPath"
            }
        } finally {
            $archive.Dispose()
        }
    }

    [ordered]@{
        packId = 'artpet'
        assetVersion = $AssetVersion
        schemaVersion = 5
        minAppVersion = $MinAppVersion
    } | ConvertTo-Json | Set-Content -LiteralPath $manifestPath -Encoding utf8NoBOM

    [IO.Directory]::Move($result, $output)
} finally {
    $absoluteStage = (Resolve-Path -LiteralPath $staging).ProviderPath
    if ([IO.Path]::GetDirectoryName($absoluteStage) -eq $outputParent -and
        [IO.Path]::GetFileName($absoluteStage) -eq $stagingName) {
        Remove-Item -LiteralPath $absoluteStage -Recurse -Force
    }
}

$attachments = @('ArtPet-assets.zip', 'ArtPet-voice-ja.zip', 'ArtPet-voice-zh-CN.zip', 'assets-manifest.json') |
    ForEach-Object { Join-Path $output $_ }
Get-FileHash -Algorithm SHA256 -LiteralPath $attachments
