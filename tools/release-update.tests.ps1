$ErrorActionPreference = 'Stop'
$scriptPath = Join-Path $PSScriptRoot 'release-update.ps1'
. $scriptPath -NoInvoke

function Expect-Failure {
    param([scriptblock]$Action, [string]$Message)
    try { & $Action } catch { return }
    throw $Message
}

$root = Join-Path ([IO.Path]::GetTempPath()) ('CODMunityReleaseUpdateTests-' + [guid]::NewGuid().ToString('N'))
$originalProjectRoot = $script:ProjectRoot
$originalReleaseOutputRoot = $script:ReleaseOutputRoot
$originalPrivateKeyPath = $script:PrivateKeyPath
$originalPublicKeyPath = $script:PublicKeyPath
$originalReleaseToolProject = $script:ReleaseToolProject
$originalPublishScript = $script:PublishScript
New-Item -ItemType Directory -Path $root | Out-Null
try {
    $powershell = (Get-Command -Name 'powershell.exe' -CommandType Application -ErrorAction Stop).Source
    $savedErrorActionPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    $normalOutput = & $powershell -NoProfile -ExecutionPolicy Bypass -File $scriptPath -Version '0.1.8-alpha' -ReleaseNotes 'No private key access test.' 2>&1
    $normalExitCode = $LASTEXITCODE
    $noInvokeOutput = & $powershell -NoProfile -ExecutionPolicy Bypass -File $scriptPath -NoInvoke 2>&1
    $noInvokeExitCode = $LASTEXITCODE
    $ErrorActionPreference = $savedErrorActionPreference
    if ($normalExitCode -eq 0 -or ($normalOutput -join "`n") -notmatch 'MinimumUpdaterVersion is required') { throw 'Normal wrapper invocation did not enter the main workflow.' }
    if ($noInvokeExitCode -ne 0 -or -not [string]::IsNullOrWhiteSpace(($noInvokeOutput -join "`n"))) { throw 'NoInvoke wrapper invocation executed release work.' }

    $msiName = 'CODMunityTop250Collector-0.1.8-alpha-win-x64.msi'
    $distMsi = Join-Path $root 'dist\msi'
    New-Item -ItemType Directory -Path $distMsi | Out-Null
    $msi = Join-Path $distMsi $msiName
    $publicKey = Join-Path $root 'public.pem'
    $privatePlaceholder = Join-Path $root 'private-placeholder.pem'
    $releaseToolProject = Join-Path $root 'ReleaseTool.csproj'
    $publishScript = Join-Path $root 'publish-update.ps1'
    [IO.File]::WriteAllBytes($msi, [byte[]](1,2,3))
    [IO.File]::WriteAllText($publicKey, 'public placeholder')
    [IO.File]::WriteAllText($privatePlaceholder, 'not a real key')
    [IO.File]::WriteAllText($releaseToolProject, '<Project />')
    [IO.File]::WriteAllText($publishScript, '# placeholder')

    $script:ProjectRoot = $root
    $script:ReleaseOutputRoot = Join-Path $root 'Releases'
    $script:PrivateKeyPath = $privatePlaceholder
    $script:PublicKeyPath = $publicKey
    $script:ReleaseToolProject = $releaseToolProject
    $script:PublishScript = $publishScript
    $workflowReleaseDirectory = Join-Path $script:ReleaseOutputRoot '0.1.8-alpha'

    $wrapperSource = Get-Content -LiteralPath $scriptPath -Raw
    if ($wrapperSource -match 'Get-Content\s+-LiteralPath\s+\$PrivateKey' -or $wrapperSource -match 'Write-(Host|Output|Error).*\$PrivateKey') {
        throw 'Wrapper attempts to read or print private key material.'
    }

    $context = New-ReleaseUpdateContext -TargetVersion '0.1.8-alpha' -TargetReleaseNotes 'Notes with spaces.' -TargetMinimumUpdaterVersion '0.1.7-alpha'
    if ($context.MsiVersion -ne '0.1.8' -or $context.MsiName -ne $msiName -or -not $context.Url.EndsWith($msiName, [StringComparison]::Ordinal)) { throw 'Version derivation is invalid.' }
    Expect-Failure { New-ReleaseUpdateContext -TargetVersion '0.1.8' -TargetReleaseNotes 'x' -TargetMinimumUpdaterVersion '0.1.7-alpha' } 'Invalid release version was accepted.'
    Expect-Failure { New-ReleaseUpdateContext -TargetVersion '0.1.8-alpha' -TargetReleaseNotes 'x' } 'Ambiguous minimum updater version was accepted.'
    Expect-Failure { New-ReleaseUpdateContext -TargetVersion '0.1.8-alpha' -TargetReleaseNotes 'x' -TargetMinimumUpdaterVersion '0.1.8-alpha' } 'Current minimum updater version was accepted.'

    $events = [Collections.Generic.List[string]]::new()
    $customContext = [pscustomobject]@{ Version = '0.1.8-alpha'; ReleaseNotes = 'Notes with spaces.'; MinimumUpdaterVersion = '0.1.7-alpha'; MsiVersion = '0.1.8'; MsiName = $msiName; MsiPath = $msi; ReleaseDirectory = (Join-Path $root 'release'); ManifestPath = (Join-Path $root 'release\manifest.json'); SignaturePath = (Join-Path $root 'release\manifest.sig'); Url = $script:OciReleaseBase + $msiName }
    $prechecks = @{ PrivateKey = $privatePlaceholder; PublicKey = $publicKey; ReleaseToolProjectPath = $releaseToolProject; PublishScriptPath = $publishScript; DotnetLocator = { 'dotnet.exe' }; OciCliLocator = { 'oci.exe' }; OciProfileValidator = { $script:events.Add('profile') } }
    $tools = Assert-ReleaseUpdatePrechecks -Context $customContext @prechecks
    if ($tools.DotnetPath -ne 'dotnet.exe' -or ($events -join '|') -ne 'profile') { throw 'Prechecks did not use injected local mocks.' }
    $missingMsiContext = $customContext | Select-Object *
    $missingMsiContext.MsiPath = Join-Path $root 'missing.msi'
    Expect-Failure { Assert-ReleaseUpdatePrechecks -Context $missingMsiContext @prechecks } 'Missing MSI passed prechecks.'
    Expect-Failure { Assert-ReleaseUpdatePrechecks -Context ($customContext | Select-Object *) -PrivateKey (Join-Path $root 'missing.pem') -PublicKey $publicKey -ReleaseToolProjectPath $releaseToolProject -PublishScriptPath $publishScript -DotnetLocator { 'dotnet.exe' } -OciCliLocator { 'oci.exe' } -OciProfileValidator {} } 'Missing key placeholder passed prechecks.'

    function Invoke-MockWorkflow {
        param([scriptblock]$Create, [scriptblock]$Verify, [scriptblock]$Publish)
        Invoke-ReleaseUpdate -TargetVersion '0.1.8-alpha' -TargetReleaseNotes 'Notes with spaces.' -TargetMinimumUpdaterVersion '0.1.7-alpha' -DotnetLocator { 'dotnet.exe' } -OciCliLocator { 'oci.exe' } -OciProfileValidator { $script:events.Add('profile') } -CreateManifestAction $Create -VerifyAction $Verify -PublishAction $Publish -GitHubPrepareAction { param($context) $script:events.Add('git-prepare') } -GitHubMirrorAction { param($context) $script:events.Add('github-mirror') }
    }

    $events.Clear()
    $create = { param($c,$dotnet,$key) if ($c.ReleaseNotes -ne 'Notes with spaces.') { throw 'Release notes were not preserved.' }; $script:events.Add('create'); [IO.File]::WriteAllText($c.ManifestPath, '{}'); [IO.File]::WriteAllText($c.SignaturePath, 'sig') }
    $verify = { param($c) $script:events.Add('verify') }
    $publish = { param($c,$dry) if ($dry) { $script:events.Add('dry-run') } else { $script:events.Add('publish') } }
    Invoke-MockWorkflow -Create $create -Verify $verify -Publish $publish 6>$null
    if (($events -join '|') -ne 'profile|git-prepare|create|verify|dry-run|publish|github-mirror') { throw 'Release sequence is invalid.' }

    Remove-Item -LiteralPath $workflowReleaseDirectory -Recurse -Force
    $events.Clear(); $createFailure = { param($c,$dotnet,$key) $script:events.Add('create'); throw 'create failed' }
    Expect-Failure { Invoke-MockWorkflow -Create $createFailure -Verify $verify -Publish $publish } 'Create failure continued.'
    if (($events -join '|') -ne 'profile|git-prepare|create') { throw 'Create failure reached later stages.' }

    Remove-Item -LiteralPath $workflowReleaseDirectory -Recurse -Force -ErrorAction SilentlyContinue
    $events.Clear(); $verifyFailure = { param($c) $script:events.Add('verify'); throw 'verify failed' }
    Expect-Failure { Invoke-MockWorkflow -Create $create -Verify $verifyFailure -Publish $publish } 'Verify failure continued.'
    if (($events -join '|') -ne 'profile|git-prepare|create|verify') { throw 'Verify failure reached publish.' }

    Remove-Item -LiteralPath $workflowReleaseDirectory -Recurse -Force -ErrorAction SilentlyContinue
    $events.Clear(); $dryFailure = { param($c,$dry) if ($dry) { $script:events.Add('dry-run'); throw 'dry run failed' } else { $script:events.Add('publish') } }
    Expect-Failure { Invoke-MockWorkflow -Create $create -Verify $verify -Publish $dryFailure } 'DryRun failure continued.'
    if (($events -join '|') -ne 'profile|git-prepare|create|verify|dry-run') { throw 'DryRun failure reached real publish.' }

    Write-Output 'PASS release-update local prechecks, version derivation, explicit minimum, create/verify/DryRun/publish gating, and secret-free mocks'
}
finally {
    $script:ProjectRoot = $originalProjectRoot
    $script:ReleaseOutputRoot = $originalReleaseOutputRoot
    $script:PrivateKeyPath = $originalPrivateKeyPath
    $script:PublicKeyPath = $originalPublicKeyPath
    $script:ReleaseToolProject = $originalReleaseToolProject
    $script:PublishScript = $originalPublishScript
    if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
}
