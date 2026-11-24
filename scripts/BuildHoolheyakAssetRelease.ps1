#Requires -Version 7
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$')]
    [string] $AssetVersion,

    [Parameter(Mandatory)]
    [ValidatePattern('^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$')]
    [string] $MinAppVersion,

    [string] $PackDirectory = (Join-Path $PSScriptRoot '../local-data/assets/霍爾海雅'),

    [string] $OutputDirectory = (Join-Path $PSScriptRoot "../artifacts/assets-v$AssetVersion")
)

$ErrorActionPreference = 'Stop'
$assetVersionNumber = [version]::Parse($AssetVersion)
$minimumAppVersion = [version]::Parse($MinAppVersion)
$packPath = (Resolve-Path -LiteralPath $PackDirectory).ProviderPath
$outputPath = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDirectory)
if (Test-Path -LiteralPath $outputPath) {
    throw "The output directory must be new. Choose a different path from $outputPath."
}

& dotnet run --file (Join-Path $PSScriptRoot 'ValidateHoolheyakPack.cs') -- $packPath
if ($LASTEXITCODE -ne 0) {
    throw 'The Hoolheyak pack failed validation.'
}

$manifestPath = Join-Path $packPath 'character.json'
$definition = Get-Content -LiteralPath $manifestPath -Raw -Encoding utf8 | ConvertFrom-Json
Push-Location -LiteralPath $packPath
try {
    $imagePaths = @(rg --files --hidden --no-ignore --iglob '*.png' --iglob '*.jpg' --iglob '*.jpeg')
    if ($LASTEXITCODE -ne 0) {
        throw 'Unable to list the Hoolheyak images.'
    }
}
finally {
    Pop-Location
}

$outputParent = [IO.Path]::GetDirectoryName($outputPath)
$stagePath = Join-Path $outputParent ('.artpet-assets-' + [guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($stagePath)
$archivePath = Join-Path $stagePath 'ArtPet-hoolheyak-assets.zip'
$metadataPath = Join-Path $stagePath 'assets-manifest.json'
try {
    $archive = [IO.Compression.ZipFile]::Open($archivePath, [IO.Compression.ZipArchiveMode]::Create)
    try {
        $remaining = 1GB
        $paths = @('character.json') + $imagePaths
        if ($paths.Count -gt 20000) {
            throw 'The asset archive exceeds the updater file count limit.'
        }

        foreach ($relativePath in $paths) {
            $source = Get-Item -LiteralPath (Join-Path $packPath $relativePath)
            $remaining -= $source.Length
            if ($remaining -lt 0) {
                throw 'The extracted asset archive exceeds the updater size limit.'
            }

            [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
                $archive, $source.FullName, $relativePath.Replace('\', '/'), [IO.Compression.CompressionLevel]::Fastest)
        }
    }
    finally {
        $archive.Dispose()
    }

    if ((Get-Item -LiteralPath $archivePath).Length -gt 512MB) {
        throw 'The compressed asset archive exceeds the updater size limit.'
    }

    [ordered]@{
        packId = $definition.id
        assetVersion = $assetVersionNumber.ToString(3)
        schemaVersion = $definition.schemaVersion
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
