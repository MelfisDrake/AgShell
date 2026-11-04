<#
.SYNOPSIS
Builds agshell-vt.dll, the live daemon's terminal state (native\agshell-vt), from the ghostty revision zmx pins with agterm's
ghostty patches, as agterm's scripts/setup.sh builds zmx against its private zmx-ghostty checkout.

.DESCRIPTION
The result lands in .build\agshell-vt\bin. A stamp of the ghostty revision, the target and the digest of the patches and
native\agshell-vt sources skips the build when nothing changed. Zig 0.16 comes from -Zig, AGSHELL_ZIG or PATH.

.EXAMPLE
scripts/build-agshell-vt.ps1 -Zig C:\temp\zig\zig-x86_64-windows-0.16.0\zig.exe
scripts/build-agshell-vt.ps1 -Test     # also runs the library's tests
#>
param(
    [string]$Zig = $env:AGSHELL_ZIG,
    [switch]$Test,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

# zmx 8bab1f0's build.zig.zon pins this ghostty; agterm builds zmx at that revision (ZMX_REV in its setup.sh).
$ghosttyRepo = 'https://github.com/ghostty-org/ghostty'
$ghosttyRev = '8af6897c0afc63037a8a3efee4162a380e3a4572'
# The gnu target needs only the UCRT, which every supported Windows has; the msvc one also needs VCRUNTIME140.dll.
$target = 'x86_64-windows-gnu'

$source = Join-Path $root 'native\agshell-vt'
$patches = Join-Path $source 'patches\ghostty'
$build = Join-Path $root '.build\agshell-vt'
$ghostty = Join-Path $build 'ghostty'
$deps = Join-Path $build 'deps'
$out = Join-Path $build 'out'
$stampFile = Join-Path $build 'stamp'
$env:ZIG_GLOBAL_CACHE_DIR = Join-Path $build 'zig-global'
$env:ZIG_LOCAL_CACHE_DIR = Join-Path $build 'zig-local'

function Step($text) { Write-Host "==> $text" -ForegroundColor Cyan }
function Run([string]$file, [string[]]$arguments) {
    & $file @arguments
    if ($LASTEXITCODE -ne 0) { throw "$file $($arguments -join ' ') failed with exit code $LASTEXITCODE" }
}

if (-not $Zig) { $Zig = (Get-Command zig -ErrorAction SilentlyContinue).Source }
if (-not $Zig -or -not (Test-Path $Zig)) { throw 'Zig 0.16 is needed: pass -Zig <zig.exe>, set AGSHELL_ZIG or put zig on PATH' }
$zigVersion = & $Zig version
if ($zigVersion -notlike '0.16.*') { throw "ghostty $ghosttyRev needs Zig 0.16, $Zig is $zigVersion" }

$inputs = @(Get-ChildItem $patches -Filter '*.patch' | Sort-Object Name) +
    @(Get-Item (Join-Path $source 'build.zig'), (Join-Path $source 'build.zig.zon')) +
    @(Get-ChildItem (Join-Path $source 'src') -File -Recurse | Sort-Object FullName)
$digest = [System.Security.Cryptography.SHA256]::Create()
foreach ($file in $inputs) {
    $bytes = [IO.File]::ReadAllBytes($file.FullName)
    [void]$digest.TransformBlock($bytes, 0, $bytes.Length, $null, 0)
}
[void]$digest.TransformFinalBlock(@(), 0, 0)
$stamp = "$ghosttyRev $target $(([BitConverter]::ToString($digest.Hash) -replace '-', '').Substring(0, 16).ToLowerInvariant())"
$dll = Join-Path $out 'bin\agshell-vt.dll'
if (-not $Force -and -not $Test -and (Test-Path $dll) -and (Test-Path $stampFile) -and (Get-Content $stampFile -Raw).Trim() -eq $stamp) {
    Write-Host "agshell-vt is up to date ($stamp)"
    return
}

New-Item -ItemType Directory -Force $build, $deps | Out-Null
$checkout = Join-Path $build 'ghostty.stamp'
$patchDigest = (Get-ChildItem $patches -Filter '*.patch' | Sort-Object Name | Get-FileHash | ForEach-Object Hash) -join ''
if (-not (Test-Path $checkout) -or (Get-Content $checkout -Raw).Trim() -ne "$ghosttyRev $patchDigest") {
    Step "fetching ghostty $ghosttyRev"
    Remove-Item -Recurse -Force $ghostty -ErrorAction SilentlyContinue
    Run git @('init', '-q', $ghostty)
    Run git @('-C', $ghostty, 'remote', 'add', 'origin', $ghosttyRepo)
    Run git @('-C', $ghostty, 'fetch', '-q', '--depth', '1', 'origin', $ghosttyRev)
    Run git @('-C', $ghostty, '-c', 'advice.detachedHead=false', 'checkout', '-q', 'FETCH_HEAD')
    foreach ($patch in Get-ChildItem $patches -Filter '*.patch' | Sort-Object Name) {
        Step "applying ghostty/$($patch.Name)"
        Run git @('-C', $ghostty, 'apply', '--whitespace=nowarn', $patch.FullName)
    }
    Set-Content $checkout "$ghosttyRev $patchDigest"
}

# zig fetches its packages itself, but not through an authenticating HTTP proxy (it answers 400); curl honors
# HTTPS_PROXY, so each package zig fails to fetch comes down here and zig takes it from the file. A git package comes as
# GitHub's tarball of its commit.
Step 'fetching packages'
Push-Location $source
try {
    $tried = @{}
    while ($true) {
        $fetch = & { $ErrorActionPreference = 'Continue'; & $Zig build --fetch "-Dtarget=$target" 2>&1 | ForEach-Object { "$_" } }
        if ($LASTEXITCODE -eq 0) { break }
        $missing = @($fetch | Select-String -Pattern '\.url = "([^"]+)"' -AllMatches | ForEach-Object { $_.Matches } | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique)
        $fresh = @($missing | Where-Object { -not $tried.ContainsKey($_) })
        if ($fresh.Count -eq 0) { throw "zig cannot fetch its packages:`n$($fetch -join "`n")" }
        foreach ($url in $fresh) {
            $tried[$url] = $true
            $download = $url
            if ($url -match '^git\+(https://github\.com/[^#]+?)(\.git)?#([0-9a-f]{40})$') { $download = "$($Matches[1])/archive/$($Matches[3]).tar.gz" }
            $file = Join-Path $deps (($download -replace '^https://', '') -replace '[^A-Za-z0-9._-]', '_')
            if (-not (Test-Path $file)) {
                Run curl.exe @('-sSfL', '-o', "$file.part", $download)
                Move-Item -Force "$file.part" $file
            }
            Write-Host "  $url"
            & { $ErrorActionPreference = 'Continue'; & $Zig fetch $file 2>&1 | Out-Null }
        }
    }

    Step "building agshell-vt ($target)"
    Remove-Item -Recurse -Force $out -ErrorAction SilentlyContinue
    Run $Zig @('build', '-Doptimize=ReleaseFast', "-Dtarget=$target", '--prefix', $out)
    if ($Test) {
        Step 'testing agshell-vt'
        Run $Zig @('build', 'test', "-Dtarget=$target")
    }
}
finally {
    Pop-Location
}

if (-not (Test-Path $dll)) { throw "the build did not produce $dll" }
Set-Content $stampFile $stamp
Write-Host "agshell-vt.dll: $dll"
