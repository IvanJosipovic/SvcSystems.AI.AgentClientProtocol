[CmdletBinding()]
param(
    [string] $SourceDirectory,
    [string] $ReportDirectory,
    [ValidateRange(1, 600)] [int] $Timeout = 10,
    [ValidateRange(1, 600)] [int] $StartupTimeout = 15,
    [ValidateRange(1, 600)] [int] $TestTimeout = 60,
    [ValidateRange(1, 60)] [int] $CloseGrace = 1
)

$ErrorActionPreference = 'Stop'
$revision = 'fa00ccdc13dfaa4b288a91877c1f3e468af4aa7d'
$repository = 'https://github.com/agentclientprotocol/acp-tck.git'
$repoRoot = Split-Path $PSScriptRoot -Parent
$artifactRoot = Join-Path $repoRoot 'artifacts' 'acp-tck'
if (-not $SourceDirectory) {
    $SourceDirectory = Join-Path $artifactRoot $revision
}
if (-not $ReportDirectory) {
    $ReportDirectory = Join-Path $artifactRoot 'reports'
}
$SourceDirectory = [IO.Path]::GetFullPath($SourceDirectory)
$ReportDirectory = [IO.Path]::GetFullPath($ReportDirectory)

foreach ($tool in @('git', 'uv', 'dotnet')) {
    if (-not (Get-Command $tool -ErrorAction SilentlyContinue)) {
        throw "Required tool '$tool' was not found. Install it before running the TCK checks."
    }
}

if (-not (Test-Path -LiteralPath $SourceDirectory)) {
    [IO.Directory]::CreateDirectory((Split-Path $SourceDirectory -Parent)) | Out-Null
    & git clone --quiet --no-checkout $repository $SourceDirectory
    if ($LASTEXITCODE -ne 0) { throw 'Failed to clone ACP TCK.' }
    & git -C $SourceDirectory checkout --quiet --detach $revision
    if ($LASTEXITCODE -ne 0) { throw 'Failed to check out the pinned ACP TCK revision.' }
}
$actualRevision = & git -C $SourceDirectory rev-parse HEAD
if ($LASTEXITCODE -ne 0 -or $actualRevision -ne $revision) {
    throw "ACP TCK must be checked out at $revision. Refusing to change the existing checkout."
}
& git -C $SourceDirectory diff --quiet HEAD --
if ($LASTEXITCODE -ne 0) { throw 'The pinned ACP TCK checkout has tracked modifications.' }

# The official runner requires Python 3.14 and its locked Python dependencies.
& uv sync --locked --check --project $SourceDirectory --python 3.14
if ($LASTEXITCODE -ne 0) {
    & uv sync --locked --project $SourceDirectory --python 3.14
    if ($LASTEXITCODE -ne 0) { throw 'Failed to restore the official ACP TCK environment.' }
}
$python = & uv run --no-sync --project $SourceDirectory python -c 'import sys; print(sys.executable)'
if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $python)) {
    throw 'Could not locate the ACP TCK Python interpreter.'
}
[IO.Directory]::CreateDirectory($ReportDirectory) | Out-Null

& uv run --no-sync --project $SourceDirectory acp-tck --version
if ($LASTEXITCODE -ne 0) { throw 'ACP TCK version check failed.' }

$reports = @()
foreach ($fixture in @('conforming_full', 'conforming', 'gated_by_auth')) {
    $reportPath = Join-Path $ReportDirectory "$fixture.json"
    $agent = Join-Path $SourceDirectory 'tests' 'fixtures' 'agents' 'v1' "$fixture.py"
    $arguments = @(
        '--protocol-version', '1',
        '--agent-cwd', $ReportDirectory,
        '--agent-env', 'PYTHONDONTWRITEBYTECODE=1',
        '--agent-env', 'ACP_TCK_BASELINE=1',
        '--timeout', "$Timeout",
        '--startup-timeout', "$StartupTimeout",
        '--test-timeout', "$TestTimeout",
        '--cancel-prompt', '__hang__',
        '--close-grace', "$CloseGrace",
        '--report-json', $reportPath,
        '-v'
    )
    if ($fixture -ne 'conforming') {
        $arguments += @('--auth-method', 'tck')
    }
    if ($fixture -eq 'conforming_full') {
        # This is an offline fixture, never a user's real agent or credentials.
        $arguments += '--allow-logout'
    }
    Write-Host "Running the complete upstream v1 conformance suite against $fixture (agent baseline, not client certification)."
    & uv run --no-sync --project $SourceDirectory acp-tck @arguments -- $python -B -X utf8 $agent
    if ($LASTEXITCODE -ne 0) { throw "Official ACP TCK baseline failed: $fixture. See $reportPath." }
    $report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
    if ($report.protocol_version -ne 1 -or -not $report.verdict.conformant) {
        throw "ACP TCK did not report a conformant v1 agent baseline: $fixture."
    }
    $reports += $report
}

# The repository's own agent host is a conformance target too: build the sample agent and drive it
# with the same official suite so Agent/IAgentHandler are exercised end to end, not just the
# Python baselines.
$sampleProject = Join-Path $repoRoot 'samples' 'SvcSystems.AI.AgentClientProtocol.SampleAgent' 'SvcSystems.AI.AgentClientProtocol.SampleAgent.csproj'
$sampleOutput = Join-Path (Split-Path $sampleProject -Parent) 'bin' 'Release' 'net11.0'
Write-Host 'Building the repository sample agent (SvcSystems.AI.AgentClientProtocol.SampleAgent).'
& dotnet build $sampleProject --configuration Release --nologo
if ($LASTEXITCODE -ne 0) { throw 'Failed to build the repository sample agent.' }
$sampleExecutable = Join-Path $sampleOutput 'SvcSystems.AI.AgentClientProtocol.SampleAgent'
if (Test-Path -LiteralPath "$sampleExecutable.exe") { $sampleExecutable = "$sampleExecutable.exe" }
if (-not (Test-Path -LiteralPath $sampleExecutable)) {
    throw "Could not find the built sample agent at $sampleExecutable."
}

$sampleReportPath = Join-Path $ReportDirectory 'sample-agent.json'
$sampleArguments = @(
    '--protocol-version', '1',
    '--agent-cwd', $ReportDirectory,
    '--agent-env', 'DOTNET_NOLOGO=1',
    '--timeout', "$Timeout",
    '--startup-timeout', "$StartupTimeout",
    '--test-timeout', "$TestTimeout",
    '--cancel-prompt', '__hang__',
    '--close-grace', "$CloseGrace",
    '--auth-method', 'tck',
    # The sample agent is offline and ships no credentials; logging out revokes nothing.
    '--allow-logout',
    '--report-json', $sampleReportPath,
    '-v'
)
Write-Host 'Running the complete upstream v1 conformance suite against the repository sample agent (agent host, not client certification).'
& uv run --no-sync --project $SourceDirectory acp-tck @sampleArguments -- $sampleExecutable
if ($LASTEXITCODE -ne 0) { throw "Official ACP TCK reported the repository sample agent as non-conformant. See $sampleReportPath." }
$sampleReport = Get-Content -LiteralPath $sampleReportPath -Raw | ConvertFrom-Json
if ($sampleReport.protocol_version -ne 1 -or -not $sampleReport.verdict.conformant) {
    throw 'ACP TCK did not report a conformant v1 agent for the repository sample agent.'
}
$reports += $sampleReport

# A conformant verdict alone can hide skipped/untested optional requirements.
$requirements = @($reports | ForEach-Object { $_.requirements })
$uncovered = @($requirements | Group-Object id | Where-Object {
    'PASS' -notin $_.Group.status -or 'FAIL' -in $_.Group.status -or 'NOT_TESTED' -in $_.Group.status
})
if ($uncovered.Count -ne 0) {
    throw "Not every upstream v1 requirement was exercised successfully: $($uncovered.Name -join ', ')."
}
Write-Host "All $(($requirements | Group-Object id).Count) upstream v1 requirements have a PASS across the agent baselines and the repository sample agent."

$previousRoot = $env:ACP_TCK_ROOT
$previousPython = $env:ACP_TCK_PYTHON
try {
    $env:ACP_TCK_ROOT = $SourceDirectory
    $env:ACP_TCK_PYTHON = $python
    $project = Join-Path $repoRoot 'tests' 'SvcSystems.AI.AgentClientProtocol.IntegrationTests' 'SvcSystems.AI.AgentClientProtocol.IntegrationTests.csproj'
    Write-Host 'Running the .NET client interoperability matrix against the official v1 fixtures.'
    Push-Location $repoRoot
    try {
        & dotnet test --project $project --filter-trait 'Category=Tck' |
            Tee-Object -FilePath (Join-Path $ReportDirectory 'client-tests.log')
        if ($LASTEXITCODE -ne 0) { throw 'The .NET client ACP TCK interoperability matrix failed.' }
    }
    finally {
        Pop-Location
    }
}
finally {
    $env:ACP_TCK_ROOT = $previousRoot
    $env:ACP_TCK_PYTHON = $previousPython
}
Write-Host "Pinned ACP TCK revision: $revision"
Write-Host "Reports: $ReportDirectory"
