param(
    [Parameter(Mandatory)][string]$Iscc,
    [Parameter(Mandatory)][string]$EvidenceDirectory
)
$ErrorActionPreference = 'Stop'
$source = Split-Path $PSScriptRoot -Parent
$evidence = [IO.Path]::GetFullPath($EvidenceDirectory)
if (Test-Path $evidence) { throw 'EvidenceDirectory must be a new directory' }
New-Item -ItemType Directory -Path $evidence | Out-Null
& $Iscc /Qp "/O$evidence" "$source\tests\installer\rollback-fixture.iss"
if ($LASTEXITCODE) { throw 'Inno compilation failed' }
$target = Join-Path $evidence 'installation'
New-Item -ItemType Directory -Path "$target\web", "$target\themes\old-empty", "$target\data" | Out-Null
'old early bytes' | Set-Content "$target\early.txt"
'old locked bytes' | Set-Content "$target\locked.txt"
'old page' | Set-Content "$target\web\old.html"
'old theme' | Set-Content "$target\themes\stale"
'private fixture data' | Set-Content "$target\data\mine.txt"
function Get-Manifest {
    @(Get-ChildItem -LiteralPath $target -Recurse -File | Sort-Object FullName | ForEach-Object {
        [ordered]@{ Path = $_.FullName.Substring($target.Length); Hash = (Get-FileHash -LiteralPath $_.FullName).Hash }
    }) | ConvertTo-Json -Compress
}
function Invoke-Fixture([string]$Name, [string]$Extra = '') {
    $silent = if ($Name -eq 'cancel') { '/SILENT' } else { '/VERYSILENT' }
    $process = Start-Process "$evidence\rollback-fixture.exe" -ArgumentList "$silent /SUPPRESSMSGBOXES /NORESTART /SP- /DIR=`"$target`" /LOG=`"$evidence\$Name.log`" $Extra" -PassThru -Wait
    return $process.ExitCode
}
$before = Get-Manifest
$before | Set-Content "$evidence\before.json"
$unreadable = [IO.File]::Open("$target\early.txt", [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::None)
try { $backupFailure = Invoke-Fixture 'backup-failure' } finally { $unreadable.Dispose() }
Get-Manifest | Set-Content "$evidence\after-backup-failure.json"
if ($backupFailure -eq 0 -or $before -ne (Get-Manifest)) { throw 'Backup failure changed the original installation' }
$lock = [IO.File]::Open("$target\locked.txt", [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
try { $refusal = Invoke-Fixture 'refusal' } finally { $lock.Dispose() }
$after = Get-Manifest
$after | Set-Content "$evidence\after-refusal.json"
if ($refusal -eq 0 -or $before -ne $after) { throw 'Busy-file refusal did not preserve every original file' }
if (-not (Test-Path "$target\themes\old-empty")) { throw 'Original empty directory was lost' }
if ((Get-Content "$evidence\refusal.log" -Raw) -notlike '*Previous installation files restored and SHA256 verified*') { throw 'Restore did not complete' }
$cancel = Invoke-Fixture 'cancel' '/CancelAfterEarly=1'
Get-Manifest | Set-Content "$evidence\after-cancel.json"
if ($cancel -eq 0 -or $before -ne (Get-Manifest)) { throw 'Cancellation after an overwrite did not preserve the original manifest' }
$success = Invoke-Fixture 'success'
if ($success -ne 0) { throw "Success installation failed: $success" }
if ((Get-Content "$target\early.txt" -Raw).Trim() -ne 'new early bytes') { throw 'Payload was not upgraded' }
if ((Test-Path "$target\web\old.html") -or (Test-Path "$target\themes\stale")) { throw 'Stale payload was not removed' }
if ((Get-Content "$target\data\mine.txt" -Raw).Trim() -ne 'private fixture data') { throw 'Unmanaged data changed' }
Get-Manifest | Set-Content "$evidence\after-success.json"
[ordered]@{ BackupFailureExit = $backupFailure; RefusalExit = $refusal; CancelExit = $cancel; OriginalManifestPreserved = $true; SuccessExit = $success; StaleCleanup = $true; UnmanagedDataPreserved = $true } | ConvertTo-Json | Set-Content "$evidence\result.json"
Get-Content "$evidence\result.json"
