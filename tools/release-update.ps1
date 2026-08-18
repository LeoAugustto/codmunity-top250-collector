[CmdletBinding()]
param(
    [string]$Version,
    [string]$ReleaseNotes,
    [string]$MinimumUpdaterVersion,
    [switch]$NoInvoke
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$script:ReleaseUpdateNoInvoke = [bool]$NoInvoke

$script:ProjectRoot = Split-Path -Parent $PSScriptRoot
$script:Top250Root = Split-Path -Parent (Split-Path -Parent $script:ProjectRoot)
$script:ReleaseOutputRoot = Join-Path $script:Top250Root 'Releases'
$script:PrivateKeyPath = Join-Path $script:Top250Root 'Private\Updater\codmunity-top250-release-private.pem'
$script:PublicKeyPath = Join-Path $script:ProjectRoot 'CODMunityTop250Collector.App\Updater\codmunity-top250-release-public.pem'
$script:ReleaseToolProject = Join-Path $script:ProjectRoot 'CODMunityTop250Collector.ReleaseTool\CODMunityTop250Collector.ReleaseTool.csproj'
$script:PublishScript = Join-Path $PSScriptRoot 'publish-update.ps1'
$script:OciReleaseBase = 'https://gr5favu0uzxf.objectstorage.sa-saopaulo-1.oci.customer-oci.com/n/gr5favu0uzxf/b/codmunity-updates/o/top250%2Freleases%2F'
$script:AlphaVersionPattern = '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)-alpha$'

# Reuse the fixed OCI target, verify gate, and commit-last upload ordering.
. $script:PublishScript -NoInvoke

function Assert-AlphaVersion {
    param([Parameter(Mandatory)][string]$Value, [Parameter(Mandatory)][string]$Name)

    if ($Value -notmatch $script:AlphaVersionPattern) {
        throw "$Name must be in X.Y.Z-alpha format."
    }
}

function ConvertTo-MsiVersion {
    param([Parameter(Mandatory)][string]$AlphaVersion)

    Assert-AlphaVersion -Value $AlphaVersion -Name 'Version'
    return $AlphaVersion.Substring(0, $AlphaVersion.Length - '-alpha'.Length)
}

function Compare-AlphaVersions {
    param([Parameter(Mandatory)][string]$Left, [Parameter(Mandatory)][string]$Right)

    Assert-AlphaVersion -Value $Left -Name 'Version'
    Assert-AlphaVersion -Value $Right -Name 'Version'
    $leftNumbers = $Left.Substring(0, $Left.Length - '-alpha'.Length).Split('.') | ForEach-Object { [uint64]$_ }
    $rightNumbers = $Right.Substring(0, $Right.Length - '-alpha'.Length).Split('.') | ForEach-Object { [uint64]$_ }
    for ($index = 0; $index -lt 3; $index++) {
        if ($leftNumbers[$index] -lt $rightNumbers[$index]) { return -1 }
        if ($leftNumbers[$index] -gt $rightNumbers[$index]) { return 1 }
    }
    return 0
}

function New-ReleaseUpdateContext {
    param(
        [Parameter(Mandatory)][string]$TargetVersion,
        [Parameter(Mandatory)][string]$TargetReleaseNotes,
        [string]$TargetMinimumUpdaterVersion
    )

    Assert-AlphaVersion -Value $TargetVersion -Name 'Version'
    if ([string]::IsNullOrWhiteSpace($TargetReleaseNotes)) {
        throw 'ReleaseNotes is required.'
    }

    # There is no local authoritative source for the currently published version.
    # Do not infer a minimum version from filenames or contact OCI: require the
    # release operator to state it explicitly whenever that value is ambiguous.
    if ([string]::IsNullOrWhiteSpace($TargetMinimumUpdaterVersion)) {
        throw 'MinimumUpdaterVersion is required because the currently published updater version cannot be determined locally with confidence.'
    }
    Assert-AlphaVersion -Value $TargetMinimumUpdaterVersion -Name 'MinimumUpdaterVersion'
    if ((Compare-AlphaVersions -Left $TargetMinimumUpdaterVersion -Right $TargetVersion) -ge 0) {
        throw 'MinimumUpdaterVersion must be older than Version.'
    }

    $msiVersion = ConvertTo-MsiVersion -AlphaVersion $TargetVersion
    $msiName = "CODMunityTop250Collector-$TargetVersion-win-x64.msi"
    $msiPath = Join-Path $script:ProjectRoot (Join-Path 'dist\msi' $msiName)
    $releaseDirectory = Join-Path $script:ReleaseOutputRoot $TargetVersion

    return [pscustomobject]@{
        Version = $TargetVersion
        ReleaseNotes = $TargetReleaseNotes
        MinimumUpdaterVersion = $TargetMinimumUpdaterVersion
        MsiVersion = $msiVersion
        MsiName = $msiName
        MsiPath = $msiPath
        ReleaseDirectory = $releaseDirectory
        ManifestPath = Join-Path $releaseDirectory 'manifest.json'
        SignaturePath = Join-Path $releaseDirectory 'manifest.sig'
        Url = $script:OciReleaseBase + $msiName
    }
}

function Assert-ReleaseUpdatePrechecks {
    param(
        [Parameter(Mandatory)]$Context,
        [Parameter(Mandatory)][string]$PrivateKey,
        [Parameter(Mandatory)][string]$PublicKey,
        [Parameter(Mandatory)][string]$ReleaseToolProjectPath,
        [Parameter(Mandatory)][string]$PublishScriptPath,
        [Parameter(Mandatory)][scriptblock]$DotnetLocator,
        [Parameter(Mandatory)][scriptblock]$OciCliLocator,
        [Parameter(Mandatory)][scriptblock]$OciProfileValidator
    )

    foreach ($file in @($Context.MsiPath, $PublicKey, $ReleaseToolProjectPath, $PublishScriptPath)) {
        if (-not (Test-Path -LiteralPath $file -PathType Leaf)) {
            throw 'A required local release file is unavailable.'
        }
    }
    # Deliberately checks only existence. The ReleaseTool alone imports/decrypts it.
    if (-not (Test-Path -LiteralPath $PrivateKey -PathType Leaf)) {
        throw 'The fixed private signing key file is unavailable.'
    }
    if ((Split-Path -Leaf $Context.MsiPath) -ne $Context.MsiName) {
        throw 'MSI filename does not match the requested release version.'
    }
    if ((Test-Path -LiteralPath $Context.ManifestPath -PathType Leaf) -or (Test-Path -LiteralPath $Context.SignaturePath -PathType Leaf)) {
        throw 'Release output already contains manifest.json or manifest.sig; refusing to overwrite signed release metadata.'
    }

    $dotnetPath = & $DotnetLocator
    if ([string]::IsNullOrWhiteSpace($dotnetPath)) { throw 'dotnet CLI was not found.' }
    $ociPath = & $OciCliLocator
    if ([string]::IsNullOrWhiteSpace($ociPath)) { throw 'OCI CLI was not found.' }
    & $OciProfileValidator
    return [pscustomobject]@{ DotnetPath = $dotnetPath; OciPath = $ociPath }
}

function Invoke-ReleaseToolCreateManifest {
    param([Parameter(Mandatory)]$Context, [Parameter(Mandatory)][string]$DotnetPath, [string]$PrivateKey = $script:PrivateKeyPath)

    $arguments = @(
        'run', '--project', $script:ReleaseToolProject, '-c', 'Release', '--', 'create-manifest',
        '--msi', $Context.MsiPath,
        '--version', $Context.Version,
        '--msi-version', $Context.MsiVersion,
        '--minimum-updater-version', $Context.MinimumUpdaterVersion,
        '--url', $Context.Url,
        '--release-notes', $Context.ReleaseNotes,
        '--private-key', $PrivateKey,
        '--out', $Context.ReleaseDirectory
    )
    # ReleaseTool prompts for the encrypted PKCS#8 passphrase directly once.
    & $DotnetPath @arguments
    if ($LASTEXITCODE -ne 0) { throw "ReleaseTool create-manifest failed with exit code $LASTEXITCODE." }
}

function Invoke-ReleaseUpdate {
    param(
        [Parameter(Mandatory)][string]$TargetVersion,
        [Parameter(Mandatory)][string]$TargetReleaseNotes,
        [string]$TargetMinimumUpdaterVersion,
        [scriptblock]$DotnetLocator = { (Get-Command -Name 'dotnet' -CommandType Application -ErrorAction Stop).Source },
        [scriptblock]$OciCliLocator = { Find-OciCli },
        [scriptblock]$OciProfileValidator = { Assert-OciReleaseProfile },
        [scriptblock]$CreateManifestAction = { param($context, $dotnet, $key) Invoke-ReleaseToolCreateManifest -Context $context -DotnetPath $dotnet -PrivateKey $key },
        [scriptblock]$VerifyAction = { param($context) Invoke-ReleaseToolVerify -MsiPath $context.MsiPath -ManifestPath $context.ManifestPath -SignaturePath $context.SignaturePath },
        [scriptblock]$PublishAction = { param($context, $dryRun) Invoke-ReleasePublication -MsiPath $context.MsiPath -ManifestPath $context.ManifestPath -SignaturePath $context.SignaturePath -WhatIfOnly:$dryRun }
    )

    $context = New-ReleaseUpdateContext -TargetVersion $TargetVersion -TargetReleaseNotes $TargetReleaseNotes -TargetMinimumUpdaterVersion $TargetMinimumUpdaterVersion
    $tools = Assert-ReleaseUpdatePrechecks -Context $context -PrivateKey $script:PrivateKeyPath -PublicKey $script:PublicKeyPath -ReleaseToolProjectPath $script:ReleaseToolProject -PublishScriptPath $script:PublishScript -DotnetLocator $DotnetLocator -OciCliLocator $OciCliLocator -OciProfileValidator $OciProfileValidator

    New-Item -ItemType Directory -Path $context.ReleaseDirectory -Force | Out-Null
    & $CreateManifestAction $context $tools.DotnetPath $script:PrivateKeyPath
    & $VerifyAction $context
    & $PublishAction $context $true
    & $PublishAction $context $false

    $msiHash = (Get-FileHash -LiteralPath $context.MsiPath -Algorithm SHA256).Hash
    Write-Host 'RELEASE PUBLISHED'
    Write-Host "Version: $($context.Version)"
    Write-Host "MSI: $($context.MsiPath)"
    Write-Host "SHA-256: $msiHash"
    Write-Host "Manifest: $($context.ManifestPath)"
    Write-Host "Signature: $($context.SignaturePath)"
    Write-Host 'OCI: published'
    Write-Host 'Order: MSI -> manifest.sig -> manifest.json'
}

if (-not $script:ReleaseUpdateNoInvoke) {
    try {
        if ([string]::IsNullOrWhiteSpace($Version) -or [string]::IsNullOrWhiteSpace($ReleaseNotes)) {
            throw 'Usage: .\tools\release-update.ps1 -Version X.Y.Z-alpha -ReleaseNotes <text> -MinimumUpdaterVersion X.Y.Z-alpha'
        }
        Invoke-ReleaseUpdate -TargetVersion $Version -TargetReleaseNotes $ReleaseNotes -TargetMinimumUpdaterVersion $MinimumUpdaterVersion
        exit 0
    }
    catch {
        Write-Error $_.Exception.Message
        exit 1
    }
}
