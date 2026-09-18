[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateCount(2, 32)]
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })]
    [string[]]$Capture,

    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })]
    [string]$ObservationOverrides,

    [string]$OutputPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-NormalizedPath([string]$Path) {
    [IO.Path]::GetFullPath($Path)
}

function Get-LegacyOrRecordedObservation($CaptureObject) {
    if ($null -ne $CaptureObject.PSObject.Properties['observation']) {
        return [ordered]@{
            uiState = [string]$CaptureObject.observation.uiState
            observedPopulation = $CaptureObject.observation.observedPopulation
            populationVerified = [bool]$CaptureObject.observation.populationVerified
        }
    }
    # Legacy artifacts used this field, but an external annotation can override it.
    return [ordered]@{
        uiState = 'UNKNOWN'
        observedPopulation = $CaptureObject.populationGroundTruth
        populationVerified = $true
    }
}

$overrideByPath = @{}
if ($PSBoundParameters.ContainsKey('ObservationOverrides')) {
    $overrideDocument = Get-Content -LiteralPath $ObservationOverrides -Raw -Encoding UTF8 | ConvertFrom-Json
    foreach ($item in @($overrideDocument.observations)) {
        $overrideByPath[(Get-NormalizedPath $item.capturePath)] = $item
    }
}

$captures = New-Object System.Collections.Generic.List[object]
foreach ($path in $Capture) {
    $normalizedPath = Get-NormalizedPath $path
    $captureObject = Get-Content -LiteralPath $normalizedPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $observation = Get-LegacyOrRecordedObservation $captureObject
    if ($overrideByPath.ContainsKey($normalizedPath)) {
        $override = $overrideByPath[$normalizedPath]
        $observation = [ordered]@{
            uiState = [string]$override.uiState
            observedPopulation = $override.observedPopulation
            populationVerified = [bool]$override.populationVerified
        }
    }
    [void]$captures.Add([pscustomobject]@{ path = $normalizedPath; data = $captureObject; observation = $observation })
}

$reference = $captures[0].data
foreach ($item in $captures) {
    $candidatePages = @($item.data.scope.candidatePageRvas) -join ','
    $referencePages = @($reference.scope.candidatePageRvas) -join ','
    if ($item.data.executable.sha256 -ne $reference.executable.sha256 -or $item.data.scope.pageBytes -ne $reference.scope.pageBytes -or $candidatePages -ne $referencePages) {
        throw 'CANDIDATE_SCALAR_SCOPE_MISMATCH'
    }
}

function Get-ScalarMap($CaptureObject, [string]$Width) {
    $map = @{}
    foreach ($page in @($CaptureObject.pages)) {
        foreach ($field in @($page.$Width)) {
            $map[($page.rva + '/' + $field.offset)] = [pscustomobject]@{ value = $field.value; page = $page.rva; offset = $field.offset }
        }
    }
    return $map
}

function Get-PointerMap($CaptureObject) {
    $map = @{}
    foreach ($page in @($CaptureObject.pages)) {
        foreach ($pointer in @($page.pointers)) {
            $map[($page.rva + '/' + $pointer.offset)] = [pscustomobject]@{ classification = [string]$pointer.classification; targetRva = $pointer.targetRva; page = $page.rva; offset = $pointer.offset }
        }
    }
    return $map
}

function Test-PointerEqual($Left, $Right) {
    $Left.classification -eq $Right.classification -and $Left.targetRva -eq $Right.targetRva
}

function Compare-Maps($Left, $Right, [string]$StableClassification = 'STABLE', [string]$ChangedClassification = 'state-like-small-transition', [switch]$Pointer) {
    $leftKeys = @($Left.Keys)
    $rightKeys = @($Right.Keys)
    $common = @($leftKeys | Where-Object { $Right.ContainsKey($_) })
    $stable = New-Object System.Collections.Generic.List[object]
    $changed = New-Object System.Collections.Generic.List[object]
    foreach ($key in $common) {
        $same = if ($Pointer) { Test-PointerEqual $Left[$key] $Right[$key] } else { $Left[$key].value -eq $Right[$key].value }
        if ($same) {
            [void]$stable.Add([ordered]@{ source = $key; classification = $StableClassification; field = $Left[$key] })
        }
        else {
            [void]$changed.Add([ordered]@{ source = $key; classification = $ChangedClassification; before = $Left[$key]; after = $Right[$key] })
        }
    }
    [ordered]@{
        identical = @($stable.ToArray())
        changed = @($changed.ToArray())
        appeared = @($rightKeys | Where-Object { -not $Left.ContainsKey($_) } | ForEach-Object { $Right[$_] })
        disappeared = @($leftKeys | Where-Object { -not $Right.ContainsKey($_) } | ForEach-Object { $Left[$_] })
    }
}

function Get-StableSecondThirdButFirstDifferent($First, $Second, $Third, [switch]$Pointer) {
    $result = New-Object System.Collections.Generic.List[object]
    foreach ($key in @($Second.Keys | Where-Object { $Third.ContainsKey($_) -and $First.ContainsKey($_) })) {
        $secondThirdEqual = if ($Pointer) { Test-PointerEqual $Second[$key] $Third[$key] } else { $Second[$key].value -eq $Third[$key].value }
        $firstDiffers = if ($Pointer) { -not (Test-PointerEqual $First[$key] $Second[$key]) } else { $First[$key].value -ne $Second[$key].value }
        if ($secondThirdEqual -and $firstDiffers) {
            [void]$result.Add([ordered]@{ source = $key; first = $First[$key]; stableSecondThird = $Second[$key] })
        }
    }
    return @($result.ToArray())
}

function Get-ExactPopulationFollowers([object[]]$Maps, [object[]]$Observations, [switch]$Pointer) {
    if ($Pointer -or @($Observations | Where-Object { -not $_.populationVerified -or $null -eq $_.observedPopulation }).Count -ne 0) {
        return @()
    }
    $result = New-Object System.Collections.Generic.List[object]
    $keys = @($Maps | ForEach-Object { $_.Keys } | Sort-Object -Unique)
    foreach ($key in $keys) {
        if (@($Maps | Where-Object { -not $_.ContainsKey($key) }).Count -ne 0) { continue }
        $matches = $true
        for ($index = 0; $index -lt $Maps.Count; $index++) {
            if ($Maps[$index][$key].value -ne $Observations[$index].observedPopulation) { $matches = $false; break }
        }
        if ($matches) {
            [void]$result.Add([ordered]@{ source = $key; classification = 'exact-population-follower'; values = @($Maps | ForEach-Object { $_[$key].value }) })
        }
    }
    return @($result.ToArray())
}

$uint32Maps = @($captures | ForEach-Object { Get-ScalarMap $_.data 'uint32' })
$uint16Maps = @($captures | ForEach-Object { Get-ScalarMap $_.data 'uint16' })
$pointerMaps = @($captures | ForEach-Object { Get-PointerMap $_.data })
$pairwise = New-Object System.Collections.Generic.List[object]
for ($index = 0; $index -lt $captures.Count - 1; $index++) {
    $leftObservation = $captures[$index].observation
    $rightObservation = $captures[$index + 1].observation
    $stableClassification = if ($leftObservation.populationVerified -and $rightObservation.populationVerified -and $leftObservation.observedPopulation -eq $rightObservation.observedPopulation) { 'STABLE_' + $leftObservation.observedPopulation } else { 'STABLE' }
    $changedClassification = if ($leftObservation.populationVerified -and $rightObservation.populationVerified -and $leftObservation.observedPopulation -eq $rightObservation.observedPopulation) { 'CHURNING' } else { 'state-like-small-transition' }
    [void]$pairwise.Add([ordered]@{
        before = $captures[$index].path
        after = $captures[$index + 1].path
        uint32 = Compare-Maps $uint32Maps[$index] $uint32Maps[$index + 1] -StableClassification $stableClassification -ChangedClassification $changedClassification
        uint16 = Compare-Maps $uint16Maps[$index] $uint16Maps[$index + 1] -StableClassification $stableClassification -ChangedClassification $changedClassification
        pointers = Compare-Maps $pointerMaps[$index] $pointerMaps[$index + 1] -StableClassification $stableClassification -ChangedClassification $changedClassification -Pointer
    })
}

$triple = $null
if ($captures.Count -eq 3) {
    $triple = [ordered]@{
        assumption = 'first, second, third capture order'
        uint32StableSecondThirdButFirstDifferent = Get-StableSecondThirdButFirstDifferent $uint32Maps[0] $uint32Maps[1] $uint32Maps[2]
        uint16StableSecondThirdButFirstDifferent = Get-StableSecondThirdButFirstDifferent $uint16Maps[0] $uint16Maps[1] $uint16Maps[2]
        pointersStableSecondThirdButFirstDifferent = Get-StableSecondThirdButFirstDifferent $pointerMaps[0] $pointerMaps[1] $pointerMaps[2] -Pointer
    }
}

$result = [ordered]@{
    schemaVersion = 2
    sha256 = $reference.executable.sha256
    captures = @($captures | ForEach-Object { [ordered]@{ path = $_.path; observation = $_.observation } })
    pairwise = @($pairwise.ToArray())
    exactPopulationFollowers = [ordered]@{
        uint32 = Get-ExactPopulationFollowers -Maps $uint32Maps -Observations @($captures | ForEach-Object { $_.observation })
        uint16 = Get-ExactPopulationFollowers -Maps $uint16Maps -Observations @($captures | ForEach-Object { $_.observation })
    }
    triple = $triple
    privacy = [ordered]@{ rawPageBytesStored = $false; stringsStored = $false; playerNamesStored = $false; playerXuidsStored = $false; playerRecordBytesStored = $false }
}
if (-not $PSBoundParameters.ContainsKey('OutputPath')) {
    $OutputPath = Join-Path (Split-Path -Parent $captures[$captures.Count - 1].path) 'candidate-scalar-comparison.json'
}
$result | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $OutputPath -Encoding UTF8
Write-Output ('CANDIDATE_SCALAR_COMPARISON_READY sha={0} captures={1} output={2}' -f $result.sha256, $captures.Count, $OutputPath)
