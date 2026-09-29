[CmdletBinding()]
param(
    [switch]$NoInvoke
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$projectRoot = Split-Path -Parent $PSScriptRoot
$solution = Join-Path $projectRoot 'CODMunityTop250Collector.sln'
$appProject = Join-Path $projectRoot 'CODMunityTop250Collector.App\CODMunityTop250Collector.App.csproj'
$updateHostProject = Join-Path $projectRoot 'CODMunityTop250Collector.UpdateHost\CODMunityTop250Collector.UpdateHost.csproj'
$testsProject = Join-Path $projectRoot 'CODMunityTop250Collector.Tests\CODMunityTop250Collector.Tests.csproj'
$msiProject = Join-Path $projectRoot 'Installer.Wix\CODMunityTop250Collector.Installer.wixproj'
$bundleProject = Join-Path $projectRoot 'Installer.Bundle\CODMunityTop250Collector.Bundle.wixproj'
$portableDirectory = Join-Path $projectRoot 'dist\portable'
$appDll = Join-Path $portableDirectory 'CODMunityTop250Collector.dll'
$watcherTemplate = Join-Path $portableDirectory 'Assets\watchdog\bnet-play.png'
$releaseVersion = '0.1.26-alpha'
$msi = Join-Path $projectRoot ('dist\msi\CODMunityTop250Collector-' + $releaseVersion + '-win-x64.msi')
$setup = Join-Path $projectRoot ('dist\setup\CODMunityTop250Collector-' + $releaseVersion + '-Setup.exe')
$resourceName = 'CODMunityTop250Collector.App.EmbeddedRankedSyncToken'
$succeeded = $false
$credential = 'NOT PRESENT'
$appDllHash = $null
$msiHash = $null
$setupHash = $null

function Invoke-RequiredDotnet {
    param([Parameter(Mandatory)][string[]]$Arguments, [Parameter(Mandatory)][string]$Step)

    & dotnet @Arguments 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "$Step failed (exit code $LASTEXITCODE)." }
}

function Get-CurrentSourceRevision {
    & git -C $projectRoot diff --quiet
    $workingTreeExitCode = $LASTEXITCODE
    & git -C $projectRoot diff --cached --quiet
    $indexExitCode = $LASTEXITCODE
    if (($workingTreeExitCode -ne 0) -or ($indexExitCode -ne 0)) { throw 'Release build requires committed source changes so artifact metadata can identify the exact source revision.' }
    $revision = (& git -C $projectRoot rev-parse --verify HEAD 2>$null).Trim()
    $exitCode = $LASTEXITCODE
    if (($exitCode -ne 0) -or ($revision -notmatch '^[0-9a-f]{40}$')) { throw 'Unable to resolve the exact Git source revision for the release build.' }
    return $revision
}

function Get-AssemblyInformationalVersion {
    param([Parameter(Mandatory)][string]$AssemblyPath)

    $assembly = [Reflection.Assembly]::LoadFrom($AssemblyPath)
    $attribute = $assembly.GetCustomAttributes([Reflection.AssemblyInformationalVersionAttribute], $false) | Select-Object -First 1
    if ($null -eq $attribute) { throw 'Published application assembly has no informational version.' }
    return [string]$attribute.InformationalVersion
}

function Assert-ArtifactSourceRevision {
    param([Parameter(Mandatory)][string]$AssemblyPath, [Parameter(Mandatory)][string]$Version, [Parameter(Mandatory)][string]$SourceRevision)

    $expected = $Version + '+' + $SourceRevision
    $actual = Get-AssemblyInformationalVersion -AssemblyPath $AssemblyPath
    if (-not [string]::Equals($actual, $expected, [StringComparison]::Ordinal)) { throw "Published informational version does not match the release source revision. expected=$expected actual=$actual" }
}

function Get-EmbeddedCredentialPresent {
    param([Parameter(Mandatory)][string]$AssemblyPath)

    $assembly = [Reflection.Assembly]::LoadFrom($AssemblyPath)
    $stream = $assembly.GetManifestResourceStream($resourceName)
    if ($null -eq $stream) { return $false }
    try { return $stream.Length -gt 0 }
    finally { $stream.Dispose() }
}

function Get-ClosedBetaCredentialState {
    param([scriptblock]$PromptProvider = { Read-Host -Prompt 'Closed Beta token' -AsSecureString })

    $inherited = [bool](-not [string]::IsNullOrWhiteSpace($env:CODMUNITY_RANKED_SYNC_TOKEN_BUILD))
    if ($inherited) {
        return [pscustomobject]@{ Source = 'environment'; Temporary = $false; SecureToken = $null; Bstr = [IntPtr]::Zero; PlainToken = $null }
    }

    $secureToken = & $PromptProvider
    $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secureToken)
    $plainToken = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr)
    if ([string]::IsNullOrWhiteSpace($plainToken)) {
        [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr)
        $secureToken.Dispose()
        throw 'Closed Beta token must not be empty.'
    }
    [Environment]::SetEnvironmentVariable('CODMUNITY_RANKED_SYNC_TOKEN_BUILD', $plainToken, 'Process')
    return [pscustomobject]@{ Source = 'prompt'; Temporary = $true; SecureToken = $secureToken; Bstr = $bstr; PlainToken = $plainToken }
}

function Clear-ClosedBetaCredentialState {
    param([object]$State)

    if ($null -eq $State) { return }
    if ($State.Temporary) { [Environment]::SetEnvironmentVariable('CODMUNITY_RANKED_SYNC_TOKEN_BUILD', $null, 'Process') }
    $State.PlainToken = $null
    if ($State.Bstr -ne [IntPtr]::Zero) { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($State.Bstr) }
    if ($null -ne $State.SecureToken) { $State.SecureToken.Dispose() }
}

if (-not $NoInvoke) {
$credentialState = $null
try {
    Push-Location $projectRoot
    $sourceRevision = Get-CurrentSourceRevision
    $credentialState = Get-ClosedBetaCredentialState
    $sourceRevisionProperty = '-p:SourceRevisionId=' + $sourceRevision
    Invoke-RequiredDotnet -Step 'Release metadata clean' -Arguments @('clean', $solution, '-c', 'Release', '--nologo')
    Invoke-RequiredDotnet -Step 'Release build' -Arguments @('build', $solution, '-c', 'Release', '--nologo', $sourceRevisionProperty)
    Invoke-RequiredDotnet -Step 'Offline tests' -Arguments @('run', '--project', $testsProject, '-c', 'Release', '--no-build', '--no-restore')
    Invoke-RequiredDotnet -Step 'UpdateHost runtime build' -Arguments @('build', $updateHostProject, '-c', 'Release', '-r', 'win-x64', '--nologo', $sourceRevisionProperty)
    Invoke-RequiredDotnet -Step 'Portable publish' -Arguments @('publish', $appProject, '-c', 'Release', '-r', 'win-x64', '--self-contained', 'false', '-o', $portableDirectory, '--no-restore', '--nologo', $sourceRevisionProperty)
    Invoke-RequiredDotnet -Step 'MSI build' -Arguments @('build', $msiProject, '-c', 'Release', '--no-restore', '--nologo')
    Invoke-RequiredDotnet -Step 'Setup build' -Arguments @('build', $bundleProject, '-c', 'Release', '--no-restore', '--nologo')
    foreach ($artifact in @($appDll, $watcherTemplate, $msi, $setup)) {
        if (-not (Test-Path -LiteralPath $artifact -PathType Leaf)) { throw "Expected artifact is unavailable: $artifact" }
    }
    Assert-ArtifactSourceRevision -AssemblyPath $appDll -Version $releaseVersion -SourceRevision $sourceRevision
    $credential = if (Get-EmbeddedCredentialPresent -AssemblyPath $appDll) { 'PRESENT' } else { 'NOT PRESENT' }
    $appDllHash = (Get-FileHash -LiteralPath $appDll -Algorithm SHA256).Hash
    $msiHash = (Get-FileHash -LiteralPath $msi -Algorithm SHA256).Hash
    $setupHash = (Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash
    $succeeded = $true
}
catch {
    Write-Output 'BUILD CLOSED BETA: FAILED'
    Write-Output ('Version: ' + $releaseVersion)
    exit 1
}
finally {
    Clear-ClosedBetaCredentialState -State $credentialState
    Pop-Location -ErrorAction SilentlyContinue
}

if ($succeeded) {
    Write-Output 'BUILD CLOSED BETA: OK'
    Write-Output ('Version: ' + $releaseVersion)
    Write-Output ('Portable: ' + $portableDirectory)
    Write-Output ('MSI: ' + $msi)
    Write-Output ('Setup: ' + $setup)
    Write-Output ('App DLL SHA-256: ' + $appDllHash)
    Write-Output ('MSI SHA-256: ' + $msiHash)
    Write-Output ('Setup SHA-256: ' + $setupHash)
    Write-Output ('Closed Beta credential source: ' + $credentialState.Source)
    Write-Output ('Embedded beta credential: ' + $credential)
}
}
