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
foreach ($transientReason in @('LIVE_SAMPLE_ONE_FAILED', 'LIVE_SAMPLE_TWO_FAILED', 'REPEATED_READ_INCONSISTENT', 'MODULE_REGION_UNAVAILABLE', 'LIVE_VALIDATION_EXCEPTION')) { Assert-That (Test-BnetTransientLiveValidationReason -Reason $transientReason) "transient live validation reason was rejected: $transientReason" }
foreach ($nonTransientReason in @('CACHE_SCHEMA_OR_SHA_MISMATCH', 'CACHE_STRUCTURAL_METADATA_MISMATCH', 'COUNT_OUT_OF_RANGE')) { Assert-That (-not (Test-BnetTransientLiveValidationReason -Reason $nonTransientReason)) "invalid cache/layout reason was treated as transient: $nonTransientReason" }
Assert-That ((Get-BnetCacheValidationDisposition -CacheStatus 'OK' -ValidationReason 'LIVE_SAMPLE_ONE_FAILED') -eq 'PENDING') 'valid cache was not retained for transient live validation failure'
Assert-That ((Get-BnetCacheValidationDisposition -CacheStatus 'OK' -ValidationReason 'LIVE_VALIDATION_EXCEPTION') -eq 'PENDING') 'live validation exception did not remain fail-closed pending'
Assert-That ((Get-BnetCacheValidationDisposition -CacheStatus 'OK' -ValidationReason 'CACHE_STRUCTURAL_METADATA_MISMATCH') -eq 'REJECTED') 'structurally invalid cache was retained'
Assert-That ((Select-BnetDiscoveryCandidate -Candidates @([pscustomobject]@{ Valid=$true; Definition=$definition; MethodVersion='fixture-v1' })).Status -eq 'SINGLE') 'single valid candidate not accepted'
Assert-That ((Select-BnetDiscoveryCandidate -Candidates @([pscustomobject]@{ Valid=$true; Definition=$definition; MethodVersion='fixture-v1' }, [pscustomobject]@{ Valid=$true; Definition=$definition; MethodVersion='fixture-v2' })).Status -eq 'AMBIGUOUS') 'multiple candidates not rejected'

$badRelation = $definition | ConvertTo-Json -Depth 8 | ConvertFrom-Json; $badRelation.datasets.mp.entry0Rva = '0x128D07A4'
Assert-That (-not (Test-BnetLayoutDefinition -Definition $badRelation).Valid) 'invalid count-entry relationship accepted'
$badStride = $definition | ConvertTo-Json -Depth 8 | ConvertFrom-Json; $badStride.entryStride = '0x460'
Assert-That (-not (Test-BnetLayoutDefinition -Definition $badStride).Valid) 'invalid stride accepted'
Assert-That (-not (Test-BnetLayoutDefinition -Definition $definition -ModuleSize ([Int64]0x1000)).Valid) 'out-of-range addresses accepted'

# Synthetic read-only process fixture: exact production row contract, three sibling blocks,
# and no SHA-specific Season 06 RVA knowledge.
$memory = @{}
function Add-FixtureBytes([Int64]$Rva, [byte[]]$Bytes) { $memory[('{0:X}/{1}' -f $Rva,$Bytes.Length)] = $Bytes }
function Add-FixtureTriple([Int64]$MpState, [int[]]$Counts) {
    for ($modeIndex = 0; $modeIndex -lt 3; $modeIndex++) {
        $state = $MpState + ($modeIndex * 0x820F0); $countRva = $state + 0x3B74C
        Add-FixtureBytes $state ([byte[]]@(3)); Add-FixtureBytes $countRva ([BitConverter]::GetBytes([uint32]$Counts[$modeIndex]))
        for ($rowIndex = 0; $rowIndex -lt $Counts[$modeIndex]; $rowIndex++) {
            $row = New-Object byte[] 0x458
            [Array]::Copy([BitConverter]::GetBytes([uint64](1000 + ($modeIndex * 100) + $rowIndex)),0,$row,0,8)
            [Array]::Copy([BitConverter]::GetBytes([uint32]$rowIndex),0,$row,8,4)
            [Array]::Copy([BitConverter]::GetBytes([int32](12000 - $rowIndex)),0,$row,12,4)
            $name = [Text.Encoding]::UTF8.GetBytes(('player{0}{1}' -f $modeIndex,$rowIndex)); [Array]::Copy($name,0,$row,0x10,$name.Length)
            Add-FixtureBytes ($countRva + 4 + ($rowIndex * 0x458)) $row
        }
    }
}
Add-FixtureTriple 0x200000 @(2,1,0)
$reader = { param([Int64]$Rva,[int]$Size) $key = '{0:X}/{1}' -f $Rva,$Size; if (-not $memory.ContainsKey($key)) { throw "fixture read missing $key" }; Write-Output -NoEnumerate $memory[$key] }.GetNewClosure()
$geometry = Get-BnetGeometryDiscoveryCandidates -ModuleSize 0x1000000 -StateCandidates @([Int64]0x200000) -ReadBytes $reader -Sha256 ('B' * 64)
Assert-That ($geometry.SemanticCandidates -eq 1) 'valid three-mode geometry was not accepted'
$countGeometry = Get-BnetGeometryCandidatesFromCounts -ModuleSize 0x1000000 -CountCandidates @([Int64]0x23B74C) -ReadLocalBytes $reader -Sha256 ('B' * 64)
Assert-That ($countGeometry.ThreeModeCandidates -eq 1 -and $countGeometry.Candidates.Count -eq 1) 'count-derived three-mode geometry was not accepted'
$discovered = $geometry.Candidates[0].Definition
Assert-That ((ConvertTo-BnetLayoutRva $discovered.datasets.mp.countRva) -eq 0x23B74C) 'geometry did not derive count RVA'
Assert-That ((ConvertTo-BnetLayoutRva $discovered.datasets.resurgence.entry0Rva) -eq 0x33F930) 'geometry did not derive sibling entry RVA'

# The live scanner receives about one million unrelated aligned small integers.  Keep that
# stress case in the compiled filter so an accidental return to PowerShell object/pipeline
# filtering is detected before a real unknown-build discovery is attempted.
$discoveryModule = Get-Module layout_discovery
$stress = & $discoveryModule {
    Initialize-BnetStructuralDiscoveryNative
    $bytes = [Top250StructuralGeometryFilter]::CreateRepeatedUInt32Buffer(4MB, [uint32]1)
    function Set-StressUInt32([byte[]]$Target, [Int64]$Rva, [uint32]$Value) { [Array]::Copy([BitConverter]::GetBytes($Value), 0, $Target, [int]$Rva, 4) }
    function Set-StressByte([byte[]]$Target, [Int64]$Rva, [byte]$Value) { $Target[[int]$Rva] = $Value }
    function Add-StressTriple([byte[]]$Target, [Int64]$Count0, [uint32[]]$Counts) {
        for ($mode = 0; $mode -lt 3; $mode++) {
            $count = $Count0 + ($mode * 0x820F0)
            Set-StressUInt32 $Target $count $Counts[$mode]
            Set-StressByte $Target ($count - 0x3B74C) ([byte]3)
        }
    }
    Add-StressTriple $bytes 0x200000 @([uint32]2,[uint32]1,[uint32]0)
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $single = [Top250StructuralGeometryFilter]::FindTriples(@([Top250DiscoveryChunk]::new(0, $bytes)), 4MB, 0x3B74C, 0x820F0, 30000)
    $singleElapsedMs = $watch.ElapsedMilliseconds
    Add-StressTriple $bytes 0x2A0000 @([uint32]1,[uint32]1,[uint32]0)
    $duplicate = [Top250StructuralGeometryFilter]::FindTriples(@([Top250DiscoveryChunk]::new(0, $bytes)), 4MB, 0x3B74C, 0x820F0, 30000)
    [pscustomobject]@{ Single=$single; Duplicate=$duplicate; SingleElapsedMs=$singleElapsedMs }
}
Assert-That (-not $stress.Single.TimedOut) 'compiled million-candidate geometry filter timed out'
Assert-That ($stress.Single.RawCountCandidates -ge 1000000) 'million-candidate stress fixture was not populated'
Assert-That ($stress.Single.CandidateCountRvas.Count -eq 1 -and $stress.Single.CandidateCountRvas[0] -eq 0x200000) 'compiled geometry filter did not recover the valid triple with an empty sibling'
Assert-That ($stress.SingleElapsedMs -lt 30000) 'compiled million-candidate geometry filter exceeded its practical test budget'
Assert-That (-not $stress.Duplicate.TimedOut -and $stress.Duplicate.CandidateCountRvas.Count -eq 2) 'duplicate structural triples were not retained for fail-closed ambiguity handling'
$duplicateDefinitions = @($stress.Duplicate.CandidateCountRvas | ForEach-Object {
    $candidateCount = [Int64]$_; $candidateAnchors = @{}
    for ($mode = 0; $mode -lt 3; $mode++) { $modeName = @('mp','br','resurgence')[$mode]; $count = $candidateCount + ($mode * 0x820F0); $candidateAnchors["$modeName/stateRva"]=$count-0x3B74C; $candidateAnchors["$modeName/countRva"]=$count; $candidateAnchors["$modeName/entry0Rva"]=$count+4 }
    [pscustomobject]@{ Valid=$true; Definition=(New-BnetLayoutDefinitionFromAnchors -Anchors $candidateAnchors -Sha256 ('C' * 64) -MethodVersion 'stress'); MethodVersion='stress' }
})
Assert-That ((Select-BnetDiscoveryCandidate -Candidates $duplicateDefinitions).Status -eq 'AMBIGUOUS') 'duplicate triples were not rejected fail-closed'

$invalidCounts = @{} + $memory
$invalidCounts['23B74C/4'] = [BitConverter]::GetBytes([uint32]251)
$invalidReader = { param([Int64]$Rva,[int]$Size) $key = '{0:X}/{1}' -f $Rva,$Size; if (-not $invalidCounts.ContainsKey($key)) { throw "fixture read missing $key" }; Write-Output -NoEnumerate $invalidCounts[$key] }.GetNewClosure()
Assert-That ((Get-BnetGeometryDiscoveryCandidates -ModuleSize 0x1000000 -StateCandidates @([Int64]0x200000) -ReadBytes $invalidReader -Sha256 ('B' * 64)).SemanticCandidates -eq 0) 'count > 250 geometry accepted'
Assert-That ((Get-BnetGeometryDiscoveryCandidates -ModuleSize 0x1000000 -StateCandidates @([Int64]0x200000, [Int64]0x300000) -ReadBytes $reader -Sha256 ('B' * 64)).SemanticCandidates -eq 1) 'invalid competing state was accepted'

$root = Join-Path ([IO.Path]::GetTempPath()) ('codmunity-layout-tests-' + [guid]::NewGuid().ToString('N'))
try {
    $path = Write-BnetDiscoveredLayout -CollectorRoot $root -Sha256 $sha -Definition $definition -MethodVersion 'fixture-v1'
    Assert-That (Test-Path -LiteralPath $path) 'cache file missing'
    $cached = Read-BnetDiscoveredLayout -CollectorRoot $root -Sha256 $sha
    Assert-That ($cached.Status -eq 'OK') 'valid exact-SHA cache rejected'
    Assert-That ((Read-BnetDiscoveredLayout -CollectorRoot $root -Sha256 ('A' * 64)).Status -eq 'MISSING') 'cache was reused for a different SHA'
    $record = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json; Assert-That ($null -eq ($record | ConvertTo-Json -Depth 12 | Select-String -Pattern 'players|xuid' -Quiet)) 'cache contains player data'
    $wrongSha = $record | ConvertTo-Json -Depth 12 | ConvertFrom-Json; $wrongSha.executableSha256 = ('D' * 64); $wrongSha | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $path -Encoding UTF8
    Assert-That ((Read-BnetDiscoveredLayout -CollectorRoot $root -Sha256 $sha).Status -eq 'REJECTED') 'wrong-SHA cache was accepted'
    $missingMetadata = $record | ConvertTo-Json -Depth 12 | ConvertFrom-Json; $missingMetadata.PSObject.Properties.Remove('structural'); $missingMetadata | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $path -Encoding UTF8
    Assert-That ((Read-BnetDiscoveredLayout -CollectorRoot $root -Sha256 $sha).Status -eq 'REJECTED') 'cache without structural metadata was accepted'
    $corruptRva = $record | ConvertTo-Json -Depth 12 | ConvertFrom-Json; $corruptRva.definition.datasets.mp.entry0Rva = '0x1'; $corruptRva | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $path -Encoding UTF8
    Assert-That ((Read-BnetDiscoveredLayout -CollectorRoot $root -Sha256 $sha).Status -eq 'REJECTED') 'cache with corrupted RVA was accepted'
    [IO.File]::WriteAllText($path, '{"schemaVersion":99}', [Text.UTF8Encoding]::new($false))
    Assert-That ((Read-BnetDiscoveredLayout -CollectorRoot $root -Sha256 $sha).Status -eq 'REJECTED') 'malformed cache was accepted'
}
finally { if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force } }

Write-Output 'LAYOUT_DISCOVERY_TESTS_OK'
