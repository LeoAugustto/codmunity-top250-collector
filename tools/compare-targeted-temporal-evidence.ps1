[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })][string]$EmptyCapture,
    [Parameter(Mandatory)][ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })][string]$PopulatedCapture
)

Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$empty=Get-Content -LiteralPath $EmptyCapture -Raw -Encoding UTF8|ConvertFrom-Json
$populated=Get-Content -LiteralPath $PopulatedCapture -Raw -Encoding UTF8|ConvertFrom-Json
if($empty.label -ne 'season06_targeted_empty' -or $populated.label -ne 'season06_targeted_populated'){throw 'TARGETED_TEMPORAL_LABEL_INVALID'}
if($empty.executable.sha256 -ne $populated.executable.sha256){throw 'TARGETED_TEMPORAL_SHA_MISMATCH'}
if($empty.scope.rvaStart -ne $populated.scope.rvaStart -or $empty.scope.rvaEndExclusive -ne $populated.scope.rvaEndExclusive -or $empty.scope.blockBytes -ne $populated.scope.blockBytes){throw 'TARGETED_TEMPORAL_SCOPE_MISMATCH'}
$before=@{};foreach($block in @($empty.blocks)){$before[($block.rva+'/'+$block.size)]=$block}
$after=@{};foreach($block in @($populated.blocks)){$after[($block.rva+'/'+$block.size)]=$block}
$changes=New-Object System.Collections.Generic.List[object]
foreach($key in @($before.Keys+$after.Keys|Sort-Object -Unique)){$a=$before[$key];$b=$after[$key];$class=if($null -eq $a){'newly-present'}elseif($null -eq $b){'disappeared'}elseif(-not $a.readable -or -not $b.readable){'unreadable'}elseif($a.sha256 -eq $b.sha256){'unchanged'}else{'hash-changed'};[void]$changes.Add([ordered]@{rva=if($null-ne$b){$b.rva}else{$a.rva};size=if($null-ne$b){$b.size}else{$a.size};classification=$class;emptySmallUint32Samples=if($null-ne$a){@($a.smallUint32Samples)}else{@()};populatedSmallUint32Samples=if($null-ne$b){@($b.smallUint32Samples)}else{@()}})}
$result=[ordered]@{schemaVersion=1;sha256=$empty.executable.sha256;emptyCapturedAtUtc=$empty.capturedAtUtc;populatedCapturedAtUtc=$populated.capturedAtUtc;scope=$empty.scope;changes=@($changes.ToArray());privacy=[ordered]@{playerNamesStored=$false;playerXuidsStored=$false;playerRecordBytesStored=$false}}
$path=Join-Path (Split-Path -Parent $PopulatedCapture) 'targeted-temporal-comparison.json';$result|ConvertTo-Json -Depth 8|Set-Content -LiteralPath $path -Encoding UTF8
Write-Output ('TARGETED_TEMPORAL_COMPARISON_READY sha={0} changedBlocks={1} output={2}' -f $result.sha256,@($result.changes|Where-Object{$_.classification -eq 'hash-changed'}).Count,$path)
