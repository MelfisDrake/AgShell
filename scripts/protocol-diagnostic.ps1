param(
    [Parameter(Mandatory)][string]$SourceRoot,
    [Parameter(Mandatory)][string]$OutputRoot
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
New-Item -ItemType Directory -Force $OutputRoot | Out-Null
$SourceRoot = (Resolve-Path $SourceRoot).Path
$OutputRoot = (Resolve-Path $OutputRoot).Path
$target = 'AgShell.Protocol.Tests.ControlServerTests.ALastLineWithoutNewlineCountsAndASilentClientIsAnswered'
$project = 'tests/AgShell.Protocol.Tests/AgShell.Protocol.Tests.csproj'
$records = [System.Collections.Generic.List[object]]::new()

function Invoke-Dotnet([string]$Name, [string[]]$Arguments) {
    $started = [DateTime]::UtcNow.ToString('O')
    & dotnet @Arguments 2>&1 | Tee-Object -FilePath (Join-Path $OutputRoot "$Name.log") | Out-Host
    $code = $LASTEXITCODE
    $records.Add([ordered]@{
        name = $Name; executable = 'dotnet'; arguments = $Arguments
        startedUtc = $started; finishedUtc = [DateTime]::UtcNow.ToString('O'); exitCode = $code
    })
    $records | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $OutputRoot 'commands.json')
    return $code
}

Push-Location $SourceRoot
try {
    $actual = (& git rev-parse HEAD).Trim()
    [ordered]@{
        workflowSha = $env:GITHUB_SHA; workflowRef = $env:GITHUB_WORKFLOW_REF
        sourceSha = $actual; requestedSourceSha = $env:SOURCE_SHA
        runUrl = "$env:GITHUB_SERVER_URL/$env:GITHUB_REPOSITORY/actions/runs/$env:GITHUB_RUN_ID"
        runId = $env:GITHUB_RUN_ID; attempt = $env:GITHUB_RUN_ATTEMPT
        imageOS = $env:ImageOS; imageVersion = $env:ImageVersion
        runnerOS = $env:RUNNER_OS; runnerArch = $env:RUNNER_ARCH
        os = [System.Runtime.InteropServices.RuntimeInformation]::OSDescription
        processorCount = [Environment]::ProcessorCount
        plan = 'Unmodified full Protocol suite (57 tests), then 10 independent dotnet test target invocations, even after a test failure. No retries, instrumentation, deadline or load changes.'
    } | ConvertTo-Json | Set-Content (Join-Path $OutputRoot 'metadata.json')
    if ($actual -cne $env:SOURCE_SHA) { throw 'Source checkout SHA mismatch' }
    & git status --porcelain | Set-Content (Join-Path $OutputRoot 'source-status-before.txt')
    if (& git status --porcelain) { throw 'Source checkout is dirty' }
    foreach ($fixture in @('agshell.slnx', 'src/AgShell.EventLog/AgShell.man')) {
        if (!(Test-Path $fixture)) { throw "Missing fixture: $fixture" }
    }
    $tracked = & git ls-files src/AgShell.Protocol tests/AgShell.Protocol.Tests Directory.Build.props agshell.slnx src/AgShell.EventLog/AgShell.man
    $tracked | ForEach-Object {
        [ordered]@{ path = $_; sha256 = (Get-FileHash -Algorithm SHA256 $_).Hash.ToLowerInvariant() }
    } | ConvertTo-Json | Set-Content (Join-Path $OutputRoot 'source-sha256.json')
    if (Get-ChildItem src/AgShell.Protocol, tests/AgShell.Protocol.Tests -Directory -Recurse | Where-Object { $_.Name -in @('bin', 'obj') }) {
        throw 'Unexpected existing bin/obj: refusing potentially stale assembly'
    }
    $env:AGSHELL_STATE_DIR = Join-Path $env:RUNNER_TEMP 'protocol-diagnostic-state'
    $null = Invoke-Dotnet 'dotnet-info' @('--info')
    $null = Invoke-Dotnet 'dotnet-runtimes' @('--list-runtimes')
    $null = Invoke-Dotnet 'dotnet-sdks' @('--list-sdks')
    $buildCode = Invoke-Dotnet 'build' @('build', $project, '-t:Rebuild', '-c', 'Release', '-v', 'normal', '-nologo')
    if ($buildCode -ne 0) { throw "Protocol graph build failed: $buildCode" }
    Get-ChildItem tests/AgShell.Protocol.Tests/bin/Release/net10.0 -File |
        Where-Object { $_.Extension -in @('.dll', '.pdb', '.json') } |
        ForEach-Object { [ordered]@{ name = $_.Name; sha256 = (Get-FileHash $_.FullName).Hash.ToLowerInvariant() } } |
        ConvertTo-Json | Set-Content (Join-Path $OutputRoot 'assembly-sha256.json')
    $bad = $false
    foreach ($index in 0..10) {
        $name = if ($index -eq 0) { 'suite' } else { 'target-{0:d2}' -f $index }
        $resultDir = Join-Path $OutputRoot $name
        New-Item -ItemType Directory $resultDir | Out-Null
        [string[]]$arguments = @('test', $project, '-c', 'Release', '--no-build', '--no-restore', '-nologo', '--logger', 'console;verbosity=detailed', '--logger', "trx;LogFileName=$name.trx", '--results-directory', $resultDir, '--diag', (Join-Path $resultDir 'vstest.log'))
        if ($index -gt 0) { $arguments += @('--filter', "FullyQualifiedName=$target") }
        $code = Invoke-Dotnet $name $arguments
        if ($code -ne 0) { $bad = $true }
        $trxPath = Join-Path $resultDir "$name.trx"
        if (!(Test-Path $trxPath)) { $bad = $true; continue }
        [xml]$trx = Get-Content -Raw $trxPath
        $expected = if ($index -eq 0) { 57 } else { 1 }
        $counters = $trx.TestRun.ResultSummary.Counters
        $targetResults = @($trx.TestRun.Results.UnitTestResult | Where-Object { $_.testName -eq $target })
        if ([int]$counters.total -ne $expected -or [int]$counters.executed -ne $expected -or [int]$counters.passed -ne $expected -or $targetResults.Count -ne 1 -or $targetResults[0].outcome -ne 'Passed') {
            $bad = $true
        }
    }
    & git diff --exit-code HEAD -- src/AgShell.Protocol tests/AgShell.Protocol.Tests Directory.Build.props agshell.slnx src/AgShell.EventLog/AgShell.man |
        Set-Content (Join-Path $OutputRoot 'source-diff-after.txt')
    if ($LASTEXITCODE -ne 0) { $bad = $true }
    if ($bad) { throw 'Diagnostic series has failures or missing/unexpected results; consult every console log, TRX and exit code' }
} finally {
    Pop-Location
}
