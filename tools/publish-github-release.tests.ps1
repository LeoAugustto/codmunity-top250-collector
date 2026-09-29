$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'publish-github-release.ps1') -NoInvoke

Assert-GitHubAlphaVersion -Value '0.1.19-alpha'
try { Assert-GitHubAlphaVersion -Value '0.1.19'; throw 'Invalid version was accepted.' } catch { if ($_.Exception.Message -eq 'Invalid version was accepted.') { throw } }

$context = Get-ReleaseContext -TargetVersion '0.1.19-alpha'
if ($context.Tag -ne 'v0.1.19-alpha') { throw 'Tag derivation failed.' }
if ((Split-Path -Leaf $context.Msi) -ne 'CODMunityTop250Collector-0.1.19-alpha-win-x64.msi') { throw 'MSI naming failed.' }
if ((Split-Path -Leaf $context.Setup) -ne 'CODMunityTop250Collector-0.1.19-alpha-Setup.exe') { throw 'Setup naming failed.' }
if ((Split-Path -Leaf $context.PortableZip) -ne 'CODMunityTop250Collector-0.1.19-alpha-portable.zip') { throw 'Portable ZIP naming failed.' }
if ($script:Repository -ne 'LeoAugustto/codmunity-top250-collector') { throw 'Repository constant changed.' }
if ($script:RemoteUrl -ne 'https://github.com/LeoAugustto/codmunity-top250-collector.git') { throw 'Remote constant changed.' }

$fixtureRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("codmunity-gh-release-view-$([guid]::NewGuid().ToString('N'))")
New-Item -ItemType Directory -Path $fixtureRoot -Force | Out-Null
try {
    $missingReleaseGh = Join-Path $fixtureRoot 'gh-release-not-found.cmd'
    [System.IO.File]::WriteAllText($missingReleaseGh, "@echo off`r`necho release not found 1>&2`r`nexit /b 1`r`n", [System.Text.ASCIIEncoding]::new())
    if (Test-GitHubReleaseExists -GhPath $missingReleaseGh -Tag 'v0.1.19-alpha') {
        throw 'A missing GitHub Release was not classified as creatable.'
    }

    $unexpectedFailureGh = Join-Path $fixtureRoot 'gh-unexpected-failure.cmd'
    [System.IO.File]::WriteAllText($unexpectedFailureGh, "@echo off`r`necho authentication failed 1>&2`r`nexit /b 1`r`n", [System.Text.ASCIIEncoding]::new())
    try {
        [void](Test-GitHubReleaseExists -GhPath $unexpectedFailureGh -Tag 'v0.1.19-alpha')
        throw 'An unexpected GitHub CLI failure was accepted.'
    }
    catch {
        if ($_.Exception.Message -eq 'An unexpected GitHub CLI failure was accepted.') { throw }
    }
}
finally {
    Remove-Item -LiteralPath $fixtureRoot -Recurse -Force -ErrorAction SilentlyContinue
}
Write-Output 'publish-github-release tests passed.'
