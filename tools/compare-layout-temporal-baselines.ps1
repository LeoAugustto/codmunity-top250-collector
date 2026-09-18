[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })][string]$EmptyBaseline,
    [Parameter(Mandatory)][ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })][string]$PopulatedBaseline
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$empty = Get-Content -LiteralPath $EmptyBaseline -Raw -Encoding UTF8 | ConvertFrom-Json
$populated = Get-Content -LiteralPath $PopulatedBaseline -Raw -Encoding UTF8 | ConvertFrom-Json
if ($empty.label -ne 'season06_top250_empty_baseline' -or $populated.label -ne 'season06_top250_populated') { throw 'TEMPORAL_BASELINE_LABEL_INVALID' }
if ($empty.executable.sha256 -ne $populated.executable.sha256) { throw 'TEMPORAL_BASELINE_SHA_MISMATCH' }
$old = @{}; foreach ($chunk in @($empty.chunks)) { $old[($chunk.rva + '/' + $chunk.size)] = $chunk }
$new = @{}; foreach ($chunk in @($populated.chunks)) { $new[($chunk.rva + '/' + $chunk.size)] = $chunk }
$changes = New-Object System.Collections.Generic.List[object]
foreach ($key in @($old.Keys + $new.Keys | Sort-Object -Unique)) {
    $before = $old[$key]; $after = $new[$key]
    $classification = if ($null -eq $before) { 'newly-committed-or-topology-changed' } elseif ($null -eq $after) { 'disappeared-or-topology-changed' } elseif ($before.sha256 -eq $after.sha256) { 'unchanged' } else { 'hash-changed' }
    $changes.Add([ordered]@{ rva = if ($null -ne $after) { $after.rva } else { $before.rva }; size = if ($null -ne $after) { $after.size } else { $before.size }; classification = $classification; emptySmallUint32Samples = if ($null -ne $before) { @($before.smallUint32Samples) } else { @() }; populatedSmallUint32Samples = if ($null -ne $after) { @($after.smallUint32Samples) } else { @() } })
}
$result = [ordered]@{ schemaVersion = 1; sha256 = $empty.executable.sha256; emptyCapturedAtUtc = $empty.capturedAtUtc; populatedCapturedAtUtc = $populated.capturedAtUtc; changes = @($changes.ToArray()); privacy = [ordered]@{ playerNamesStored = $false; playerXuidsStored = $false; playerRecordBytesStored = $false } }
$path = Join-Path (Split-Path -Parent $PopulatedBaseline) 'temporal-cache-comparison.json'; $result | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $path -Encoding UTF8
$changed = @($result.changes | Where-Object { $_.classification -ne 'unchanged' }).Count
Write-Output ('TEMPORAL_BASELINE_COMPARISON_READY sha={0} changedChunks={1} output={2}' -f $result.sha256, $changed, $path)
