#Requires -Version 7.0
[CmdletBinding()]
param(
    [string] $Destination = (Join-Path $PSScriptRoot '../local-data/voices/霍爾海雅/ja')
)

$ErrorActionPreference = 'Stop'
$characterId = 'char_4027_heyak'
$audioBase = "https://torappu.prts.wiki/assets/audio/voice/$characterId"
# This Japanese game table uses the same voice IDs and dialogue as the PRTS voice record.
$tableUrl = 'https://raw.githubusercontent.com/Kengxxiao/ArknightsGameData_YoStar/3efb96637b9c344d282118bc91f6f8101d7712d5/ja_JP/gamedata/excel/charword_table.json'
$table = Invoke-RestMethod -Uri $tableUrl -TimeoutSec 30 -MaximumRetryCount 2
$entries = $table.charWords.PSObject.Properties |
    Where-Object Name -Like "${characterId}_CN_*" |
    ForEach-Object Value |
    Sort-Object voiceId

$null = New-Item -ItemType Directory -Force -Path $Destination
$subtitles = [ordered]@{}
foreach ($entry in $entries) {
    if ($entry.voiceId -cnotmatch '^CN_[0-9]{3}$' -or
        $entry.voiceAsset -cne "$characterId/$($entry.voiceId)" -or
        [string]::IsNullOrWhiteSpace($entry.voiceText)) {
        continue
    }

    $id = $entry.voiceId.ToLowerInvariant()
    $file = Join-Path $Destination "$id.mp3"
    if (!(Test-Path -LiteralPath $file -PathType Leaf) -or (Get-Item -LiteralPath $file).Length -eq 0) {
        $temp = "$file.$([guid]::NewGuid().ToString('N')).tmp"
        try {
            $response = Invoke-WebRequest -Uri "$audioBase/$id.mp3" -OutFile $temp -PassThru -TimeoutSec 30
            if ($response.Headers['Content-Type'][0] -notlike 'audio/*' -or $response.RawContentLength -le 0) {
                throw "PRTS did not return audio for $id."
            }

            Move-Item -LiteralPath $temp -Destination $file -Force
        }
        catch {
            if ($_.Exception.Response.StatusCode -ne [System.Net.HttpStatusCode]::NotFound) {
                throw
            }

            Write-Warning "PRTS audio is unavailable for $id."
            continue
        }
        finally {
            if (Test-Path -LiteralPath $temp) {
                Remove-Item -LiteralPath $temp
            }
        }
    }

    $subtitles[$id] = $entry.voiceText.Trim()
}

if ($subtitles.Count -eq 0) {
    throw 'No matching Japanese voice files were imported.'
}

$manifest = Join-Path $Destination 'subtitles.json'
$temp = "$manifest.$([guid]::NewGuid().ToString('N')).tmp"
try {
    $subtitles | ConvertTo-Json | Set-Content -LiteralPath $temp -Encoding utf8
    Move-Item -LiteralPath $temp -Destination $manifest -Force
}
finally {
    if (Test-Path -LiteralPath $temp) {
        Remove-Item -LiteralPath $temp
    }
}

Write-Output "Imported $($subtitles.Count) Japanese voice lines to $([System.IO.Path]::GetFullPath($Destination))."
