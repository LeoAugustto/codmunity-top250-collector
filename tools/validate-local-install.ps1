[CmdletBinding()]
param([Parameter(Mandatory)][string]$Version, [switch]$ReplaceAndValidate)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$setup = Join-Path $projectRoot ('dist\setup\CODMunityTop250Collector-' + $Version + '-Setup.exe')
$portable = [IO.Path]::GetFullPath((Join-Path $projectRoot 'dist\portable'))
$installedRoot = Join-Path $env:LOCALAPPDATA 'Programs\CODMunity Top 250 Collector'

function Assert-ExitCode { param([System.Diagnostics.Process]$Process, [string]$Step) if ($Process.ExitCode -notin @(0, 3010)) { throw ($Step + ' failed (exit code ' + $Process.ExitCode + ').') } }
function Test-InstalledPayload {
    param([string]$PortableRoot, [string]$InstallRoot)
    $portableFiles = Get-ChildItem -LiteralPath $PortableRoot -File -Recurse
    if ($portableFiles.Count -eq 0) { throw 'Portable payload is empty.' }
    foreach ($portableFile in $portableFiles) {
        $relative = $portableFile.FullName.Substring($PortableRoot.Length).TrimStart('\')
        $installedFile = Join-Path $InstallRoot $relative
        if (-not (Test-Path -LiteralPath $installedFile -PathType Leaf)) { throw ('Installed payload is missing: ' + $relative) }
        $expected = (Get-FileHash -LiteralPath $portableFile.FullName -Algorithm SHA256).Hash
        $actual = (Get-FileHash -LiteralPath $installedFile -Algorithm SHA256).Hash
        if ($expected -ne $actual) { throw ('Installed payload hash mismatch: ' + $relative) }
    }
}

if (-not $ReplaceAndValidate) { throw 'This helper is destructive by design. Re-run with -ReplaceAndValidate after closing the Collector.' }
if (-not (Test-Path -LiteralPath $setup -PathType Leaf)) { throw ('Setup is unavailable: ' + $setup) }
if (-not (Test-Path -LiteralPath $portable -PathType Container)) { throw ('Portable payload is unavailable: ' + $portable) }
if (Get-Process -Name 'CODMunityTop250Collector' -ErrorAction SilentlyContinue) { throw 'Close CODMunity Top 250 Collector before same-version validation.' }

# Uses registered Burn uninstall; avoids in-place same-version MSI repair that can preserve equal-version files.
$uninstall = Start-Process -FilePath $setup -ArgumentList '-uninstall' -Wait -PassThru
Assert-ExitCode -Process $uninstall -Step 'Clean Setup uninstall'
$install = Start-Process -FilePath $setup -Wait -PassThru
Assert-ExitCode -Process $install -Step 'Interactive Setup install'
Test-InstalledPayload -PortableRoot $portable -InstallRoot $installedRoot
Write-Output 'LOCAL SAME-VERSION VALIDATION: OK'
