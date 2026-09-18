[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })][string]$CaptureA,
    [Parameter(Mandatory)][ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })][string]$CaptureB,
    [Parameter(Mandatory)][ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })][string]$CaptureC,
    [Parameter(Mandatory)][ValidateRange(0,250)][int]$PopulationA,
    [Parameter(Mandatory)][ValidateRange(0,250)][int]$PopulationB,
    [Parameter(Mandatory)][ValidateRange(0,250)][int]$PopulationC
)

Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'

function Read-TargetedCapture([string]$Path) { Get-Content -LiteralPath $Path -Raw -Encoding UTF8 | ConvertFrom-Json }
function Test-TargetedScopeEqual($Left,$Right) {
    $Left.executable.sha256 -eq $Right.executable.sha256 -and
    $Left.scope.rvaStart -eq $Right.scope.rvaStart -and
    $Left.scope.rvaEndExclusive -eq $Right.scope.rvaEndExclusive -and
    $Left.scope.blockBytes -eq $Right.scope.blockBytes
}

$a=Read-TargetedCapture $CaptureA; $b=Read-TargetedCapture $CaptureB; $c=Read-TargetedCapture $CaptureC
if($a.label -ne 'season06_targeted_populated' -or $b.label -ne 'season06_targeted_empty' -or $c.label -ne 'season06_targeted_populated'){throw 'TARGETED_TRIANGULATION_LABEL_INVALID'}
if(-not (Test-TargetedScopeEqual $a $b) -or -not (Test-TargetedScopeEqual $a $c)){throw 'TARGETED_TRIANGULATION_SCOPE_MISMATCH'}
$am=@{};$bm=@{};$cm=@{};foreach($block in @($a.blocks)){$am[$block.rva]=$block};foreach($block in @($b.blocks)){$bm[$block.rva]=$block};foreach($block in @($c.blocks)){$cm[$block.rva]=$block}
if($am.Count -ne $bm.Count -or $am.Count -ne $cm.Count){throw 'TARGETED_TRIANGULATION_BLOCK_SET_MISMATCH'}
$pages=New-Object System.Collections.Generic.List[object]
foreach($rva in @($am.Keys|Sort-Object {[convert]::ToInt64($_,16)})){
    if(-not $bm.ContainsKey($rva) -or -not $cm.ContainsKey($rva)){throw 'TARGETED_TRIANGULATION_RVA_MISMATCH'}
    $ab=$am[$rva].sha256 -ne $bm[$rva].sha256; $bc=$bm[$rva].sha256 -ne $cm[$rva].sha256; $ac=$am[$rva].sha256 -ne $cm[$rva].sha256
    $pattern=if($ab -and $bc -and $ac){'all-three-different'}elseif($ab -and $bc){'a-equals-c'}elseif($ab -and $ac){'b-equals-c'}elseif($bc -and $ac){'a-equals-b'}else{'unchanged-all'}
    [void]$pages.Add([ordered]@{rva=$rva;size=$am[$rva].size;pattern=$pattern;changedAtoB=$ab;changedBtoC=$bc;changedAtoC=$ac})
}
$result=[ordered]@{schemaVersion=1;executable=[ordered]@{sha256=$a.executable.sha256;captureAPid=$a.executable.pid;captureBPid=$b.executable.pid;captureCPid=$c.executable.pid};scope=$a.scope;populationGroundTruth=[ordered]@{a=$PopulationA;b=$PopulationB;c=$PopulationC};captures=[ordered]@{a=$CaptureA;b=$CaptureB;c=$CaptureC};pages=@($pages.ToArray());privacy=[ordered]@{playerNamesStored=$false;playerXuidsStored=$false;playerRecordBytesStored=$false;rawPageBytesStored=$false}}
$path=Join-Path (Split-Path -Parent $CaptureC) 'targeted-temporal-triangulation.json';$result|ConvertTo-Json -Depth 8|Set-Content -LiteralPath $path -Encoding UTF8
$strong=@($result.pages|Where-Object{$_.pattern -eq 'all-three-different'}).Count
Write-Output ('TARGETED_TEMPORAL_TRIANGULATION_READY sha={0} strongPages={1} output={2}' -f $a.executable.sha256,$strong,$path)
