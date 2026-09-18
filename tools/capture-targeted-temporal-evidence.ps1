[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })]
    [string]$CodPath,
    [Parameter(Mandatory)]
    [ValidateSet('season06_targeted_empty', 'season06_targeted_populated')]
    [string]$Label,
    [Int64]$RvaStart = 0x128BE000L,
    [Int64]$RvaEndExclusive = 0x12DBE000L,
    [ValidateSet(4096, 256)]
    [int]$BlockBytes = 4096,
    [string]$OutputRoot = (Join-Path $env:LOCALAPPDATA 'CODMunity\Top250Collector\research\layout-evidence')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($RvaStart -lt 0 -or $RvaEndExclusive -le $RvaStart -or (($RvaEndExclusive - $RvaStart) % $BlockBytes) -ne 0) { throw 'TARGETED_RVA_RANGE_INVALID' }

if (-not ('Top250TargetedTemporalNative' -as [type])) {
    Add-Type @"
using System;
using System.Runtime.InteropServices;
public sealed class Top250TargetedTemporalRegion { public long BaseAddress, RegionSize; public uint State, Protect, Type; }
public sealed class Top250TargetedTemporalRead { public byte[] Bytes; public int BytesRead, Error; }
public static class Top250TargetedTemporalNative {
    public const uint PROCESS_VM_READ = 0x0010, PROCESS_QUERY_LIMITED_INFORMATION = 0x1000, MEM_COMMIT = 0x1000, PAGE_GUARD = 0x100, PAGE_NOACCESS = 0x01;
    [DllImport("kernel32.dll", SetLastError=true)] public static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] public static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] public static extern bool ReadProcessMemory(IntPtr process, IntPtr address, byte[] buffer, UIntPtr size, out UIntPtr bytesRead);
    [DllImport("kernel32.dll", SetLastError=true)] public static extern IntPtr VirtualQueryEx(IntPtr process, IntPtr address, out MEMORY_BASIC_INFORMATION info, UIntPtr length);
    [DllImport("ntdll.dll")] static extern int NtQueryInformationProcess(IntPtr process, int processInformationClass, out PROCESS_BASIC_INFORMATION processInformation, int processInformationLength, out int returnLength);
    [StructLayout(LayoutKind.Sequential)] public struct MEMORY_BASIC_INFORMATION { public IntPtr BaseAddress, AllocationBase; public uint AllocationProtect; public ushort PartitionId, Reserved0; public UIntPtr RegionSize; public uint State, Protect, Type; }
    [StructLayout(LayoutKind.Sequential)] struct PROCESS_BASIC_INFORMATION { public IntPtr Reserved1, PebBaseAddress, Reserved2_0, Reserved2_1, UniqueProcessId, Reserved3; }
    public static Top250TargetedTemporalRegion Query(IntPtr process, long address) { MEMORY_BASIC_INFORMATION info; if (VirtualQueryEx(process, new IntPtr(address), out info, (UIntPtr)Marshal.SizeOf(typeof(MEMORY_BASIC_INFORMATION))) == IntPtr.Zero) return null; return new Top250TargetedTemporalRegion { BaseAddress=info.BaseAddress.ToInt64(), RegionSize=(long)info.RegionSize.ToUInt64(), State=info.State, Protect=info.Protect, Type=info.Type }; }
    public static Top250TargetedTemporalRead Read(IntPtr process, long address, int count) { var bytes=new byte[count]; UIntPtr read; bool ok=ReadProcessMemory(process,new IntPtr(address),bytes,(UIntPtr)count,out read); int actual=(int)read.ToUInt64(); if(actual!=count) Array.Resize(ref bytes, Math.Max(0,actual)); return new Top250TargetedTemporalRead { Bytes=bytes, BytesRead=actual, Error=(ok && actual==count) ? 0 : Marshal.GetLastWin32Error() }; }
    public static long GetImageBaseFromPeb(IntPtr process) { PROCESS_BASIC_INFORMATION info; int returned; if(NtQueryInformationProcess(process,0,out info,Marshal.SizeOf(typeof(PROCESS_BASIC_INFORMATION)),out returned)!=0 || info.PebBaseAddress==IntPtr.Zero) return 0; var read=Read(process,info.PebBaseAddress.ToInt64()+0x10,IntPtr.Size); if(read.BytesRead!=IntPtr.Size) return 0; return IntPtr.Size==8 ? BitConverter.ToInt64(read.Bytes,0) : BitConverter.ToInt32(read.Bytes,0); }
}
"@
}

function Format-TargetedHex([Int64]$Value) { '0x{0:X}' -f $Value }
function Get-TargetedSha256([byte[]]$Bytes) {
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try { ([BitConverter]::ToString($algorithm.ComputeHash($Bytes))).Replace('-', '') }
    finally { $algorithm.Dispose() }
}
function Get-TargetedFileSha256([string]$Path) {
    $algorithm = [Security.Cryptography.SHA256]::Create()
    $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try { ([BitConverter]::ToString($algorithm.ComputeHash($stream))).Replace('-', '') }
    finally { $stream.Dispose(); $algorithm.Dispose() }
}
function Get-TargetedPeImageSize([string]$Path) {
    $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try { $header=New-Object byte[] 4096; [void]$stream.Read($header,0,$header.Length); $pe=[BitConverter]::ToInt32($header,0x3C); if([BitConverter]::ToUInt16($header,0)-ne 0x5A4D -or [BitConverter]::ToUInt32($header,$pe)-ne 0x4550){throw 'PE_SIGNATURE_INVALID'}; [Int64][BitConverter]::ToUInt32($header,$pe+24+0x38) }
    finally { $stream.Dispose() }
}
function Test-TargetedReadableWritable([uint32]$Protect) {
    $base=$Protect -band 0xFF
    (($Protect -band [Top250TargetedTemporalNative]::PAGE_GUARD) -eq 0) -and $base -ne 0 -and $base -ne [Top250TargetedTemporalNative]::PAGE_NOACCESS -and (($base -band 0x04) -ne 0 -or ($base -band 0x08) -ne 0 -or ($base -band 0x40) -ne 0 -or ($base -band 0x80) -ne 0)
}
function Get-TargetedCodProcess([string]$Path) {
    $resolved=[IO.Path]::GetFullPath($Path)
    $candidate=@(Get-CimInstance Win32_Process -Filter "Name='cod.exe'" | Where-Object { $_.ExecutablePath -and [string]::Equals([IO.Path]::GetFullPath($_.ExecutablePath),$resolved,[StringComparison]::OrdinalIgnoreCase) } | Select-Object -First 1)
    if($candidate.Count -eq 0){return $null}
    Get-Process -Id $candidate[0].ProcessId -ErrorAction Stop
}

$process=Get-TargetedCodProcess $CodPath
if($null -eq $process){Write-Output 'TARGETED_TEMPORAL_CAPTURE_SKIPPED reason=COD_NOT_RUNNING';exit 0}
$sha=(Get-TargetedFileSha256 $CodPath).ToUpperInvariant()
$imageSize=Get-TargetedPeImageSize $CodPath
if($RvaEndExclusive -gt $imageSize){throw 'TARGETED_RVA_OUTSIDE_IMAGE'}
$access=[Top250TargetedTemporalNative]::PROCESS_VM_READ -bor [Top250TargetedTemporalNative]::PROCESS_QUERY_LIMITED_INFORMATION
$handle=[Top250TargetedTemporalNative]::OpenProcess($access,$false,$process.Id)
if($handle -eq [IntPtr]::Zero){throw ('OPEN_PROCESS_FAILED win32Error='+[Runtime.InteropServices.Marshal]::GetLastWin32Error())}
try {
    $moduleBase=[Int64][Top250TargetedTemporalNative]::GetImageBaseFromPeb($handle)
    if($moduleBase -le 0){throw 'MAIN_MODULE_IDENTITY_UNAVAILABLE'}
    $blocks=New-Object System.Collections.Generic.List[object]
    $readableBlocks=0; $unreadableBlocks=0
    for($rva=$RvaStart;$rva -lt $RvaEndExclusive;$rva+=$BlockBytes){
        $address=$moduleBase+$rva; $region=[Top250TargetedTemporalNative]::Query($handle,$address)
        $eligible=($null -ne $region) -and $region.State -eq [Top250TargetedTemporalNative]::MEM_COMMIT -and (Test-TargetedReadableWritable $region.Protect) -and (($region.BaseAddress+$region.RegionSize)-ge($address+$BlockBytes))
        if(-not $eligible){$unreadableBlocks++;[void]$blocks.Add([ordered]@{rva=Format-TargetedHex $rva;size=$BlockBytes;state=if($null -eq $region){$null}else{('0x{0:X}'-f $region.State)};protect=if($null -eq $region){$null}else{('0x{0:X}'-f $region.Protect)};type=if($null -eq $region){$null}else{('0x{0:X}'-f $region.Type)};readable=$false;readError=$null;sha256=$null;smallUint32Samples=@()});continue}
        $read=[Top250TargetedTemporalNative]::Read($handle,$address,$BlockBytes)
        if($read.BytesRead -ne $BlockBytes){$unreadableBlocks++;[void]$blocks.Add([ordered]@{rva=Format-TargetedHex $rva;size=$BlockBytes;state=('0x{0:X}'-f $region.State);protect=('0x{0:X}'-f $region.Protect);type=('0x{0:X}'-f $region.Type);readable=$false;readError=$read.Error;sha256=$null;smallUint32Samples=@()});continue}
        $small=New-Object System.Collections.Generic.List[object]
        for($offset=0;$offset -le $read.Bytes.Length-4;$offset+=4){$value=[BitConverter]::ToUInt32($read.Bytes,$offset);if($value -ge 1 -and $value -le 250 -and $small.Count -lt 64){[void]$small.Add([ordered]@{offset=('0x{0:X}'-f $offset);value=$value})}}
        $readableBlocks++;[void]$blocks.Add([ordered]@{rva=Format-TargetedHex $rva;size=$BlockBytes;state=('0x{0:X}'-f $region.State);protect=('0x{0:X}'-f $region.Protect);type=('0x{0:X}'-f $region.Type);readable=$true;readError=$null;sha256=Get-TargetedSha256 $read.Bytes;smallUint32Samples=@($small.ToArray())})
    }
    $timestamp=[DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss');$out=Join-Path (Join-Path $OutputRoot $sha) ($timestamp+'-'+$Label);New-Item -ItemType Directory -Force -Path $out|Out-Null
    $capture=[ordered]@{schemaVersion=1;capturedAtUtc=[DateTime]::UtcNow.ToString('o');label=$Label;source='live-read-only-targeted-module-writable-temporal';executable=[ordered]@{sha256=$sha;pid=$process.Id;moduleBase=Format-TargetedHex $moduleBase;moduleSize=$imageSize};scope=[ordered]@{moduleRelative=$true;rvaStart=Format-TargetedHex $RvaStart;rvaEndExclusive=Format-TargetedHex $RvaEndExclusive;blockBytes=$BlockBytes;writableCommittedOnly=$true;readableBlocks=$readableBlocks;unreadableBlocks=$unreadableBlocks};blocks=@($blocks.ToArray());privacy=[ordered]@{playerNamesStored=$false;playerXuidsStored=$false;playerRecordBytesStored=$false}}
    $path=Join-Path $out 'targeted-temporal-capture.json';$capture|ConvertTo-Json -Depth 8|Set-Content -LiteralPath $path -Encoding UTF8
    Write-Output ('TARGETED_TEMPORAL_CAPTURED label={0} sha={1} output={2} readableBlocks={3} unreadableBlocks={4}' -f $Label,$sha,$out,$readableBlocks,$unreadableBlocks)
}
finally {[void][Top250TargetedTemporalNative]::CloseHandle($handle)}
