<#
.SYNOPSIS
Builds an AgShell release: tests, the app, agshellctl and the live daemon, a portable zip and the Inno Setup installer; with -Publish also a
GitHub release, as agterm's scripts/release.sh does for its DMG.

.EXAMPLE
scripts/release.ps1 0.1.0             # build into dist\ only
scripts/release.ps1 0.1.0 -Publish -Repository MelfisDrake/AgShell    # also tag v0.1.0 on the pushed main and create the GitHub release
scripts/release.ps1 0.1.0 -NoAot      # agshellctl and the live daemon without Native AOT, for machines without the C++ build tools

The live daemon's agshell-vt.dll is built first by scripts/build-agshell-vt.ps1, which needs Zig 0.16 (AGSHELL_ZIG or zig on PATH).
#>
param(
    [Parameter(Mandatory = $true)][ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version,
    [switch]$Publish,
    [switch]$NoAot,
    [string]$Repository
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$dist = Join-Path $root 'dist'
$app = Join-Path $dist 'app'
$ctl = Join-Path $dist 'ctl'
$live = Join-Path $dist 'live'
$zip = Join-Path $dist "agshell-$Version-win-x64.zip"

function Step($text) { Write-Host "==> $text" -ForegroundColor Cyan }
function Run([string]$file, [string[]]$arguments) {
    & $file @arguments
    if ($LASTEXITCODE -ne 0) { throw "$file $($arguments -join ' ') failed with exit code $LASTEXITCODE" }
}

if ($Publish) {
    if ($Repository -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$') { throw 'publishing requires -Repository MelfisDrake/AgShell for the chosen destination' }
    if ($NoAot) { throw 'refusing to publish a release whose agshellctl is not Native AOT' }
    Step 'checking that main is clean and pushed'
    if (git -C $root status --porcelain) { throw 'the working tree has changes; commit them first' }
    Run git @('-C', $root, 'fetch', '--quiet', 'origin')
    if ((git -C $root rev-parse HEAD) -ne (git -C $root rev-parse origin/main)) { throw 'HEAD is not origin/main; push first' }
}

Remove-Item -Recurse -Force $dist -ErrorAction SilentlyContinue
Step 'building agshell-vt.dll'
& (Join-Path $PSScriptRoot 'build-agshell-vt.ps1')

Step 'running the tests'
$diagnostics = Join-Path $root '.build\release-diagnostics'
Remove-Item -Recurse -Force $diagnostics -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $diagnostics -Force | Out-Null
& dotnet test (Join-Path $root 'agshell.slnx') -c Release -v normal -nologo `
    --logger 'console;verbosity=detailed' --logger trx --results-directory (Join-Path $diagnostics 'trx') `
    2>&1 | Tee-Object -FilePath (Join-Path $diagnostics 'tests-console.log')
$testExitCode = $LASTEXITCODE
if ($testExitCode -ne 0) { throw "dotnet test failed with exit code $testExitCode; diagnostics: $diagnostics" }

Step 'publishing agshell.exe (self-contained, single file)'
Run dotnet @('publish', (Join-Path $root 'src\AgShell.App\AgShell.App.csproj'), '-c', 'Release', '-r', 'win-x64', '--self-contained',
    '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true', '-p:EnableCompressionInSingleFile=true',
    "-p:Version=$Version", '-o', $app, '-nologo', '-v', 'q')

Step ($(if ($NoAot) { 'publishing agshellctl.exe (single file, no AOT)' } else { 'publishing agshellctl.exe (Native AOT)' }))
$ctlArguments = @('publish', (Join-Path $root 'src\AgShell.Ctl\AgShell.Ctl.csproj'), '-c', 'Release', '-r', 'win-x64', "-p:Version=$Version", '-o', $ctl, '-nologo', '-v', 'q')
if ($NoAot) { $ctlArguments += @('-p:PublishAot=false', '--self-contained', '-p:PublishSingleFile=true', '-p:PublishTrimmed=true', '-p:EnableCompressionInSingleFile=true') }
Run dotnet $ctlArguments

Step ($(if ($NoAot) { 'publishing agshell-live.exe (single file, no AOT)' } else { 'publishing agshell-live.exe (Native AOT)' }))
$liveArguments = @('publish', (Join-Path $root 'src\AgShell.Live\AgShell.Live.csproj'), '-c', 'Release', '-r', 'win-x64', "-p:Version=$Version", '-o', $live, '-nologo', '-v', 'q')
if ($NoAot) { $liveArguments += @('-p:PublishAot=false', '--self-contained', '-p:PublishSingleFile=true', '-p:PublishTrimmed=true', '-p:EnableCompressionInSingleFile=true') }
Run dotnet $liveArguments

# The app's build drops a framework-dependent agshellctl and live daemon next to it; the standalone ones replace them.
Get-ChildItem $app -Filter 'agshellctl*' | Remove-Item -Force
Copy-Item (Join-Path $ctl 'agshellctl.exe') $app
Get-ChildItem $app -Filter 'agshell-live*' | Remove-Item -Force
Copy-Item (Join-Path $live 'agshell-live.exe') $app
Copy-Item -Force (Join-Path $live 'agshell-vt.dll') $app
Get-ChildItem $app -File | Where-Object { $_.Extension -in '.pdb', '.xml' } | Remove-Item -Force

Step 'including third-party license texts and notices'
Copy-Item (Join-Path $root 'THIRD-PARTY-NOTICES.md') $app
Copy-Item (Join-Path $root 'LICENSE') $app
Copy-Item (Join-Path $root 'licenses') (Join-Path $app 'licenses') -Recurse -Force

Step "packing $([IO.Path]::GetFileName($zip))"
Compress-Archive -Path (Join-Path $app '*') -DestinationPath $zip

$iscc = (Get-Command ISCC.exe -ErrorAction SilentlyContinue).Source
if (-not $iscc) {
    $iscc = @("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe", "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe") |
        Where-Object { Test-Path $_ } | Select-Object -First 1
}
if ($iscc) {
    Step 'building the installer'
    Run $iscc @('/Q', "/DAppVersion=$Version", "/DSourceDir=$app", "/O$dist", (Join-Path $root 'installer\agshell.iss'))
} elseif ($Publish) {
    throw 'Inno Setup 6 (ISCC.exe) is needed to publish'
} else {
    Write-Warning 'Inno Setup 6 not found: the installer is skipped'
}

if ($Publish) {
    Step "creating the GitHub release v$Version"
    $assets = @($zip) + @(Get-ChildItem $dist -Filter 'agshell-*-setup.exe' | ForEach-Object FullName)
    Run gh (@('release', 'create', "v$Version", '--repo', $Repository, '--target', 'main', '--title', "AgShell $Version", '--generate-notes') + $assets)
}

Step "done: $dist"
