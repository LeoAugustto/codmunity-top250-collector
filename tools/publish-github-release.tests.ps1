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
Write-Output 'publish-github-release tests passed.'
