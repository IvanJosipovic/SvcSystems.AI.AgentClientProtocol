param(
    [switch] $CreatePr
)

$ErrorActionPreference = 'Stop'

if ($args -contains '--create-pr') {
    $CreatePr = $true
}

$unexpectedArguments = @($args | Where-Object { $_ -ne '--create-pr' })
if ($unexpectedArguments.Count -gt 0) {
    throw "Unexpected argument(s): $($unexpectedArguments -join ', ')"
}

$repositoryRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repositoryRoot

$branchName = if ([string]::IsNullOrWhiteSpace($env:BRANCH_NAME)) {
    'chore/update-acp-schemas'
} else {
    $env:BRANCH_NAME
}

$acpSchemaDirectory = Join-Path $repositoryRoot 'schema/acp/v1'
$registrySchemaDirectory = Join-Path $repositoryRoot 'schema/registry/v1'
New-Item -ItemType Directory -Force -Path $acpSchemaDirectory, $registrySchemaDirectory | Out-Null

$schemaRoot = [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot 'schema'))
$allowedSchemaDirectories = @(
    [System.IO.Path]::GetFullPath($acpSchemaDirectory),
    [System.IO.Path]::GetFullPath($registrySchemaDirectory)
)

function Clear-SchemaDirectory {
    param(
        [Parameter(Mandatory)]
        [string] $Path,

        [Parameter(Mandatory)]
        [string[]] $Preserve
    )

    $fullPath = [System.IO.Path]::GetFullPath($Path)
    if ($fullPath -notin $allowedSchemaDirectories) {
        throw "Refusing to clear unexpected schema directory '$fullPath'."
    }

    $relativePath = [System.IO.Path]::GetRelativePath($schemaRoot, $fullPath)
    if ([System.IO.Path]::IsPathRooted($relativePath) -or
        $relativePath -eq '..' -or
        $relativePath.StartsWith("..$([System.IO.Path]::DirectorySeparatorChar)", [System.StringComparison]::Ordinal)) {
        throw "Refusing to clear a directory outside '$schemaRoot'."
    }

    foreach ($item in Get-ChildItem -LiteralPath $fullPath -Force) {
        if ($item.Name -notlike '*.generator.json' -and $item.Name -notin $Preserve) {
            Remove-Item -LiteralPath $item.FullName -Recurse -Force
        }
    }
}

Clear-SchemaDirectory -Path $acpSchemaDirectory -Preserve @('schema.generator.json', 'VENDORED.md')
Clear-SchemaDirectory -Path $registrySchemaDirectory -Preserve @('registry.generator.json')

& gh release download `
    --repo agentclientprotocol/agent-client-protocol `
    --pattern 'meta.json' `
    --pattern 'meta.unstable.json' `
    --pattern 'schema.json' `
    --pattern 'schema.unstable.json' `
    --clobber `
    --dir $acpSchemaDirectory
if ($LASTEXITCODE -ne 0) {
    throw 'Failed to download ACP schema release assets.'
}

& gh release download `
    --repo agentclientprotocol/registry `
    --pattern 'agent.schema.json' `
    --pattern 'registry.schema.json' `
    --clobber `
    --dir $registrySchemaDirectory
if ($LASTEXITCODE -ne 0) {
    throw 'Failed to download ACP Registry schema release assets.'
}

& git diff HEAD --quiet -- schema
$diffExitCode = $LASTEXITCODE
if ($diffExitCode -eq 0) {
    Write-Host 'Vendored schemas are already up to date.'
    exit 0
}
if ($diffExitCode -ne 1) {
    throw "git diff failed with exit code $diffExitCode."
}

if (-not $CreatePr) {
    Write-Host 'Schema assets were updated in the working tree. Pass --create-pr to commit, push, and create a pull request.'
    exit 0
}

& gh auth status --hostname github.com *> $null
if ($LASTEXITCODE -ne 0) {
    if ([string]::IsNullOrWhiteSpace($env:GH_TOKEN)) {
        if ([Console]::IsInputRedirected) {
            throw 'GitHub CLI is not logged in. Run gh auth login or set GH_TOKEN before using --create-pr.'
        }

        $secureToken = Read-Host 'GitHub CLI is not logged in. Enter a GitHub token for --create-pr' -AsSecureString
        $env:GH_TOKEN = [System.Net.NetworkCredential]::new('', $secureToken).Password
        if ([string]::IsNullOrWhiteSpace($env:GH_TOKEN)) {
            throw 'A GitHub token is required to create the schema pull request.'
        }
    }

    & gh auth status --hostname github.com *> $null
    if ($LASTEXITCODE -ne 0) {
        throw 'GitHub CLI authentication failed. Run gh auth login or provide a valid GH_TOKEN.'
    }
}
if ([string]::IsNullOrWhiteSpace($env:GITHUB_REPOSITORY)) {
    throw 'GITHUB_REPOSITORY must be set to create the schema pull request.'
}
if ([string]::IsNullOrWhiteSpace($env:DEFAULT_BRANCH)) {
    throw 'DEFAULT_BRANCH must be set to create the schema pull request.'
}

& git config user.name 'github-actions[bot]'
if ($LASTEXITCODE -ne 0) { throw 'Failed to configure the Git author name.' }
& git config user.email '41898282+github-actions[bot]@users.noreply.github.com'
if ($LASTEXITCODE -ne 0) { throw 'Failed to configure the Git author email.' }

& git checkout -B $branchName
if ($LASTEXITCODE -ne 0) { throw "Failed to check out branch '$branchName'." }
& git add schema
if ($LASTEXITCODE -ne 0) { throw 'Failed to stage schema updates.' }
& git commit -m 'chore: update ACP schemas'
if ($LASTEXITCODE -ne 0) { throw 'Failed to commit schema updates.' }
& git push --force-with-lease origin $branchName
if ($LASTEXITCODE -ne 0) { throw "Failed to push branch '$branchName'." }

$existingPullRequest = & gh pr list `
    --repo $env:GITHUB_REPOSITORY `
    --head $branchName `
    --state open `
    --json number `
    --jq '.[0].number'
if ($LASTEXITCODE -ne 0) {
    throw 'Failed to check for an existing schema pull request.'
}

if (-not [string]::IsNullOrWhiteSpace(($existingPullRequest -join '').Trim())) {
    Write-Host "Pull request #$($existingPullRequest -join '') already exists."
    exit 0
}

& gh pr create `
    --repo $env:GITHUB_REPOSITORY `
    --head $branchName `
    --base $env:DEFAULT_BRANCH `
    --title 'chore: update ACP schemas' `
    --body 'Updates the vendored ACP and ACP Registry schemas from their latest GitHub releases.'
if ($LASTEXITCODE -ne 0) {
    throw 'Failed to create the schema pull request.'
}
