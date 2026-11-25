#Requires -Version 7
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$')]
    [string] $AssetVersion,

    [Parameter(Mandatory)]
    [ValidatePattern('^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$')]
    [string] $MinAppVersion,

    [string] $ModelDirectory = (Join-Path $PSScriptRoot '../Ark-Models/models'),
    [string] $PortraitDirectory = (Join-Path $PSScriptRoot '../ArknightsGameResource/portrait'),

    [string] $OutputDirectory = (Join-Path $PSScriptRoot "../artifacts/assets-v$AssetVersion")
)

$ErrorActionPreference = 'Stop'
$assetVersionNumber = [version]::Parse($AssetVersion)
$minimumAppVersion = [version]::Parse($MinAppVersion)
$outputPath = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDirectory)
if (Test-Path -LiteralPath $outputPath) {
    throw "The output directory must be new. Choose a different path from $outputPath."
}

if ($minimumAppVersion -lt [version]'1.3.0') {
    throw 'The Spine package requires ArkPet 1.3.0 or newer.'
}

$source = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'HoolheyakSource.json')) | ConvertFrom-Json
$files = [ordered]@{}
$outfits = foreach ($outfit in $source.outfits) {
    $modelId = $outfit.id
    if ($outfit.sharedModel) {
        $modelId = $outfit.sharedModel
    } else {
        $model = Join-Path $ModelDirectory $outfit.model.ToLowerInvariant() "build_char_$($outfit.model)"
        $atlas = "$model.atlas"
        $atlasText = [IO.File]::ReadAllText($atlas).Trim()
        $texture = ($atlasText -split '\r?\n', 2)[0]
        if ([string]::IsNullOrEmpty($texture) -or $atlasText -match '\n[ \t\r]*\n' -or
            [IO.Path]::GetFileName($texture) -cne $texture -or
            $texture.IndexOfAny([IO.Path]::GetInvalidFileNameChars()) -ge 0 -or
            -not $texture.EndsWith('.png', [StringComparison]::OrdinalIgnoreCase)) {
            throw "Expected a single atlas page with a local PNG texture: $atlas"
        }
        $files["models/$modelId/model.skel"] = "$model.skel"
        $files["models/$modelId/model.atlas"] = $atlas
        $files["models/$modelId/$texture"] = Join-Path (Split-Path -Parent $model) $texture
    }
    $files["portraits/$($outfit.id).png"] = Join-Path $PortraitDirectory $outfit.portrait
    [ordered]@{
        id = $outfit.id
        displayName = $outfit.name
        modelId = $modelId
    }
}

$schemaVersion = 2
$definition = [ordered]@{
    schemaVersion = $schemaVersion
    id = $source.id
    displayName = $source.displayName
    defaultOutfitId = $source.defaultOutfitId
    outfits = @($outfits)
}

$outputParent = [IO.Path]::GetDirectoryName($outputPath)
$stagePath = Join-Path $outputParent ('.artpet-assets-' + [guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($stagePath)
$archivePath = Join-Path $stagePath 'ArtPet-hoolheyak-assets.zip'
$metadataPath = Join-Path $stagePath 'assets-manifest.json'
try {
    $archive = [IO.Compression.ZipFile]::Open($archivePath, [IO.Compression.ZipArchiveMode]::Create)
    try {
        $manifestEntry = $archive.CreateEntry('character.json')
        $writer = [IO.StreamWriter]::new($manifestEntry.Open())
        try {
            $writer.Write(($definition | ConvertTo-Json -Depth 4))
        } finally {
            $writer.Dispose()
        }

        foreach ($file in $files.GetEnumerator()) {
            $inputFile = Get-Item -LiteralPath $file.Value
            if ($inputFile -isnot [IO.FileInfo] -or $inputFile.Length -eq 0 -or $inputFile.Length -gt 8MB) {
                throw "Invalid Spine asset: $($file.Value)"
            }
            [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
                $archive, $inputFile.FullName, $file.Key, [IO.Compression.CompressionLevel]::Optimal)
        }
    }
    finally {
        $archive.Dispose()
    }

    if ((Get-Item -LiteralPath $archivePath).Length -gt 512MB) {
        throw 'The compressed asset archive exceeds the updater size limit.'
    }

    [ordered]@{
        packId = $source.id
        assetVersion = $assetVersionNumber.ToString(3)
        schemaVersion = $schemaVersion
        minAppVersion = $minimumAppVersion.ToString(3)
    } | ConvertTo-Json | Set-Content -LiteralPath $metadataPath -Encoding utf8NoBOM

    [IO.Directory]::Move($stagePath, $outputPath)
}
catch {
    [IO.File]::Delete($archivePath)
    [IO.File]::Delete($metadataPath)
    [IO.Directory]::Delete($stagePath)
    throw
}

Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $outputPath 'ArtPet-hoolheyak-assets.zip'), (Join-Path $outputPath 'assets-manifest.json')
