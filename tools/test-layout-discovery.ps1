param([string]$EngineRoot = (Join-Path (Split-Path -Parent $PSScriptRoot) 'engine'))

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $EngineRoot 'layout_discovery.psm1') -Force
function Assert-That([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }

$sha = '97413B95AA94A9694690BA70B16189E4827E2B363850F873387E8A88101D4FE4'
$anchors = @{
    'mp/stateRva' = [Int64]0x12895050; 'mp/countRva' = [Int64]0x128D079C; 'mp/entry0Rva' = [Int64]0x128D07A0
    'br/stateRva' = [Int64]0x12917140; 'br/countRva' = [Int64]0x1295288C; 'br/entry0Rva' = [Int64]0x12952890
    'resurgence/stateRva' = [Int64]0x12999230; 'resurgence/countRva' = [Int64]0x129D497C; 'resurgence/entry0Rva' = [Int64]0x129D4980
}
$definition = New-BnetLayoutDefinitionFromAnchors -Anchors $anchors -Sha256 $sha -MethodVersion 'fixture-v1'
Assert-That (Test-BnetLayoutDefinition -Definition $definition -ModuleSize ([Int64]0x20000000)).Valid 'valid candidate rejected'
Assert-That ((Select-BnetDiscoveryCandidate -Candidates @([pscustomobject]@{ Valid=$true; Definition=$definition; MethodVersion='fixture-v1' })).Status -eq 'SINGLE') 'single valid candidate not accepted'
Assert-That ((Select-BnetDiscoveryCandidate -Candidates @([pscustomobject]@{ Valid=$true; Definition=$definition; MethodVersion='fixture-v1' }, [pscustomobject]@{ Valid=$true; Definition=$definition; MethodVersion='fixture-v2' })).Status -eq 'AMBIGUOUS') 'multiple candidates not rejected'

$badRelation = $definition | ConvertTo-Json -Depth 8 | ConvertFrom-Json; $badRelation.datasets.mp.entry0Rva = '0x128D07A4'
Assert-That (-not (Test-BnetLayoutDefinition -Definition $badRelation).Valid) 'invalid count-entry relationship accepted'
$badStride = $definition | ConvertTo-Json -Depth 8 | ConvertFrom-Json; $badStride.entryStride = '0x460'
Assert-That (-not (Test-BnetLayoutDefinition -Definition $badStride).Valid) 'invalid stride accepted'
Assert-That (-not (Test-BnetLayoutDefinition -Definition $definition -ModuleSize ([Int64]0x1000)).Valid) 'out-of-range addresses accepted'

$root = Join-Path ([IO.Path]::GetTempPath()) ('codmunity-layout-tests-' + [guid]::NewGuid().ToString('N'))
try {
    $path = Write-BnetDiscoveredLayout -CollectorRoot $root -Sha256 $sha -Definition $definition -MethodVersion 'fixture-v1'
    Assert-That (Test-Path -LiteralPath $path) 'cache file missing'
    $cached = Read-BnetDiscoveredLayout -CollectorRoot $root -Sha256 $sha
    Assert-That ($cached.Status -eq 'OK') 'valid exact-SHA cache rejected'
    Assert-That ((Read-BnetDiscoveredLayout -CollectorRoot $root -Sha256 ('A' * 64)).Status -eq 'MISSING') 'cache was reused for a different SHA'
    $record = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json; Assert-That ($null -eq ($record | ConvertTo-Json -Depth 12 | Select-String -Pattern 'players|xuid' -Quiet)) 'cache contains player data'
    [IO.File]::WriteAllText($path, '{"schemaVersion":99}', [Text.UTF8Encoding]::new($false))
    Assert-That ((Read-BnetDiscoveredLayout -CollectorRoot $root -Sha256 $sha).Status -eq 'REJECTED') 'malformed cache was accepted'
}
finally { if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force } }

Write-Output 'LAYOUT_DISCOVERY_TESTS_OK'