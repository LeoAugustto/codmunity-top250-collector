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
$msi = Join-Path $projectRoot 'dist\msi\CODMunityTop250Collector-0.1.18-alpha-win-x64.msi'
$setup = Join-Path $projectRoot 'dist\setup\CODMunityTop250Collector-0.1.18-alpha-Setup.exe'
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
    $credentialState = Get-ClosedBetaCredentialState
    Invoke-RequiredDotnet -Step 'Release build' -Arguments @('build', $solution, '-c', 'Release', '--nologo')
    Invoke-RequiredDotnet -Step 'Offline tests' -Arguments @('run', '--project', $testsProject, '-c', 'Release', '--no-build', '--no-restore')
    Invoke-RequiredDotnet -Step 'UpdateHost runtime build' -Arguments @('build', $updateHostProject, '-c', 'Release', '-r', 'win-x64', '--nologo')
    Invoke-RequiredDotnet -Step 'Portable publish' -Arguments @('publish', $appProject, '-c', 'Release', '-r', 'win-x64', '--self-contained', 'false', '-o', $portableDirectory, '--no-restore', '--nologo')
    Invoke-RequiredDotnet -Step 'MSI build' -Arguments @('build', $msiProject, '-c', 'Release', '--no-restore', '--nologo')
    Invoke-RequiredDotnet -Step 'Setup build' -Arguments @('build', $bundleProject, '-c', 'Release', '--no-restore', '--nologo')
    foreach ($artifact in @($appDll, $msi, $setup)) {
        if (-not (Test-Path -LiteralPath $artifact -PathType Leaf)) { throw "Expected artifact is unavailable: $artifact" }
    }
    $credential = if (Get-EmbeddedCredentialPresent -AssemblyPath $appDll) { 'PRESENT' } else { 'NOT PRESENT' }
    $appDllHash = (Get-FileHash -LiteralPath $appDll -Algorithm SHA256).Hash
    $msiHash = (Get-FileHash -LiteralPath $msi -Algorithm SHA256).Hash
    $setupHash = (Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash
    $succeeded = $true
}
catch {
    Write-Output 'BUILD CLOSED BETA: FAILED'
    Write-Output ('Version: 0.1.18-alpha')
    exit 1
}
finally {
    Clear-ClosedBetaCredentialState -State $credentialState
    Pop-Location -ErrorAction SilentlyContinue
}

if ($succeeded) {
    Write-Output 'BUILD CLOSED BETA: OK'
    Write-Output 'Version: 0.1.18-alpha'
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
