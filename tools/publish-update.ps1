[CmdletBinding()]
param(
    [string]$Msi,
    [string]$Manifest,
    [string]$Signature,
    [switch]$DryRun,
    [switch]$NoInvoke
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# These are deliberately fixed. A release publisher cannot select another OCI target.
$script:OciProfile = 'CODMUNITY_RELEASE'
$script:OciNamespace = 'gr5favu0uzxf'
$script:OciBucket = 'codmunity-updates'
$script:MsiObjectPrefix = 'top250/releases/'
$script:ManifestObjectName = 'top250/manifest.json'
$script:SignatureObjectName = 'top250/manifest.sig'
$script:MsiFileNamePattern = '^CODMunityTop250Collector-\d+\.\d+\.\d+-alpha-win-x64\.msi$'

function Find-OciCli {
    $command = Get-Command -Name 'oci' -CommandType Application -ErrorAction Stop
    return $command.Source
}

function Assert-OciReleaseProfile {
    $configPath = if (-not [string]::IsNullOrWhiteSpace($env:OCI_CLI_CONFIG_FILE)) {
        $env:OCI_CLI_CONFIG_FILE
    }
    else {
        Join-Path $env:USERPROFILE '.oci\config'
    }

    if (-not (Test-Path -LiteralPath $configPath -PathType Leaf)) {
        throw "OCI profile '$script:OciProfile' was not found because the OCI config file is unavailable."
    }

    $config = Get-Content -LiteralPath $configPath -Raw
    if ($config -notmatch ('(?m)^\[' + [regex]::Escape($script:OciProfile) + '\]\s*$')) {
        throw "OCI profile '$script:OciProfile' was not found in the OCI config."
    }
}

function Assert-InputFiles {
    param([string]$MsiPath, [string]$ManifestPath, [string]$SignaturePath)

    foreach ($path in @($MsiPath, $ManifestPath, $SignaturePath)) {
        if ([string]::IsNullOrWhiteSpace($path) -or -not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "Required file is unavailable: $path"
        }
    }
}

function New-PublicationPlan {
    param([string]$MsiPath, [string]$ManifestPath, [string]$SignaturePath)

    Assert-InputFiles -MsiPath $MsiPath -ManifestPath $ManifestPath -SignaturePath $SignaturePath
    $msiName = Split-Path -Leaf $MsiPath
    if ($msiName -notmatch $script:MsiFileNamePattern) {
        throw "MSI filename is invalid. Expected CODMunityTop250Collector-X.Y.Z-alpha-win-x64.msi."
    }

    # manifest.json is the release announcement/commit, so it is intentionally last.
    return @(
        [pscustomobject]@{ File = (Resolve-Path -LiteralPath $MsiPath).Path; ObjectName = $script:MsiObjectPrefix + $msiName; Label = 'MSI' },
        [pscustomobject]@{ File = (Resolve-Path -LiteralPath $SignaturePath).Path; ObjectName = $script:SignatureObjectName; Label = 'Signature' },
        [pscustomobject]@{ File = (Resolve-Path -LiteralPath $ManifestPath).Path; ObjectName = $script:ManifestObjectName; Label = 'Manifest' }
    )
}

function Invoke-ReleaseToolVerify {
    param([string]$MsiPath, [string]$ManifestPath, [string]$SignaturePath)

    $projectRoot = Split-Path -Parent $PSScriptRoot
    $releaseToolProject = Join-Path $projectRoot 'CODMunityTop250Collector.ReleaseTool\CODMunityTop250Collector.ReleaseTool.csproj'
    $publicKey = Join-Path $projectRoot 'CODMunityTop250Collector.App\Updater\codmunity-top250-release-public.pem'
    if (-not (Test-Path -LiteralPath $releaseToolProject -PathType Leaf) -or -not (Test-Path -LiteralPath $publicKey -PathType Leaf)) {
        throw 'ReleaseTool project or distributed public key is unavailable.'
    }

    $dotnet = (Get-Command -Name 'dotnet' -CommandType Application -ErrorAction Stop).Source
    $arguments = @('run', '--project', $releaseToolProject, '-c', 'Release', '--', 'verify', '--manifest', $ManifestPath, '--signature', $SignaturePath, '--public-key', $publicKey, '--msi', $MsiPath)
    & $dotnet @arguments
    if ($LASTEXITCODE -ne 0) { throw "ReleaseTool verify failed with exit code $LASTEXITCODE." }
}

function Invoke-OciObjectPut {
    param([string]$OciPath, [string]$FilePath, [string]$ObjectName)

    $arguments = @('os', 'object', 'put', '--namespace-name', $script:OciNamespace, '--bucket-name', $script:OciBucket, '--profile', $script:OciProfile, '--name', $ObjectName, '--file', $FilePath, '--force')
    & $OciPath @arguments
    if ($LASTEXITCODE -ne 0) { throw "OCI upload failed for $ObjectName with exit code $LASTEXITCODE." }
}

function Invoke-ReleasePublication {
    param(
        [Parameter(Mandatory)][string]$MsiPath,
        [Parameter(Mandatory)][string]$ManifestPath,
        [Parameter(Mandatory)][string]$SignaturePath,
        [switch]$WhatIfOnly,
        [scriptblock]$CliLocator = { Find-OciCli },
        [scriptblock]$ProfileValidator = { Assert-OciReleaseProfile },
        [scriptblock]$VerifyAction = { param($m, $manifest, $signature) Invoke-ReleaseToolVerify -MsiPath $m -ManifestPath $manifest -SignaturePath $signature },
        [scriptblock]$UploadAction = { param($oci, $file, $name) Invoke-OciObjectPut -OciPath $oci -FilePath $file -ObjectName $name }
    )

    $ociPath = & $CliLocator
    if ([string]::IsNullOrWhiteSpace($ociPath)) { throw 'OCI CLI was not found.' }
    & $ProfileValidator
    Assert-InputFiles -MsiPath $MsiPath -ManifestPath $ManifestPath -SignaturePath $SignaturePath
    & $VerifyAction $MsiPath $ManifestPath $SignaturePath
    Write-Host 'LOCAL VERIFY: OK'
    Assert-InputFiles -MsiPath $MsiPath -ManifestPath $ManifestPath -SignaturePath $SignaturePath
    $plan = New-PublicationPlan -MsiPath $MsiPath -ManifestPath $ManifestPath -SignaturePath $SignaturePath

    if ($WhatIfOnly) {
        Write-Host 'Would publish:'
        for ($index = 0; $index -lt $plan.Count; $index++) { Write-Host ("{0}. {1}" -f ($index + 1), $plan[$index].ObjectName) }
        Write-Host 'DRY RUN COMPLETE - no files uploaded.'
        return
    }

    $completed = @()
    foreach ($item in $plan) {
        try {
            & $UploadAction $ociPath $item.File $item.ObjectName
            $completed += $item.ObjectName
        }
        catch {
            if ($item.ObjectName -eq $script:ManifestObjectName) {
                Write-Error 'Manifest upload failed. The release was not announced completely; investigate manifest publication before retrying.'
            }
            elseif ($completed.Count -gt 0) {
                Write-Error 'A partial upload occurred, but manifest.json was not published. The release is not announced.'
            }
            throw
        }
    }
}

if (-not $NoInvoke) {
    try {
        if ([string]::IsNullOrWhiteSpace($Msi) -or [string]::IsNullOrWhiteSpace($Manifest) -or [string]::IsNullOrWhiteSpace($Signature)) {
            throw 'Usage: .\tools\publish-update.ps1 -Msi <path> -Manifest <path> -Signature <path> [-DryRun]'
        }
        Invoke-ReleasePublication -MsiPath $Msi -ManifestPath $Manifest -SignaturePath $Signature -WhatIfOnly:$DryRun
        exit 0
    }
    catch {
        Write-Error $_.Exception.Message
        exit 1
    }
}
