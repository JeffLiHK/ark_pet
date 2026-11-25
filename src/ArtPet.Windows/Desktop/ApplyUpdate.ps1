$ErrorActionPreference = 'Stop'
$target = [IO.DirectoryInfo]::new($env:ARTPET_UPDATE_TARGET)
$stage = [IO.DirectoryInfo]::new($env:ARTPET_UPDATE_STAGE)
if ($stage.Parent.FullName.TrimEnd('\') -ne $target.FullName.TrimEnd('\') -or
    $stage.Name -notmatch '^\.artpet-update-[0-9a-f]{32}$' -or
    ($stage.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
    exit 1
}

$executable = [IO.Path]::Combine($target.FullName, $env:ARTPET_UPDATE_EXECUTABLE_NAME)
$payload = [IO.Path]::Combine($stage.FullName, 'payload')
$backup = [IO.Path]::Combine($stage.FullName, 'backup')
$journal = [Collections.Generic.List[object]]::new()
$restart = $false
$keepBackup = $false
$failure = $null

try {
    $parent = Get-Process -Id ([int]$env:ARTPET_UPDATE_PROCESS) -ErrorAction SilentlyContinue
    if ($null -ne $parent -and -not $parent.WaitForExit(30000)) {
        throw 'Art Pet 未能在更新時結束。'
    }
    $restart = $true

    foreach ($source in [IO.Directory]::EnumerateFiles($payload, '*', [IO.SearchOption]::AllDirectories)) {
        $relative = $source.Substring($payload.Length + 1)
        $destination = [IO.Path]::Combine($target.FullName, $relative)
        $saved = [IO.Path]::Combine($backup, $relative)

        # Walk the destination before touching it so a linked file or folder cannot redirect writes.
        for ($path = $destination; $path.TrimEnd('\') -ne $target.FullName.TrimEnd('\'); $path = [IO.Path]::GetDirectoryName($path)) {
            if (([IO.File]::Exists($path) -or [IO.Directory]::Exists($path)) -and
                ([IO.File]::GetAttributes($path) -band [IO.FileAttributes]::ReparsePoint)) {
                throw "更新路徑包含連結：$relative"
            }
        }

        [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination))
        $replacing = [IO.File]::Exists($destination)
        if ($replacing) {
            [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($saved))
            [IO.File]::Replace($source, $destination, $saved)
        } else {
            [IO.File]::Move($source, $destination)
        }
        $journal.Add(@{ Destination = $destination; Backup = $saved; Replaced = $replacing })
    }

    Start-Process -FilePath $executable -WorkingDirectory $target.FullName
} catch {
    $failure = $_.Exception.Message
    for ($i = $journal.Count - 1; $i -ge 0; $i--) {
        $entry = $journal[$i]
        try {
            if ($entry.Replaced) {
                [IO.File]::Replace($entry.Backup, $entry.Destination, [NullString]::Value)
            } else {
                [IO.File]::Delete($entry.Destination)
            }
        } catch {
            $keepBackup = $true
        }
    }

    if ($keepBackup) {
        $failure += "`n備份位於 $backup"
    } elseif ($restart) {
        try {
            Start-Process -FilePath $env:ARTPET_UPDATE_PREVIOUS_EXECUTABLE -WorkingDirectory $target.FullName
        } catch {
            $failure += "`n請重新開啟 Art Pet。"
        }
    }
} finally {
    if (-not $keepBackup) {
        try {
            [IO.Directory]::Delete($stage.FullName, $true)
        } catch {
            # A failed cleanup must not stop the updated app.
        }
    }
}

if ($null -ne $failure) {
    Add-Type -AssemblyName System.Windows.Forms
    [void][Windows.Forms.MessageBox]::Show($failure, 'Art Pet 更新失敗')
    exit 1
}
