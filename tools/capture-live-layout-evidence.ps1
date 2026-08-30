[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })]
    [string]$CodPath,
    [string]$OutputRoot = (Join-Path $env:LOCALAPPDATA 'CODMunity\Top250Collector\research\layout-evidence'),
    [ValidateRange(1, 256)]
    [int]$WindowBytes = 64,
    [ValidateRange(1, 256)]
    [int]$MaxCodeScanMiB = 64
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$knownAnchors = [ordered]@{
    mp = [ordered]@{ stateRva = 0x12895050L; countRva = 0x128D079CL; entry0Rva = 0x128D07A0L }
    br = [ordered]@{ stateRva = 0x12917140L; countRva = 0x1295288CL; entry0Rva = 0x12952890L }
    resurgence = [ordered]@{ stateRva = 0x12999230L; countRva = 0x129D497CL; entry0Rva = 0x129D4980L }
}
function Format-LayoutHex([Int64]$Value) { '0x{0:X}' -f $Value }
function Convert-BytesToHex([byte[]]$Bytes) { if ($null -eq $Bytes) { return $null }; [BitConverter]::ToString($Bytes).Replace('-', ' ') }
function Get-Sha256([byte[]]$Bytes) {
    if ($null -eq $Bytes) { return $null }
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try { return Convert-BytesToHex $algorithm.ComputeHash($Bytes) -replace ' ', '' }
    finally { $algorithm.Dispose() }
}

if (-not ('LayoutEvidenceLiveNative' -as [type])) {
    Add-Type @"
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
public sealed class LayoutEvidenceReadResult { public byte[] Bytes = new byte[0]; public int BytesRead; public int Error; }
public sealed class LayoutEvidenceRegion { public long BaseAddress; public long AllocationBase; public long RegionSize; public uint State; public uint Protect; public uint AllocationProtect; public uint Type; }
public sealed class LayoutEvidenceLiveReference { public long SourceAddress; public long TargetAddress; public int AnchorIndex; public string Kind = ""; public byte[] InstructionBytes = new byte[0]; }
public sealed class LayoutEvidenceScanResult { public LayoutEvidenceLiveReference[] References = new LayoutEvidenceLiveReference[0]; public long ScannedBytes; public int ReadFailures; }
public static class LayoutEvidenceLiveNative {
    public const uint PROCESS_VM_READ = 0x0010, PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    [DllImport("kernel32.dll", SetLastError=true)] public static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] public static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] public static extern bool ReadProcessMemory(IntPtr process, IntPtr address, [Out] byte[] buffer, UIntPtr size, out UIntPtr bytesRead);
    [DllImport("kernel32.dll", SetLastError=true)] public static extern IntPtr VirtualQueryEx(IntPtr process, IntPtr address, out MEMORY_BASIC_INFORMATION info, UIntPtr length);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] public static extern bool QueryFullProcessImageName(IntPtr process, int flags, System.Text.StringBuilder name, ref int size);
    [StructLayout(LayoutKind.Sequential)] public struct MEMORY_BASIC_INFORMATION { public IntPtr BaseAddress; public IntPtr AllocationBase; public uint AllocationProtect; public ushort PartitionId; public ushort Reserved0; public UIntPtr RegionSize; public uint State; public uint Protect; public uint Type; }
    public static LayoutEvidenceReadResult Read(IntPtr process, long address, int count) { var bytes = new byte[count]; UIntPtr read; var ok=ReadProcessMemory(process,new IntPtr(address),bytes,(UIntPtr)count,out read); var readCount=(int)read.ToUInt64(); if(readCount != count) Array.Resize(ref bytes, Math.Max(0,readCount)); return new LayoutEvidenceReadResult { Bytes=bytes, BytesRead=readCount, Error=ok && readCount==count ? 0 : Marshal.GetLastWin32Error() }; }
    public static LayoutEvidenceRegion Query(IntPtr process, long address) { MEMORY_BASIC_INFORMATION info; var result=VirtualQueryEx(process,new IntPtr(address),out info,(UIntPtr)Marshal.SizeOf(typeof(MEMORY_BASIC_INFORMATION))); if(result==IntPtr.Zero) return null; return new LayoutEvidenceRegion { BaseAddress=info.BaseAddress.ToInt64(), AllocationBase=info.AllocationBase.ToInt64(), RegionSize=(long)info.RegionSize.ToUInt64(), State=info.State, Protect=info.Protect, AllocationProtect=info.AllocationProtect, Type=info.Type }; }
    public static string ImagePath(IntPtr process) { var capacity=32768; var value=new System.Text.StringBuilder(capacity); var size=capacity; return QueryFullProcessImageName(process,0,value,ref size) ? value.ToString() : null; }
    public static LayoutEvidenceScanResult FindRipReferences(IntPtr process, long[] starts, long[] sizes, long[] anchors, long nearBytes, long maxBytes) {
        var refs=new List<LayoutEvidenceLiveReference>(); var seen=new HashSet<string>(); long scanned=0; var failures=0; const int Chunk=1024*1024;
        for(int region=0;region<starts.Length && scanned<maxBytes;region++) { long remaining=Math.Min(sizes[region],maxBytes-scanned), consumed=0; var carry=new byte[0];
            while(remaining>0) { int take=(int)Math.Min(Chunk,remaining); var rr=Read(process,starts[region]+consumed,take); if(rr.BytesRead != take) { failures++; break; }
                var buffer=new byte[carry.Length+rr.Bytes.Length]; if(carry.Length>0) Buffer.BlockCopy(carry,0,buffer,0,carry.Length); Buffer.BlockCopy(rr.Bytes,0,buffer,carry.Length,rr.Bytes.Length); var bufferAddress=starts[region]+consumed-carry.Length;
                for(int i=0;i<buffer.Length;i++) { string kind=null; int dispOffset=0,length=0; if(i+7<=buffer.Length && (buffer[i]&0xF0)==0x40 && (buffer[i+1]==0x8D || buffer[i+1]==0x8B) && (buffer[i+2]&0xC7)==0x05) { kind=buffer[i+1]==0x8D?"LEA_RIP_REL32":"MOV_RIP_REL32";dispOffset=3;length=7; } else if(i+7<=buffer.Length && buffer[i]==0x83 && buffer[i+1]==0x3D) {kind="CMP_RIP_REL32_IMM8";dispOffset=2;length=7;} else if(i+10<=buffer.Length && buffer[i]==0xC7 && buffer[i+1]==0x05) {kind="MOV_RIP_REL32_IMM32";dispOffset=2;length=10;} if(kind==null) continue;
                    long source=bufferAddress+i, target=source+length+BitConverter.ToInt32(buffer,i+dispOffset); for(int anchor=0;anchor<anchors.Length;anchor++) { if(Math.Abs(target-anchors[anchor])>nearBytes) continue; var key=source.ToString()+"/"+anchor.ToString(); if(!seen.Add(key)) continue; var instruction=new byte[length];Buffer.BlockCopy(buffer,i,instruction,0,length);refs.Add(new LayoutEvidenceLiveReference{SourceAddress=source,TargetAddress=target,AnchorIndex=anchor,Kind=kind,InstructionBytes=instruction}); }
                }
                var keep=Math.Min(9,buffer.Length); carry=new byte[keep]; if(keep>0)Buffer.BlockCopy(buffer,buffer.Length-keep,carry,0,keep); remaining-=take;consumed+=take;scanned+=take;
            }
        }
        return new LayoutEvidenceScanResult { References=refs.ToArray(), ScannedBytes=scanned, ReadFailures=failures };
    }
}
"@
}
function Test-ReadableProtection([uint32]$Protect) { (($Protect -band 0x100) -eq 0) -and (($Protect -band 0xFF) -ne 0x01) -and (($Protect -band 0xFF) -ne 0) }
function Test-ExecutableProtection([uint32]$Protect) { $base=$Protect -band 0xFF; $base -in 0x10,0x20,0x40,0x80 }
function Get-CodProcessByPath {
    param([string]$Path)
    $resolved=[IO.Path]::GetFullPath($Path)
    foreach($process in @(Get-Process -Name cod -ErrorAction SilentlyContinue)) { try { if([string]::Equals([IO.Path]::GetFullPath($process.Path),$resolved,[StringComparison]::OrdinalIgnoreCase)) { return $process } } catch {} }
    return $null
}
function Get-LiveAnchorEvidence {
    param([IntPtr]$Handle,[Int64]$ModuleBase,[string]$Mode,[string]$Field,[Int64]$Rva,[int]$Radius)
    $address=$ModuleBase+$Rva; $region=[LayoutEvidenceLiveNative]::Query($Handle,$address)
    if($null -eq $region) { return [ordered]@{ mode=$Mode;field=$Field;rva=Format-LayoutHex $Rva;absoluteVa=Format-LayoutHex $address;readable=$false;reason='VIRTUAL_QUERY_FAILED' } }
    $readable=($region.State -eq 0x1000) -and (Test-ReadableProtection $region.Protect)
    $before=if($Field -eq 'entry0Rva'){16}else{$Radius}; $after=if($Field -eq 'entry0Rva'){32}else{$Radius}; $start=[Math]::Max([int64]$region.BaseAddress,$address-$before); $requested=[int][Math]::Min([int64]($before+$after+1),($region.BaseAddress+$region.RegionSize)-$start)
    $result=if($readable -and $requested -gt 0){[LayoutEvidenceLiveNative]::Read($Handle,$start,$requested)}else{$null}
    $captured=if($null -eq $result){$null}else{$result.Bytes}; $redacted=($Field -eq 'entry0Rva' -or $Field -eq 'countRva')
    [ordered]@{ mode=$Mode;field=$Field;rva=Format-LayoutHex $Rva;absoluteVa=Format-LayoutHex $address;region=[ordered]@{base=Format-LayoutHex $region.BaseAddress;size=$region.RegionSize;state=('0x{0:X}' -f $region.State);protect=('0x{0:X}' -f $region.Protect);type=('0x{0:X}' -f $region.Type)};readable=$readable;windowStartVa=Format-LayoutHex $start;windowBefore=$address-$start;windowAfter=$after;capturedBytes=if($null -eq $captured){0}else{$captured.Length};windowSha256=Get-Sha256 $captured;bytes=if($redacted){$null}else{Convert-BytesToHex $captured};contentRedacted=$redacted;reason=if($null -eq $result){if($readable){'READ_FAILED'}else{'REGION_NOT_READABLE'}}elseif($result.BytesRead -ne $requested){'PARTIAL_READ'}else{$null} }
}
function Get-ExecutableRegions {
    param([IntPtr]$Handle,[Int64]$ModuleBase,[Int64]$ModuleSize,[Int64]$MaxBytes)
    $regions=New-Object System.Collections.Generic.List[object]; $address=$ModuleBase; $end=$ModuleBase+$ModuleSize; $used=0L
    while($address -lt $end -and $used -lt $MaxBytes) { $region=[LayoutEvidenceLiveNative]::Query($Handle,$address); if($null -eq $region -or $region.RegionSize -le 0){break}; $next=$region.BaseAddress+$region.RegionSize; if($region.State -eq 0x1000 -and (Test-ExecutableProtection $region.Protect)) { $take=[Math]::Min($region.RegionSize,$MaxBytes-$used); if($take -gt 0){$regions.Add([pscustomobject]@{base=[int64]$region.BaseAddress;size=[int64]$take;protect=[uint32]$region.Protect});$used+=$take} }; if($next -le $address){break};$address=$next }
    return [object[]]$regions.ToArray()
}

$resolvedPath=[IO.Path]::GetFullPath($CodPath); $process=Get-CodProcessByPath -Path $resolvedPath
if($null -eq $process){Write-Output 'LIVE_EVIDENCE_SKIPPED reason=COD_NOT_RUNNING'; exit 0}
$staticTool = Join-Path $PSScriptRoot 'capture-layout-evidence.ps1'
try { $staticOutput = @(& $staticTool -CodPath $resolvedPath -OutputRoot $OutputRoot) }
catch { throw ('STATIC_EVIDENCE_CAPTURE_FAILED type=' + $_.Exception.GetType().Name) }
$match=[regex]::Match(($staticOutput -join "`n"),'json=(?<json>.+?) summary='); if(-not $match.Success){throw 'STATIC_EVIDENCE_OUTPUT_UNRECOGNIZED'}; $evidenceDirectory=Split-Path -Parent $match.Groups['json'].Value.Trim()
$sha=(Get-FileHash -LiteralPath $resolvedPath -Algorithm SHA256).Hash.ToUpperInvariant(); $moduleBase=[int64]$process.MainModule.BaseAddress.ToInt64(); $moduleSize=[int64]$process.MainModule.ModuleMemorySize
$handle=[LayoutEvidenceLiveNative]::OpenProcess(([LayoutEvidenceLiveNative]::PROCESS_VM_READ -bor [LayoutEvidenceLiveNative]::PROCESS_QUERY_LIMITED_INFORMATION),$false,$process.Id); if($handle -eq [IntPtr]::Zero){throw ('OPEN_PROCESS_FAILED win32Error='+[Runtime.InteropServices.Marshal]::GetLastWin32Error())}
try {
    $anchorRecords=New-Object System.Collections.Generic.List[object]; $addresses=New-Object System.Collections.Generic.List[Int64]; $keys=New-Object System.Collections.Generic.List[string]
    foreach($mode in $knownAnchors.Keys){foreach($field in $knownAnchors[$mode].Keys){$rva=[int64]$knownAnchors[$mode][$field];$anchorRecords.Add((Get-LiveAnchorEvidence -Handle $handle -ModuleBase $moduleBase -Mode $mode -Field $field -Rva $rva -Radius $WindowBytes));$addresses.Add($moduleBase+$rva);$keys.Add("$mode/$field")}}
    $regions=Get-ExecutableRegions -Handle $handle -ModuleBase $moduleBase -ModuleSize $moduleSize -MaxBytes ([int64]$MaxCodeScanMiB*1MB); $starts=[int64[]]@($regions|ForEach-Object{$_.base});$sizes=[int64[]]@($regions|ForEach-Object{$_.size});$scan=[LayoutEvidenceLiveNative]::FindRipReferences($handle,$starts,$sizes,[int64[]]$addresses,0x200,[int64]$MaxCodeScanMiB*1MB)
    $references=@($scan.References|ForEach-Object{[ordered]@{anchor=$keys[$_.AnchorIndex];kind=$_.Kind;sourceVa=Format-LayoutHex $_.SourceAddress;sourceRva=Format-LayoutHex ($_.SourceAddress-$moduleBase);targetVa=Format-LayoutHex $_.TargetAddress;targetRva=Format-LayoutHex ($_.TargetAddress-$moduleBase);instructionBytes=Convert-BytesToHex $_.InstructionBytes;trust='NOT_TRUSTED_ONE_BUILD'}})
    $live=[ordered]@{schemaVersion=1;capturedAtUtc=[DateTime]::UtcNow.ToString('o');source='live-read-only';executable=[ordered]@{sha256=$sha;pid=$process.Id;moduleBase=Format-LayoutHex $moduleBase;moduleSize=$moduleSize};anchors=[object[]]$anchorRecords.ToArray();relationships=[ordered]@{entry0MinusCount=4;stateToCount=0x3B74C;modeSpacing=0x820F0;entryStride=0x458};privacy=[ordered]@{playerDataStored=$false;entry0BytesRedacted=$true;countWindowBytesRedacted=$true}}
    $codeRegions = @($regions | ForEach-Object {
        [ordered]@{ base = Format-LayoutHex $_.base; size = $_.size; protect = ('0x{0:X}' -f $_.protect) }
    })
    $researchCandidates = @($references | ForEach-Object {
        [ordered]@{
            anchor = $_.anchor; kind = $_.kind; instructionBytes = $_.instructionBytes
            wildcards = 'Relocation-sensitive disp32 only after cross-build comparison'
            resolvesTo = $_.targetRva; trust = 'NOT_TRUSTED_ONE_BUILD'
            whyMaySurvive = 'Opcode and addressing form can be compared across builds.'
            whyMayFail = 'Compiler, inlining, relocation, indirection, or code-path changes may alter it.'
        }
    })
    $referencedAnchors = @($references | ForEach-Object { $_.anchor } | Sort-Object -Unique)
    $crossModeStructure = if ($referencedAnchors.Count -ge 3) { 'Multiple mode anchors have research-only direct reference hits; compare exact structure across builds.' } elseif ($referencedAnchors.Count -eq 1) { 'Only one mode anchor has direct reference hits; no cross-mode code structure is established.' } else { 'No recognised direct RIP-relative anchor reference found in bounded executable regions.' }
    $analysis = [ordered]@{
        schemaVersion = 1; capturedAtUtc = [DateTime]::UtcNow.ToString('o')
        method = 'bounded mapped executable-region direct RIP-relative scan'; codeRegions = $codeRegions
        scannedBytes = $scan.ScannedBytes; scanLimitBytes = ([int64]$MaxCodeScanMiB * 1MB); boundedScan = $true; readFailures = $scan.ReadFailures
        references = $references; pointerChains = @(); referencedAnchors = $referencedAnchors
        crossModeStructure = $crossModeStructure; researchCandidates = $researchCandidates
    }
    $live|ConvertTo-Json -Depth 12|Set-Content -LiteralPath (Join-Path $evidenceDirectory 'live-memory-evidence.json') -Encoding UTF8; $analysis|ConvertTo-Json -Depth 12|Set-Content -LiteralPath (Join-Path $evidenceDirectory 'reference-analysis.json') -Encoding UTF8
    Add-Content -LiteralPath (Join-Path $evidenceDirectory 'layout-evidence.txt') -Value ("Live read-only anchors={0}; executableScanBytes={1}; references={2}; playerDataStored=false" -f $anchorRecords.Count,$scan.ScannedBytes,$references.Count) -Encoding UTF8
    Write-Output ("LIVE_EVIDENCE_CAPTURED sha={0} output={1} anchors={2} references={3}" -f $sha,$evidenceDirectory,$anchorRecords.Count,$references.Count)
}
finally {[void][LayoutEvidenceLiveNative]::CloseHandle($handle)}