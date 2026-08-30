Set-StrictMode -Version Latest

$script:DiscoverySchemaVersion = 1
$script:ExpectedModes = @('mp', 'br', 'resurgence')
$script:ExpectedFields = @('stateRva', 'countRva', 'entry0Rva')
$script:DefaultEntryStride = 0x458

function ConvertTo-BnetLayoutRva {
    param([Parameter(Mandatory)][object]$Value)
    if ($Value -is [string] -and $Value.StartsWith('0x', [StringComparison]::OrdinalIgnoreCase)) { return [Int64][Convert]::ToInt64($Value.Substring(2), 16) }
    return [Int64]$Value
}

function Test-BnetDiscoverySha {
    param([string]$Sha256)
    return -not [string]::IsNullOrWhiteSpace($Sha256) -and $Sha256 -match '^[A-Fa-f0-9]{64}$'
}

function Get-BnetDiscoveredLayoutPath {
    param([Parameter(Mandatory)][string]$CollectorRoot, [Parameter(Mandatory)][string]$Sha256)
    if (-not (Test-BnetDiscoverySha -Sha256 $Sha256)) { throw 'DISCOVERY_CACHE_SHA_INVALID' }
    Join-Path (Join-Path (Join-Path $CollectorRoot 'layouts') 'discovered') ($Sha256.ToUpperInvariant() + '.json')
}

function Test-BnetLayoutDefinition {
    param([Parameter(Mandatory)][object]$Definition, [Int64]$ModuleSize = 0)
    try {
        if ($null -eq $Definition.datasets) { return [pscustomobject]@{ Valid = $false; Reason = 'DATASETS_MISSING' } }
        $stride = if ($null -eq $Definition.entryStride) { 0 } else { ConvertTo-BnetLayoutRva $Definition.entryStride }
        if ($stride -ne $script:DefaultEntryStride) { return [pscustomobject]@{ Valid = $false; Reason = 'ENTRY_STRIDE_UNSUPPORTED' } }
        $stateToCount = @(); $modeBases = @{}
        foreach ($mode in $script:ExpectedModes) {
            $property = $Definition.datasets.PSObject.Properties[$mode]; if ($null -eq $property) { return [pscustomobject]@{ Valid = $false; Reason = "DATASET_MISSING_$mode" } }
            $dataset = $property.Value; $values = @{}
            foreach ($field in $script:ExpectedFields) {
                $fieldProperty = $dataset.PSObject.Properties[$field]; if ($null -eq $fieldProperty) { return [pscustomobject]@{ Valid = $false; Reason = "FIELD_MISSING_${mode}_$field" } }
                $value = ConvertTo-BnetLayoutRva $fieldProperty.Value
                if ($value -le 0) { return [pscustomobject]@{ Valid = $false; Reason = "FIELD_NONPOSITIVE_${mode}_$field" } }
                if ($ModuleSize -gt 0 -and $value -ge $ModuleSize) { return [pscustomobject]@{ Valid = $false; Reason = "FIELD_OUTSIDE_MODULE_${mode}_$field" } }
                $values[$field] = $value
            }
            if ($values.entry0Rva -ne ($values.countRva + 4)) { return [pscustomobject]@{ Valid = $false; Reason = "COUNT_ENTRY_RELATION_${mode}" } }
            if ($values.stateRva -ge $values.countRva) { return [pscustomobject]@{ Valid = $false; Reason = "STATE_COUNT_ORDER_${mode}" } }
            $stateToCount += ($values.countRva - $values.stateRva); $modeBases[$mode] = $values.stateRva
        }
        if (@($stateToCount | Select-Object -Unique).Count -ne 1) { return [pscustomobject]@{ Valid = $false; Reason = 'STATE_COUNT_RELATION_INCOHERENT' } }
        $firstGap = $modeBases.br - $modeBases.mp; $secondGap = $modeBases.resurgence - $modeBases.br
        if ($firstGap -le 0 -or $secondGap -le 0 -or $firstGap -ne $secondGap) { return [pscustomobject]@{ Valid = $false; Reason = 'MODE_BLOCK_SPACING_INCOHERENT' } }
        [pscustomobject]@{ Valid = $true; Reason = 'OK'; StateToCount = $stateToCount[0]; ModeBlockSpacing = $firstGap }
    }
    catch { [pscustomobject]@{ Valid = $false; Reason = 'DEFINITION_PARSE_FAILED' } }
}

function New-BnetLayoutDefinitionFromAnchors {
    param([Parameter(Mandatory)][hashtable]$Anchors, [Parameter(Mandatory)][string]$Sha256, [Parameter(Mandatory)][string]$MethodVersion)
    if (-not (Test-BnetDiscoverySha -Sha256 $Sha256)) { throw 'DISCOVERY_SHA_INVALID' }
    $datasets = [ordered]@{}
    foreach ($mode in $script:ExpectedModes) {
        $item = [ordered]@{}
        foreach ($field in $script:ExpectedFields) {
            $key = "$mode/$field"; if (-not $Anchors.ContainsKey($key)) { throw "DISCOVERY_ANCHOR_MISSING_$key" }
            $item[$field] = ('0x{0:X}' -f [Int64]$Anchors[$key])
        }
        $datasets[$mode] = [pscustomobject]$item
    }
    [pscustomobject]([ordered]@{
        schemaVersion = $script:DiscoverySchemaVersion
        name = 'bnet-autodiscovered-' + $Sha256.Substring(0, 12).ToLowerInvariant()
        source = 'static-anchor-set'
        methodVersion = $MethodVersion
        entryStride = ('0x{0:X}' -f $script:DefaultEntryStride)
        datasets = [pscustomobject]$datasets
        anchorIds = @($Anchors.Keys | Sort-Object)
    })
}

function Select-BnetDiscoveryCandidate {
    param([object[]]$Candidates)
    $valid = @($Candidates | Where-Object { $null -ne $_ -and $_.Valid -and $null -ne $_.Definition })
    if ($valid.Count -eq 0) { return [pscustomobject]@{ Status = 'NONE'; Candidate = $null; Count = 0 } }
    if ($valid.Count -ne 1) { return [pscustomobject]@{ Status = 'AMBIGUOUS'; Candidate = $null; Count = $valid.Count } }
    [pscustomobject]@{ Status = 'SINGLE'; Candidate = $valid[0].Definition; Count = 1; MethodVersion = $valid[0].MethodVersion }
}

function Read-BnetDiscoveredLayout {
    param([Parameter(Mandatory)][string]$CollectorRoot, [Parameter(Mandatory)][string]$Sha256)
    $path = Get-BnetDiscoveredLayoutPath -CollectorRoot $CollectorRoot -Sha256 $Sha256
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { return [pscustomobject]@{ Status = 'MISSING'; Definition = $null } }
    try { $record = Get-Content -LiteralPath $path -Raw -Encoding UTF8 | ConvertFrom-Json -ErrorAction Stop }
    catch { return [pscustomobject]@{ Status = 'REJECTED'; Reason = 'CACHE_JSON_INVALID'; Definition = $null } }
    if ($record.schemaVersion -ne $script:DiscoverySchemaVersion -or -not [string]::Equals([string]$record.executableSha256, $Sha256, [StringComparison]::OrdinalIgnoreCase)) { return [pscustomobject]@{ Status = 'REJECTED'; Reason = 'CACHE_SCHEMA_OR_SHA_MISMATCH'; Definition = $null } }
    $shape = Test-BnetLayoutDefinition -Definition $record.definition
    if (-not $shape.Valid) { return [pscustomobject]@{ Status = 'REJECTED'; Reason = ('CACHE_' + $shape.Reason); Definition = $null } }
    [pscustomobject]@{ Status = 'OK'; Definition = $record.definition; MethodVersion = $record.methodVersion }
}

function Write-BnetDiscoveredLayout {
    param([Parameter(Mandatory)][string]$CollectorRoot, [Parameter(Mandatory)][string]$Sha256, [Parameter(Mandatory)][object]$Definition, [Parameter(Mandatory)][string]$MethodVersion)
    $shape = Test-BnetLayoutDefinition -Definition $Definition
    if (-not $shape.Valid) { throw ('DISCOVERY_CACHE_WRITE_REJECTED_' + $shape.Reason) }
    $path = Get-BnetDiscoveredLayoutPath -CollectorRoot $CollectorRoot -Sha256 $Sha256
    $directory = Split-Path -Parent $path; New-Item -ItemType Directory -Force -Path $directory | Out-Null
    $record = [ordered]@{ schemaVersion = $script:DiscoverySchemaVersion; executableSha256 = $Sha256.ToUpperInvariant(); discoveredAtUtc = [DateTime]::UtcNow.ToString('o'); methodVersion = $MethodVersion; validation = [ordered]@{ requiredAnchors = 9; repeatedReadSamples = 2; entryStride = $Definition.entryStride }; definition = $Definition }
    $temp = $path + '.tmp'; $record | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $temp -Encoding UTF8; Move-Item -LiteralPath $temp -Destination $path -Force
    $path
}

Export-ModuleMember -Function ConvertTo-BnetLayoutRva,Test-BnetDiscoverySha,Get-BnetDiscoveredLayoutPath,Test-BnetLayoutDefinition,New-BnetLayoutDefinitionFromAnchors,Select-BnetDiscoveryCandidate,Read-BnetDiscoveredLayout,Write-BnetDiscoveredLayout