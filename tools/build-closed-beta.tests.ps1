$ErrorActionPreference = 'Stop'
$scriptPath = Join-Path $PSScriptRoot 'build-closed-beta.ps1'

# The caller must supply an isolated environment; this test uses synthetic credentials only.
. $scriptPath -NoInvoke

if (-not [string]::IsNullOrWhiteSpace($env:CODMUNITY_RANKED_SYNC_TOKEN_BUILD)) { throw 'Test requires an isolated environment without a build credential.' }
try {
    $env:CODMUNITY_RANKED_SYNC_TOKEN_BUILD = 'synthetic-inherited-token'
    $promptCalled = $false
    $inherited = Get-ClosedBetaCredentialState -PromptProvider { $script:promptCalled = $true; throw 'Prompt must not be called for an inherited credential.' }
    if ($inherited.Source -ne 'environment' -or $inherited.Temporary -or $promptCalled) { throw 'Inherited credential path is invalid.' }
    Clear-ClosedBetaCredentialState -State $inherited
    if ($env:CODMUNITY_RANKED_SYNC_TOKEN_BUILD -ne 'synthetic-inherited-token') { throw 'Inherited credential was removed.' }

    Remove-Item Env:CODMUNITY_RANKED_SYNC_TOKEN_BUILD -ErrorAction SilentlyContinue
    $prompt = { ConvertTo-SecureString 'synthetic-prompt-token' -AsPlainText -Force }
    $temporary = Get-ClosedBetaCredentialState -PromptProvider $prompt
    if ($temporary.Source -ne 'prompt' -or -not $temporary.Temporary -or [string]::IsNullOrWhiteSpace($env:CODMUNITY_RANKED_SYNC_TOKEN_BUILD)) { throw 'Prompt credential path is invalid.' }
    Clear-ClosedBetaCredentialState -State $temporary
    if (-not [string]::IsNullOrWhiteSpace($env:CODMUNITY_RANKED_SYNC_TOKEN_BUILD)) { throw 'Temporary credential was not removed.' }

    $failureState = Get-ClosedBetaCredentialState -PromptProvider $prompt
    try { throw 'synthetic build failure' }
    catch { }
    finally { Clear-ClosedBetaCredentialState -State $failureState }
    if (-not [string]::IsNullOrWhiteSpace($env:CODMUNITY_RANKED_SYNC_TOKEN_BUILD)) { throw 'Temporary credential survived a build failure.' }

    $source = Get-Content -LiteralPath $scriptPath -Raw
    if ($source -match 'Write-(Host|Output|Error).*CODMUNITY_RANKED_SYNC_TOKEN_BUILD' -or $source -match 'Get-ChildItem\s+Env:|\bprintenv\b|\bset\s*$') { throw 'Build helper could expose environment secrets.' }
    if (-not $source.Contains('Get-CurrentSourceRevision') -or -not $source.Contains('git -C $projectRoot diff --quiet') -or -not $source.Contains('SourceRevisionId=') -or -not $source.Contains('Assert-ArtifactSourceRevision')) { throw 'Build helper does not guard the published source revision.' }
    Write-Output 'PASS build-closed-beta inherited environment credential, secure prompt fallback, cleanup, secret-safe output, and source-revision artifact guard'
}
finally {
    Remove-Item Env:CODMUNITY_RANKED_SYNC_TOKEN_BUILD -ErrorAction SilentlyContinue
}
