$ErrorActionPreference = 'Stop'
$scriptPath = Join-Path $PSScriptRoot 'publish-update.ps1'
. $scriptPath -NoInvoke

function Expect-Failure {
    param([scriptblock]$Action, [string]$Message)
    try { & $Action } catch { return }
    throw $Message
}

$root = Join-Path ([IO.Path]::GetTempPath()) ('CODMunityPublishUpdateTests-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root | Out-Null
try {
    $msi = Join-Path $root 'CODMunityTop250Collector-0.1.5-alpha-win-x64.msi'
    $manifest = Join-Path $root 'manifest.json'
    $signature = Join-Path $root 'manifest.sig'
    [IO.File]::WriteAllBytes($msi, [byte[]](1,2,3))
    [IO.File]::WriteAllText($manifest, '{}')
    [IO.File]::WriteAllText($signature, 'signature')

    $plan = New-PublicationPlan -MsiPath $msi -ManifestPath $manifest -SignaturePath $signature
    $expected = @('top250/releases/CODMunityTop250Collector-0.1.5-alpha-win-x64.msi', 'top250/manifest.sig', 'top250/manifest.json')
    if (($plan.ObjectName -join '|') -ne ($expected -join '|')) { throw 'Publication order or object names are invalid.' }
    if ($plan[-1].ObjectName -ne 'top250/manifest.json') { throw 'Manifest is not last.' }
    Expect-Failure { New-PublicationPlan -MsiPath (Join-Path $root 'missing.msi') -ManifestPath $manifest -SignaturePath $signature } 'Missing file accepted.'
    $invalidMsi = Join-Path $root 'outside-top250.msi'; [IO.File]::WriteAllBytes($invalidMsi, [byte[]](1))
    Expect-Failure { New-PublicationPlan -MsiPath $invalidMsi -ManifestPath $manifest -SignaturePath $signature } 'Invalid MSI name accepted.'

    $events = [Collections.Generic.List[string]]::new()
    $common = @{
        CliLocator = { $script:events.Add('cli'); 'oci.exe' }
        ProfileValidator = { $script:events.Add('profile') }
        VerifyAction = { param($m,$manifestPath,$signaturePath) $script:events.Add('verify') }
        UploadAction = { param($oci,$file,$objectName) $script:events.Add($objectName) }
    }
    Invoke-ReleasePublication -MsiPath $msi -ManifestPath $manifest -SignaturePath $signature -WhatIfOnly @common
    if (($events -join '|') -ne 'cli|profile|verify') { throw 'DryRun invoked OCI upload.' }

    $events.Clear()
    $verifyFailure = $common.Clone(); $verifyFailure.VerifyAction = { param($m,$manifestPath,$signaturePath) $script:events.Add('verify'); throw 'verify failed' }
    Expect-Failure { Invoke-ReleasePublication -MsiPath $msi -ManifestPath $manifest -SignaturePath $signature @verifyFailure } 'Verify failure allowed upload.'
    if (($events -join '|') -ne 'cli|profile|verify') { throw 'Verify failure invoked OCI upload.' }

    $events.Clear()
    Invoke-ReleasePublication -MsiPath $msi -ManifestPath $manifest -SignaturePath $signature @common
    if (($events -join '|') -ne ('cli|profile|verify|' + ($expected -join '|'))) { throw 'Upload order is invalid.' }

    foreach ($failureObject in $expected) {
        $events.Clear(); $script:failureObject = $failureObject
        $failing = $common.Clone(); $failing.UploadAction = { param($oci,$file,$objectName) $script:events.Add($objectName); if ($objectName -eq $script:failureObject) { throw 'upload failed' } }
        Expect-Failure { Invoke-ReleasePublication -MsiPath $msi -ManifestPath $manifest -SignaturePath $signature @failing } "Upload failure was accepted for $failureObject."
        $failureIndex = [array]::IndexOf($expected, $failureObject)
        $uploadEvents = @($events | Select-Object -Skip 3)
        if ($uploadEvents.Count -ne ($failureIndex + 1) -or $uploadEvents[-1] -ne $failureObject) { throw "Upload did not stop at $failureObject." }
    }

    Write-Output 'PASS publish-update local validation, verify gate, fixed object names, strict ordering, DryRun, and upload failure handling'
}
finally {
    if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
}
