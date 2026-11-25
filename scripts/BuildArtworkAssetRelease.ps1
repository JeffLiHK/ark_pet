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
    [string] $AppIcon = (Join-Path $PSScriptRoot '../src/ArtPet.Windows/Media/Sprout.ico'),
    [string] $OutputDirectory = (Join-Path $PSScriptRoot "../artifacts/assets-v$AssetVersion")
)

$ErrorActionPreference = 'Stop'
if ([version]$MinAppVersion -lt [version]'1.3.0') {
    throw 'The full artwork package requires ArtPet 1.3.0 or newer.'
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

$outputParent = [IO.Path]::GetDirectoryName($output)
$stagingName = '.artpet-artwork-' + [guid]::NewGuid().ToString('N')
$staging = Join-Path $outputParent $stagingName
$result = Join-Path $staging 'release'
[void][IO.Directory]::CreateDirectory($result)
$archivePath = Join-Path $result 'ArtPet-assets.zip'
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
            $sources[$id] = Join-Path $CharacterDirectory $id
        }
    }

    if (-not $sources.Contains('sprout')) {
        $sproutDirectory = Join-Path $staging 'sprout'
        & dotnet run --file (Join-Path $PSScriptRoot 'ExportSproutArtwork.cs') -- $sproutDirectory
        if ($LASTEXITCODE -ne 0) {
            throw 'Unable to export the bundled Sprout artwork.'
        }
        $sources['sprout'] = $sproutDirectory
    }

    $legacyPath = $null
    if (-not $sources.Contains('hoolheyak')) {
        $legacyOutput = Join-Path $staging 'hoolheyak'
        & (Join-Path $PSScriptRoot 'BuildHoolheyakAssetRelease.ps1') -AssetVersion $AssetVersion `
            -MinAppVersion $MinAppVersion -OutputDirectory $legacyOutput | Out-Null
        $legacyPath = Join-Path $legacyOutput 'ArtPet-hoolheyak-assets.zip'
    }

    $archive = [IO.Compression.ZipFile]::Open($archivePath, [IO.Compression.ZipArchiveMode]::Create)
    try {
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
        foreach ($source in $sources.GetEnumerator() | Sort-Object @{ Expression = { $_.Key -ne 'hoolheyak' } },
            @{ Expression = { $_.Key -eq 'sprout' } }, Key) {
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

        if ($characters.Count -gt 64) {
            throw 'The artwork package exceeds the 64-character limit.'
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
    }

    $archive = [IO.Compression.ZipFile]::OpenRead($archivePath)
    try {
        $expanded = ($archive.Entries | Measure-Object -Property Length -Sum).Sum
        if ($archive.Entries.Count -gt 20000 -or $expanded -gt 1GB -or (Get-Item -LiteralPath $archivePath).Length -gt 512MB) {
            throw 'The full artwork package exceeds the updater file count or size limit.'
        }
    } finally {
        $archive.Dispose()
    }

    [ordered]@{
        packId = 'artpet'
        assetVersion = $AssetVersion
        schemaVersion = 3
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

Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $output 'ArtPet-assets.zip'), (Join-Path $output 'assets-manifest.json')
