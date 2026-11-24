#Requires -Version 7.0
[CmdletBinding()]
param(
    [string] $Destination = (Join-Path $PSScriptRoot '../local-data/voices/霍爾海雅/ja')
)

$ErrorActionPreference = 'Stop'
$characterId = 'char_4027_heyak'
$audioArchive = 'https://raw.githubusercontent.com/PseudoMon/arknights-audio/d6de8b50e23bdfa5cf9d391f4ccb3798d7baf4db'
$japaneseTable = 'https://raw.githubusercontent.com/Kengxxiao/ArknightsGameData_YoStar/3efb96637b9c344d282118bc91f6f8101d7712d5/ja_JP/gamedata/excel/charword_table.json'
$traditionalChineseTable = 'https://raw.githubusercontent.com/ArknightsAssets/ArknightsGamedata/3fbae6508ce8b5c562f8c00d931a99cc0e647f26/tw/gamedata/excel/charword_table.json'

function Get-Subtitles([string] $TableUrl) {
    $table = Invoke-RestMethod -Uri $TableUrl -TimeoutSec 30 -MaximumRetryCount 2
    $entries = $table.charWords.PSObject.Properties |
        Where-Object Name -Like ($characterId + '_CN_*') |
        ForEach-Object Value |
        Sort-Object voiceId

    $subtitles = [ordered]@{}
    foreach ($entry in $entries) {
        if ($entry.voiceId -cmatch '^CN_[0-9]{3}$' -and
            $entry.voiceAsset -ceq "$characterId/$($entry.voiceId)" -and
            ![string]::IsNullOrWhiteSpace($entry.voiceText)) {
            $subtitles[$entry.voiceId.ToLowerInvariant()] = $entry.voiceText.Trim()
        }
    }

    if ($subtitles.Count -eq 0) {
        throw "No matching subtitles were found in $TableUrl."
    }

    return $subtitles
}

function Export-Subtitles([string] $Directory, [System.Collections.IDictionary] $Lines) {
    $null = New-Item -ItemType Directory -Force -Path $Directory
    $manifest = Join-Path $Directory 'subtitles.json'
    $temp = "$manifest.$([guid]::NewGuid().ToString('N')).tmp"
    try {
        $Lines | ConvertTo-Json | Set-Content -LiteralPath $temp -Encoding utf8
        Move-Item -LiteralPath $temp -Destination $manifest -Force
    }
    finally {
        if (Test-Path -LiteralPath $temp) {
            Remove-Item -LiteralPath $temp
        }
    }

    Write-Output "Imported $($Lines.Count) subtitles to $Directory."
}

function Import-Audio([string] $Directory, [string] $VoiceFolder, [string[]] $Ids) {
    $null = New-Item -ItemType Directory -Force -Path $Directory
    $audioBase = "$audioArchive/$VoiceFolder/$characterId"
    $available = 0
    foreach ($id in $Ids) {
        $file = Join-Path $Directory "$id.mp3"
        if ((Test-Path -LiteralPath $file -PathType Leaf) -and (Get-Item -LiteralPath $file).Length -gt 0) {
            $available++
            continue
        }

        $temp = "$file.$([guid]::NewGuid().ToString('N')).tmp"
        try {
            $assetId = $id.ToUpperInvariant()
            $response = Invoke-WebRequest -Uri "$audioBase/$assetId.mp3" -OutFile $temp -PassThru -TimeoutSec 30 -MaximumRetryCount 2 -RetryIntervalSec 1
            if ($response.Headers['Content-Type'][0] -notlike 'audio/*' -or $response.RawContentLength -le 0) {
                throw "The voice archive did not return audio for $id."
            }

            Move-Item -LiteralPath $temp -Destination $file -Force
            $available++
        }
        catch {
            if ($_.Exception.Response.StatusCode -ne [System.Net.HttpStatusCode]::NotFound) {
                throw
            }

            Write-Warning "$VoiceFolder audio is unavailable for $id."
        }
        finally {
            if (Test-Path -LiteralPath $temp) {
                Remove-Item -LiteralPath $temp
            }
        }
    }

    if ($available -eq 0) {
        throw "No matching audio was imported from $audioBase."
    }

    Write-Output "Imported $available voice lines to $Directory."
}

$japaneseDirectory = [System.IO.Path]::GetFullPath($Destination)
$root = Split-Path -Parent $japaneseDirectory
$japanese = Get-Subtitles $japaneseTable
$traditionalChinese = Get-Subtitles $traditionalChineseTable
$ids = @($japanese.Keys; $traditionalChinese.Keys) | Sort-Object -Unique

Export-Subtitles $japaneseDirectory $japanese
Export-Subtitles (Join-Path $root 'zh-TW') $traditionalChinese
Import-Audio $japaneseDirectory 'voice' $ids
Import-Audio (Join-Path $root 'zh-CN') 'voice_cn' $ids
