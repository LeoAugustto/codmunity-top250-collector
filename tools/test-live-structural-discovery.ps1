[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$CodPath,
    [ValidateRange(1, 3600)][int]$MaxDiscoverySeconds = 120
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$projectRoot = Split-Path -Parent $PSScriptRoot
$resolvedPath = (Resolve-Path -LiteralPath $CodPath -ErrorAction Stop).Path
if (-not [string]::Equals([IO.Path]::GetFileName($resolvedPath), 'cod.exe', [StringComparison]::OrdinalIgnoreCase)) { throw 'COD_PATH_MUST_POINT_TO_COD_EXE' }

Import-Module (Join-Path $projectRoot 'engine\layout_discovery.psm1') -Force

$cod = @(
    Get-Process -Name cod -ErrorAction SilentlyContinue | Where-Object {
        try { $_.Path -and [string]::Equals($_.Path, $resolvedPath, [StringComparison]::OrdinalIgnoreCase) } catch { $false }
    }
)
if ($cod.Count -ne 1) { throw ('EXPECTED_EXACTLY_ONE_RUNNING_COD_PROCESS count=' + $cod.Count) }

# These values are deliberately post-discovery assertions only. They are never passed
# to Invoke-BnetStructuralDiscovery and therefore cannot influence candidate selection.
$expectedSeason06 = [ordered]@{
    mp = @{ state = [Int64]0x12BDED10; count = [Int64]0x12C1A45C; entry0 = [Int64]0x12C1A460 }
    br = @{ state = [Int64]0x12C60E00; count = [Int64]0x12C9C54C; entry0 = [Int64]0x12C9C550 }
    resurgence = @{ state = [Int64]0x12CE2EF0; count = [Int64]0x12D1E63C; entry0 = [Int64]0x12D1E640 }
}

function Format-Rva([Int64]$Value) { '0x{0:X}' -f $Value }
function Get-Rva([object]$Value) { ConvertTo-BnetLayoutRva $Value }

$sha = (Get-FileHash -LiteralPath $resolvedPath -Algorithm SHA256).Hash.ToUpperInvariant()
Write-Output 'STRUCTURAL_DISCOVERY_START'
Write-Output ('sha={0}' -f $sha)
Write-Output ('pid={0}' -f $cod[0].Id)

# Direct invocation: no build catalog, orchestrator, static layout, or discovered cache is read or written.
$progress = {
    param([string]$Stage, [hashtable]$Data, [TimeSpan]$Elapsed)
    $suffix = @($Data.Keys | Sort-Object | ForEach-Object { '{0}={1}' -f $_,$Data[$_] }) -join ' '
    Write-Host ('{0} elapsedMs={1} {2}' -f $Stage,[int64]$Elapsed.TotalMilliseconds,$suffix)
}
try {
    $result = Invoke-BnetStructuralDiscovery -Cod $cod[0] -Sha256 $sha -MaxDiscoverySeconds $MaxDiscoverySeconds -ProgressCallback $progress
}
catch {
    Write-Output 'STRUCTURAL_DISCOVERY_RESULT=ERROR'
    Write-Output 'stage=STRUCTURAL_DISCOVERY'
    Write-Output ('reason={0}' -f $_.Exception.Message)
    exit 1
}
$requiredResultProperties = @('ModuleBase','ModuleSize','WritableRegions','BytesScanned','RawCountLikeCandidates','TripleAddressCandidates','CountRangeCandidates','GeometryCandidates','ThreeModeCandidates','SemanticCandidates','CandidateScanMilliseconds','TripleLookupMilliseconds','GeometryValidationMilliseconds','SemanticValidationMilliseconds','TotalMilliseconds','Candidates','Reason')
$missingResultProperties = @($requiredResultProperties | Where-Object { $null -eq $result.PSObject.Properties[$_] })
if ($missingResultProperties.Count -gt 0) {
    Write-Output 'STRUCTURAL_DISCOVERY_RESULT=ERROR'
    Write-Output 'stage=MODULE_BASE_RESOLVE'
    Write-Output ('reason=RESULT_CONTRACT_MISSING_' + ($missingResultProperties -join '_'))
    exit 1
}
if ($result.Reason -eq 'MODULE_BASE_RESOLVE_FAILED' -or $result.Reason -eq 'MODULE_REGION_UNAVAILABLE') {
    Write-Output 'STRUCTURAL_DISCOVERY_RESULT=ERROR'
    Write-Output 'stage=MODULE_BASE_RESOLVE'
    Write-Output ('reason={0}' -f $result.Reason)
    exit 1
}
Write-Output ('moduleBase={0}' -f (Format-Rva $result.ModuleBase))
Write-Output ('writableRegions={0}' -f $result.WritableRegions)
Write-Output ('bytesScanned={0}' -f $result.BytesScanned)
Write-Output ('rawCountCandidates={0}' -f $result.RawCountLikeCandidates)
Write-Output ('tripleAddressCandidates={0}' -f $result.TripleAddressCandidates)
Write-Output ('countRangeCandidates={0}' -f $result.CountRangeCandidates)
Write-Output ('stateGeometryCandidates={0}' -f $result.ThreeModeCandidates)
Write-Output ('geometryCandidates={0}' -f $result.GeometryCandidates)
Write-Output ('tripleSpacingCandidates={0}' -f $result.ThreeModeCandidates)
Write-Output ('semanticCandidates={0}' -f $result.SemanticCandidates)
Write-Output ('validTriples={0}' -f @($result.Candidates).Count)
Write-Output ('candidateScanMs={0}' -f $result.CandidateScanMilliseconds)
Write-Output ('tripleLookupMs={0}' -f $result.TripleLookupMilliseconds)
Write-Output ('geometryValidationMs={0}' -f $result.GeometryValidationMilliseconds)
Write-Output ('semanticValidationMs={0}' -f $result.SemanticValidationMilliseconds)
Write-Output ('totalMs={0}' -f $result.TotalMilliseconds)

$decision = Select-BnetDiscoveryCandidate -Candidates @($result.Candidates)
if ($decision.Status -eq 'SINGLE') {
    $definition = $decision.Candidate
    foreach ($mode in @('mp','br','resurgence')) {
        $dataset = $definition.datasets.$mode
        Write-Output ('{0} state={1} count={2} entry0={3}' -f $mode.ToUpperInvariant(), (Format-Rva (Get-Rva $dataset.stateRva)), (Format-Rva (Get-Rva $dataset.countRva)), (Format-Rva (Get-Rva $dataset.entry0Rva)))
    }
    $shape = Test-BnetLayoutDefinition -Definition $definition
    Write-Output ('stride={0}' -f $definition.entryStride)
    Write-Output ('stateToCount={0}' -f (Format-Rva $shape.StateToCount))
    Write-Output ('modeSpacing={0}' -f (Format-Rva $shape.ModeBlockSpacing))

    $matchesExpected = $true
    foreach ($mode in @('mp','br','resurgence')) {
        $dataset = $definition.datasets.$mode
        if ((Get-Rva $dataset.stateRva) -ne $expectedSeason06[$mode].state -or (Get-Rva $dataset.countRva) -ne $expectedSeason06[$mode].count -or (Get-Rva $dataset.entry0Rva) -ne $expectedSeason06[$mode].entry0) { $matchesExpected = $false }
    }
    Write-Output ('postDiscoveryGroundTruthMatch={0}' -f $matchesExpected.ToString().ToLowerInvariant())
    Write-Output 'STRUCTURAL_DISCOVERY_RESULT=UNIQUE'
}
elseif ($decision.Status -eq 'AMBIGUOUS') {
    Write-Output 'STRUCTURAL_DISCOVERY_RESULT=AMBIGUOUS'
}
else {
    Write-Output ('structuralReason={0}' -f $result.Reason)
    if ($result.Reason -eq 'TIMEOUT') { Write-Output ('lastCompletedStage={0}' -f $result.LastStage); Write-Output 'STRUCTURAL_DISCOVERY_RESULT=TIMEOUT'; return }
    Write-Output 'STRUCTURAL_DISCOVERY_RESULT=NONE'
}
