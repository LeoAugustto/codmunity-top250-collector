[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })]
    [string]$CodPath,
    [Parameter(Mandatory)]
    [ValidateSet('season06_top250_empty_baseline', 'season06_top250_populated')]
    [string]$Label,
    [ValidateRange(1, 16)]
    [int]$ChunkMiB = 1,
    [string]$OutputRoot = (Join-Path $env:LOCALAPPDATA 'CODMunity\Top250Collector\research\layout-evidence')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not ('Top250TemporalEvidenceNative' -as [type])) {
    Add-Type @"
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public sealed class Top250TemporalRegion { public long BaseAddress, AllocationBase, RegionSize; public uint State, Protect, Type; }
public static class Top250TemporalEvidenceNative {
    public const uint PROCESS_VM_READ = 0x0010, PROCESS_QUERY_LIMITED_INFORMATION = 0x1000, MEM_COMMIT = 0x1000, MEM_IMAGE = 0x1000000, MEM_MAPPED = 0x40000, PAGE_GUARD = 0x100, PAGE_NOACCESS = 0x01;
    [DllImport("kernel32.dll", SetLastError=true)] public static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] public static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] public static extern bool ReadProcessMemory(IntPtr process, IntPtr address, byte[] buffer, UIntPtr size, out UIntPtr bytesRead);
    [DllImport("kernel32.dll", SetLastError=true)] public static extern IntPtr VirtualQueryEx(IntPtr process, IntPtr address, out MEMORY_BASIC_INFORMATION info, UIntPtr length);
    [DllImport("ntdll.dll")] static extern int NtQueryInformationProcess(IntPtr process, int processInformationClass, out PROCESS_BASIC_INFORMATION processInformation, int processInformationLength, out int returnLength);
    [StructLayout(LayoutKind.Sequential)] public struct MEMORY_BASIC_INFORMATION { public IntPtr BaseAddress, AllocationBase; public uint AllocationProtect; public ushort PartitionId, Reserved0; public UIntPtr RegionSize; public uint State, Protect, Type; }
    [StructLayout(LayoutKind.Sequential)] struct PROCESS_BASIC_INFORMATION { public IntPtr Reserved1, PebBaseAddress, Reserved2_0, Reserved2_1, UniqueProcessId, Reserved3; }
    public static Top250TemporalRegion Query(IntPtr process, long address) { MEMORY_BASIC_INFORMATION info; if (VirtualQueryEx(process, new IntPtr(address), out info, (UIntPtr)Marshal.SizeOf(typeof(MEMORY_BASIC_INFORMATION))) == IntPtr.Zero) return null; return new Top250TemporalRegion { BaseAddress = info.BaseAddress.ToInt64(), AllocationBase = info.AllocationBase.ToInt64(), RegionSize = (long)info.RegionSize.ToUInt64(), State = info.State, Protect = info.Protect, Type = info.Type }; }
    public static byte[] ReadExact(IntPtr process, long address, int count) { var bytes = new byte[count]; UIntPtr read; if (!ReadProcessMemory(process, new IntPtr(address), bytes, (UIntPtr)count, out read) || (int)read.ToUInt64() != count) throw new InvalidOperationException("READ_PROCESS_MEMORY_FAILED win32Error=" + Marshal.GetLastWin32Error()); return bytes; }
    public static long GetImageBaseFromPeb(IntPtr process) { PROCESS_BASIC_INFORMATION info; int returned; if(NtQueryInformationProcess(process,0,out info,Marshal.SizeOf(typeof(PROCESS_BASIC_INFORMATION)),out returned) != 0 || info.PebBaseAddress == IntPtr.Zero) return 0; var bytes=ReadExact(process,info.PebBaseAddress.ToInt64()+0x10,IntPtr.Size); return IntPtr.Size==8 ? BitConverter.ToInt64(bytes,0) : BitConverter.ToInt32(bytes,0); }
}
"@
}

function Format-TemporalHex([Int64]$Value) { '0x{0:X}' -f $Value }
function Get-TemporalSha256([byte[]]$Bytes) {
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try { ([BitConverter]::ToString($algorithm.ComputeHash($Bytes))).Replace('-', '') }
    finally { $algorithm.Dispose() }
}
function Get-TemporalFileSha256([string]$Path) {
    $algorithm = [Security.Cryptography.SHA256]::Create()
    $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try { ([BitConverter]::ToString($algorithm.ComputeHash($stream))).Replace('-', '') }
    finally { $stream.Dispose(); $algorithm.Dispose() }
}
function Test-TemporalReadableWritable([uint32]$Protect) {
    $base = $Protect -band 0xFF
    (($Protect -band [Top250TemporalEvidenceNative]::PAGE_GUARD) -eq 0) -and $base -ne 0 -and $base -ne [Top250TemporalEvidenceNative]::PAGE_NOACCESS -and (($base -band 0x04) -ne 0 -or ($base -band 0x08) -ne 0 -or ($base -band 0x40) -ne 0 -or ($base -band 0x80) -ne 0)
}
function Get-CodTemporalProcess([string]$Path) {
    $resolved = [IO.Path]::GetFullPath($Path)
    $candidate = @(Get-CimInstance Win32_Process -Filter "Name='cod.exe'" | Where-Object { $_.ExecutablePath -and [string]::Equals([IO.Path]::GetFullPath($_.ExecutablePath), $resolved, [StringComparison]::OrdinalIgnoreCase) } | Select-Object -First 1)
    if ($candidate.Count -eq 0) { return $null }
    Get-Process -Id $candidate[0].ProcessId -ErrorAction Stop
}
function Get-PeIdentity([string]$Path) {
    $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try {
        $header = New-Object byte[] 4096; [void]$stream.Read($header, 0, $header.Length)
        if ([BitConverter]::ToUInt16($header, 0) -ne 0x5A4D) { throw 'PE_DOS_SIGNATURE_INVALID' }
        $pe = [BitConverter]::ToInt32($header, 0x3C); if ($pe -lt 0x40 -or ($pe + 0x60) -gt $header.Length -or [BitConverter]::ToUInt32($header, $pe) -ne 0x4550) { throw 'PE_SIGNATURE_INVALID' }
        $optional = $pe + 24
        [pscustomobject]@{ timestamp = [BitConverter]::ToUInt32($header, $pe + 8); imageSize = [BitConverter]::ToUInt32($header, $optional + 0x38) }
    }
    finally { $stream.Dispose() }
}

$process = Get-CodTemporalProcess -Path $CodPath
if ($null -eq $process) { Write-Output 'TEMPORAL_BASELINE_SKIPPED reason=COD_NOT_RUNNING'; exit 0 }
$sha = (Get-TemporalFileSha256 $CodPath).ToUpperInvariant()
$peIdentity = Get-PeIdentity -Path $CodPath
$handle = [Top250TemporalEvidenceNative]::OpenProcess(([Top250TemporalEvidenceNative]::PROCESS_VM_READ -bor [Top250TemporalEvidenceNative]::PROCESS_QUERY_LIMITED_INFORMATION), $false, $process.Id)
if ($handle -eq [IntPtr]::Zero) { throw ('OPEN_PROCESS_FAILED win32Error=' + [Runtime.InteropServices.Marshal]::GetLastWin32Error()) }
try {
    # The exact cod.exe path is resolved through CIM above. Some protected
    # game processes deny module snapshots, so obtain only the main-image base
    # from the read-only PEB ProcessBasicInformation contract.
    $moduleBase = [Int64][Top250TemporalEvidenceNative]::GetImageBaseFromPeb($handle)
    $moduleSize = [Int64]$peIdentity.imageSize
    if ($moduleBase -le 0 -or $moduleSize -le 0) { throw 'MAIN_MODULE_IDENTITY_UNAVAILABLE' }
    Write-Output ('TEMPORAL_BASELINE_START label={0} sha={1} moduleBase={2} moduleSize={3}' -f $Label, $sha, (Format-TemporalHex $moduleBase), $moduleSize)
    $moduleEnd = $moduleBase + $moduleSize; $chunkBytes = $ChunkMiB * 1MB
    $chunks = New-Object System.Collections.Generic.List[object]; $scannedBytes = 0L; $address = $moduleBase
    while ($address -lt $moduleEnd) {
        $region = [Top250TemporalEvidenceNative]::Query($handle, $address); if ($null -eq $region -or $region.RegionSize -le 0) { break }
        $next = $region.BaseAddress + $region.RegionSize; if ($next -le $address) { break }
        $start = [Math]::Max($region.BaseAddress, $moduleBase); $end = [Math]::Min($next, $moduleEnd)
        if ($region.State -eq [Top250TemporalEvidenceNative]::MEM_COMMIT -and (Test-TemporalReadableWritable $region.Protect) -and $end -gt $start) {
            for ($position = $start; $position -lt $end; $position += $chunkBytes) {
                $take = [int][Math]::Min([Int64]$chunkBytes, $end - $position); $bytes = [Top250TemporalEvidenceNative]::ReadExact($handle, $position, $take); $small = New-Object System.Collections.Generic.List[object]
                for ($offset = 0; $offset -le $bytes.Length - 4; $offset += 4) { $value = [BitConverter]::ToUInt32($bytes, $offset); if ($value -ge 1 -and $value -le 250 -and $small.Count -lt 64) { [void]$small.Add([ordered]@{ offset = ('0x{0:X}' -f $offset); value = $value }) } }
                [void]$chunks.Add([ordered]@{ rva = Format-TemporalHex ($position - $moduleBase); size = $take; state = ('0x{0:X}' -f $region.State); protect = ('0x{0:X}' -f $region.Protect); type = ('0x{0:X}' -f $region.Type); sha256 = Get-TemporalSha256 $bytes; smallUint32Samples = @($small.ToArray()); smallUint32SampleLimitReached = ($small.Count -eq 64) }); $scannedBytes += $take
            }
        }
        $address = $next
    }
    $timestamp = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss'); $outputDirectory = Join-Path (Join-Path $OutputRoot $sha) ($timestamp + '-' + $Label); New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null
    $capture = [ordered]@{ schemaVersion = 1; capturedAtUtc = [DateTime]::UtcNow.ToString('o'); label = $Label; source = 'live-read-only-module-owned-writable-temporal-baseline'; executable = [ordered]@{ sha256 = $sha; pid = $process.Id; moduleBase = Format-TemporalHex $moduleBase; moduleSize = $moduleSize; peTimestamp = ('0x{0:X}' -f $peIdentity.timestamp) }; scope = [ordered]@{ moduleRelative = $true; writableCommittedOnly = $true; chunkBytes = $chunkBytes; scannedBytes = $scannedBytes }; chunks = @($chunks.ToArray()); privacy = [ordered]@{ playerNamesStored = $false; playerXuidsStored = $false; playerRecordBytesStored = $false } }
    $path = Join-Path $outputDirectory 'temporal-cache-baseline.json'; $capture | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $path -Encoding UTF8
    Write-Output ('TEMPORAL_BASELINE_CAPTURED label={0} sha={1} output={2} chunks={3} scannedBytes={4}' -f $Label, $sha, $outputDirectory, $chunks.Count, $scannedBytes)
}
finally { [void][Top250TemporalEvidenceNative]::CloseHandle($handle) }
