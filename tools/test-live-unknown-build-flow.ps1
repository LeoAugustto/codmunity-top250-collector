[CmdletBinding()]
param(
    [Parameter(Mandatory, ParameterSetName = 'Live')][string]$CodPath,
    [Parameter(Mandatory, ParameterSetName = 'SelfTest')][switch]$ComparisonSelfTest,
    [ValidateRange(1, 3600)][int]$MaxDiscoverySeconds = 120
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Convert-AnchorRva([object]$Value) { ConvertTo-BnetLayoutRva $Value }
function Get-EffectiveEntryStride([object]$Definition) {
    $property = $Definition.PSObject.Properties['entryStride']
    if (($null -eq $property) -or ($null -eq $property.Value) -or [string]::IsNullOrWhiteSpace([string]$property.Value)) { return [Int64]0x458 }
    return Convert-AnchorRva $property.Value
}
function Same-Definition([object]$Left, [object]$Right) {
    foreach ($mode in @('mp','br','resurgence')) {
        foreach ($field in @('stateRva','countRva','entry0Rva')) {
            if ((Convert-AnchorRva $Left.datasets.$mode.$field) -ne (Convert-AnchorRva $Right.datasets.$mode.$field)) { return $false }
        }
    }
    return ((Get-EffectiveEntryStride $Left) -eq (Get-EffectiveEntryStride $Right))
}
function Invoke-NormalDynamicExtraction([object]$Definition, [string]$ExpectedSha, [string]$Path) {
    $exporter = Join-Path $projectRoot 'engine\exporters\top250_bnet_dynamic_layout.ps1'
    $powerShell = (Get-Command powershell.exe -CommandType Application -ErrorAction Stop).Source
    $results = @()
    foreach ($mode in @('mp','br','resurgence')) {
        $set = $Definition.datasets.$mode
        $output = & $powerShell -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $exporter -ExpectedCodPath $Path -Mode $mode -ExpectedSha256 $ExpectedSha -ManagerRva (Convert-AnchorRva $set.stateRva) -CountRva (Convert-AnchorRva $set.countRva) -Entry0Rva (Convert-AnchorRva $set.entry0Rva) -EmitJson -Quiet 2>&1
        if ($LASTEXITCODE -ne 0) { throw ('NORMAL_EXTRACTION_FAILED mode=' + $mode + ' exitCode=' + $LASTEXITCODE) }
        $payload = ($output -join [Environment]::NewLine) | ConvertFrom-Json -ErrorAction Stop
        if ($payload.mode -ne $mode -or [int]$payload.nativeCount -ne [int]$payload.rowCount -or [int]$payload.nativeCount -lt 0 -or [int]$payload.nativeCount -gt 250) { throw ('NORMAL_EXTRACTION_CONTRACT_FAILED mode=' + $mode) }
        $results += [pscustomobject]@{ Mode=$mode; NativeCount=[int]$payload.nativeCount; RowCount=[int]$payload.rowCount; Payload=$payload }
    }
    return @($results)
}
function Same-Extraction([object[]]$Left, [object[]]$Right) {
    foreach ($mode in @('mp','br','resurgence')) {
        $a = @($Left | Where-Object { $_.Mode -eq $mode })[0]; $b = @($Right | Where-Object { $_.Mode -eq $mode })[0]
        if (($null -eq $a) -or ($null -eq $b) -or $a.NativeCount -ne $b.NativeCount -or $a.RowCount -ne $b.RowCount) { return $false }
        if ((@($a.Payload.players) | ConvertTo-Json -Depth 6 -Compress) -ne (@($b.Payload.players) | ConvertTo-Json -Depth 6 -Compress)) { return $false }
    }
    return $true
}

$projectRoot = Split-Path -Parent $PSScriptRoot
Import-Module (Join-Path $projectRoot 'engine\layout_discovery.psm1') -Force
if ($ComparisonSelfTest) {
    $anchors = @{
        'mp/stateRva'=[Int64]0x200000; 'mp/countRva'=[Int64]0x23B74C; 'mp/entry0Rva'=[Int64]0x23B750
        'br/stateRva'=[Int64]0x2820F0; 'br/countRva'=[Int64]0x2BD83C; 'br/entry0Rva'=[Int64]0x2BD840
        'resurgence/stateRva'=[Int64]0x3041E0; 'resurgence/countRva'=[Int64]0x33F92C; 'resurgence/entry0Rva'=[Int64]0x33F930
    }
    $explicit = New-BnetLayoutDefinitionFromAnchors -Anchors $anchors -Sha256 ('A' * 64) -MethodVersion 'comparison-fixture'
    $staticStyle = $explicit | ConvertTo-Json -Depth 8 | ConvertFrom-Json; $staticStyle.PSObject.Properties.Remove('entryStride')
    $incompatible = $explicit | ConvertTo-Json -Depth 8 | ConvertFrom-Json; $incompatible.entryStride = '0x460'
    if (-not (Same-Definition $explicit $staticStyle)) { throw 'DEFAULT_ENTRY_STRIDE_NOT_NORMALIZED' }
    if (Same-Definition $explicit $incompatible) { throw 'INCOMPATIBLE_ENTRY_STRIDE_ACCEPTED' }
    Write-Output 'UNKNOWN_BUILD_FLOW_COMPARISON_TEST_OK'
    return
}

$resolvedPath = (Resolve-Path -LiteralPath $CodPath -ErrorAction Stop).Path
if (-not [string]::Equals([IO.Path]::GetFileName($resolvedPath), 'cod.exe', [StringComparison]::OrdinalIgnoreCase)) { throw 'COD_PATH_MUST_POINT_TO_COD_EXE' }
$cod = @(Get-Process -Name cod -ErrorAction SilentlyContinue | Where-Object {
    try { $_.Path -and [string]::Equals($_.Path, $resolvedPath, [StringComparison]::OrdinalIgnoreCase) } catch { $false }
})
if ($cod.Count -ne 1) { throw ('EXPECTED_EXACTLY_ONE_RUNNING_COD_PROCESS count=' + $cod.Count) }

$sha = (Get-FileHash -LiteralPath $resolvedPath -Algorithm SHA256).Hash.ToUpperInvariant()
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('codmunity-unknown-build-flow-' + [guid]::NewGuid().ToString('N'))
try {
    New-Item -ItemType Directory -Force -Path $testRoot | Out-Null
    $cachePath = Get-BnetDiscoveredLayoutPath -CollectorRoot $testRoot -Sha256 $sha
    Write-Output 'UNKNOWN_BUILD_FLOW_START'
    Write-Output ('sha={0}' -f $sha)
    Write-Output 'staticLayoutExcluded=true'
    Write-Output ('initialCachePresent={0}' -f (Test-Path -LiteralPath $cachePath -PathType Leaf).ToString().ToLowerInvariant())

    # Direct structural invocation deliberately occurs before any build manifest is read.
    # No static catalog or pre-existing cache can participate in the cold result.
    $progress = { param([string]$Stage,[hashtable]$Data,[TimeSpan]$Elapsed) if ($Stage -in @('CANDIDATE_SCAN_DONE','GEOMETRY_FILTER_DONE','SEMANTIC_VALIDATION_DONE')) { Write-Host ('{0} elapsedMs={1}' -f $Stage,[int64]$Elapsed.TotalMilliseconds) } }
    $cold = Invoke-BnetStructuralDiscovery -Cod $cod[0] -Sha256 $sha -MaxDiscoverySeconds $MaxDiscoverySeconds -ProgressCallback $progress
    $coldDecision = Select-BnetDiscoveryCandidate -Candidates @($cold.Candidates)
    if ($coldDecision.Status -ne 'SINGLE') { throw ('COLD_STRUCTURAL_DISCOVERY_' + $coldDecision.Status + '_' + $cold.Reason) }
    Write-Output 'discoverySource=structural'
    Write-Output ('validTriples={0}' -f @($cold.Candidates).Count)
    Write-Output 'layoutDiscoveryStatus=resolved'

    # The exporter is the normal read-only collection contract.  EmitJson prevents data files
    # from being written and the harness never calls the API.
    $coldExtraction = Invoke-NormalDynamicExtraction -Definition $coldDecision.Candidate -ExpectedSha $sha -Path $resolvedPath
    Write-Output ('normalExtraction=ok counts={0}' -f (($coldExtraction | ForEach-Object { $_.Mode + ':' + $_.NativeCount }) -join ','))

    [void](Write-BnetDiscoveredLayout -CollectorRoot $testRoot -Sha256 $sha -Definition $coldDecision.Candidate -MethodVersion $coldDecision.MethodVersion)
    Write-Output 'cacheWritten=true'
    Write-Output ('cachePath={0}' -f $cachePath)

    Write-Output 'SECOND_PASS_START'
    $cached = Read-BnetDiscoveredLayout -CollectorRoot $testRoot -Sha256 $sha
    if ($cached.Status -ne 'OK') { throw ('SECOND_PASS_CACHE_' + $cached.Status + '_' + $cached.Reason) }
    $cachedExtraction = Invoke-NormalDynamicExtraction -Definition $cached.Definition -ExpectedSha $sha -Path $resolvedPath
    if (-not (Same-Definition $coldDecision.Candidate $cached.Definition)) { throw 'FIRST_SECOND_LAYOUT_MISMATCH' }
    if (-not (Same-Extraction $coldExtraction $cachedExtraction)) { throw 'FIRST_SECOND_EXTRACTION_MISMATCH' }
    Write-Output 'discoverySource=cache'
    Write-Output 'cacheLoaded=true'
    Write-Output 'cacheRevalidated=true'
    Write-Output 'layoutDiscoveryStatus=resolved'
    Write-Output 'firstSecondLayoutMatch=true'

    # Ground-truth/static comparison is intentionally post-discovery only.
    $manifest = Get-Content -LiteralPath (Join-Path $projectRoot 'engine\build_layouts.json') -Raw -Encoding UTF8 | ConvertFrom-Json -ErrorAction Stop
    $mapping = $manifest.sha256Builds.PSObject.Properties[$sha]
    $groundTruthMatch = $false
    if ($null -ne $mapping) {
        $known = $manifest.battlenetLayoutDefinitions.PSObject.Properties[[string]$mapping.Value.layout]
        if ($null -ne $known) {
            $groundTruthMatch = (Same-Definition $coldDecision.Candidate $known.Value)
            if ($groundTruthMatch) {
                $knownExtraction = Invoke-NormalDynamicExtraction -Definition $known.Value -ExpectedSha $sha -Path $resolvedPath
                if (-not (Same-Extraction $coldExtraction $knownExtraction)) { throw 'STATIC_DISCOVERED_EXTRACTION_MISMATCH' }
            }
        }
    }
    Write-Output ('groundTruthMatch={0}' -f $groundTruthMatch.ToString().ToLowerInvariant())
    Write-Output 'UNKNOWN_BUILD_FLOW_RESULT=PASS'
}
finally {
    if (Test-Path -LiteralPath $testRoot) { Remove-Item -LiteralPath $testRoot -Recurse -Force }
}
