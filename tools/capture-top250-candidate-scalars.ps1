[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })]
    [string]$CodPath,

    [ValidateSet('VISIBLE', 'EMPTY', 'UNKNOWN')]
    [string]$UiState = 'UNKNOWN',

    [ValidateRange(0, 250)]
    [int]$ObservedPopulation,

    [switch]$PopulationVerified,

    [string]$OutputRoot = (Join-Path $env:LOCALAPPDATA 'CODMunity\Top250Collector\research\layout-evidence')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$populationSupplied = $PSBoundParameters.ContainsKey('ObservedPopulation')
if ($PopulationVerified -and -not $populationSupplied) {
    throw 'OBSERVED_POPULATION_REQUIRED_WHEN_VERIFIED'
}
if ($UiState -eq 'VISIBLE' -and (-not $populationSupplied -or -not $PopulationVerified)) {
    throw 'VERIFIED_VISIBLE_POPULATION_REQUIRED'
}

# This scope is deliberately fixed to the nineteen pages isolated by the 4 -> 0 -> 5 temporal diff.
$candidatePageRvas = [Int64[]]@(
    0x129B6000L, 0x12A03000L, 0x12B0A000L, 0x12B9B000L, 0x12B9C000L, 0x12BDE000L,
    0x12C60000L, 0x12CE2000L, 0x12D67000L, 0x12D80000L, 0x12D97000L, 0x12D99000L,
    0x12D81000L, 0x12D94000L, 0x12D95000L,
    0x12B24000L, 0x12C1B000L, 0x12C1C000L, 0x12C1D000L
)
$pageBytes = 4096

if (-not ('Top250CandidateScalarNative' -as [type])) {
    Add-Type @"
using System;
using System.Runtime.InteropServices;
public sealed class Top250CandidateScalarRegion { public long BaseAddress, RegionSize; public uint State, Protect, Type; }
public sealed class Top250CandidateScalarRead { public byte[] Bytes; public int BytesRead, Error; }
public static class Top250CandidateScalarNative {
    public const uint PROCESS_VM_READ = 0x0010, PROCESS_QUERY_LIMITED_INFORMATION = 0x1000, MEM_COMMIT = 0x1000, PAGE_GUARD = 0x100, PAGE_NOACCESS = 0x01;
    [DllImport("kernel32.dll", SetLastError=true)] public static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] public static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] public static extern bool ReadProcessMemory(IntPtr process, IntPtr address, byte[] buffer, UIntPtr size, out UIntPtr bytesRead);
    [DllImport("kernel32.dll", SetLastError=true)] public static extern IntPtr VirtualQueryEx(IntPtr process, IntPtr address, out MEMORY_BASIC_INFORMATION info, UIntPtr length);
    [DllImport("ntdll.dll")] static extern int NtQueryInformationProcess(IntPtr process, int processInformationClass, out PROCESS_BASIC_INFORMATION processInformation, int processInformationLength, out int returnLength);
    [StructLayout(LayoutKind.Sequential)] public struct MEMORY_BASIC_INFORMATION { public IntPtr BaseAddress, AllocationBase; public uint AllocationProtect; public ushort PartitionId, Reserved0; public UIntPtr RegionSize; public uint State, Protect, Type; }
    [StructLayout(LayoutKind.Sequential)] struct PROCESS_BASIC_INFORMATION { public IntPtr Reserved1, PebBaseAddress, Reserved2_0, Reserved2_1, UniqueProcessId, Reserved3; }
    public static Top250CandidateScalarRegion Query(IntPtr process, long address) { MEMORY_BASIC_INFORMATION info; if (VirtualQueryEx(process, new IntPtr(address), out info, (UIntPtr)Marshal.SizeOf(typeof(MEMORY_BASIC_INFORMATION))) == IntPtr.Zero) return null; return new Top250CandidateScalarRegion { BaseAddress=info.BaseAddress.ToInt64(), RegionSize=(long)info.RegionSize.ToUInt64(), State=info.State, Protect=info.Protect, Type=info.Type }; }
    public static Top250CandidateScalarRead Read(IntPtr process, long address, int count) { var bytes=new byte[count]; UIntPtr read; bool ok=ReadProcessMemory(process,new IntPtr(address),bytes,(UIntPtr)count,out read); int actual=(int)read.ToUInt64(); if(actual!=count) Array.Resize(ref bytes,Math.Max(0,actual)); return new Top250CandidateScalarRead { Bytes=bytes, BytesRead=actual, Error=(ok && actual==count) ? 0 : Marshal.GetLastWin32Error() }; }
    public static long GetImageBaseFromPeb(IntPtr process) { PROCESS_BASIC_INFORMATION info; int returned; if(NtQueryInformationProcess(process,0,out info,Marshal.SizeOf(typeof(PROCESS_BASIC_INFORMATION)),out returned)!=0 || info.PebBaseAddress==IntPtr.Zero) return 0; var read=Read(process,info.PebBaseAddress.ToInt64()+0x10,IntPtr.Size); if(read.BytesRead!=IntPtr.Size) return 0; return IntPtr.Size==8 ? BitConverter.ToInt64(read.Bytes,0) : BitConverter.ToInt32(read.Bytes,0); }
}
"@
}

function Format-CandidateHex([Int64]$Value) {
    '0x{0:X}' -f $Value
}

function Get-CandidateFileSha256([string]$Path) {
    $algorithm = [Security.Cryptography.SHA256]::Create()
    $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try {
        ([BitConverter]::ToString($algorithm.ComputeHash($stream))).Replace('-', '')
    }
    finally {
        $stream.Dispose()
        $algorithm.Dispose()
    }
}

function Get-CandidateImageSize([string]$Path) {
    $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try {
        $header = New-Object byte[] 4096
        [void]$stream.Read($header, 0, $header.Length)
        $pe = [BitConverter]::ToInt32($header, 0x3C)
        if ([BitConverter]::ToUInt16($header, 0) -ne 0x5A4D -or [BitConverter]::ToUInt32($header, $pe) -ne 0x4550) {
            throw 'PE_SIGNATURE_INVALID'
        }
        [Int64][BitConverter]::ToUInt32($header, $pe + 24 + 0x38)
    }
    finally {
        $stream.Dispose()
    }
}

function Test-CandidateReadableWritable([uint32]$Protect) {
    $base = $Protect -band 0xFF
    (($Protect -band [Top250CandidateScalarNative]::PAGE_GUARD) -eq 0) -and
        $base -ne 0 -and
        $base -ne [Top250CandidateScalarNative]::PAGE_NOACCESS -and
        (($base -band 0x04) -ne 0 -or ($base -band 0x08) -ne 0 -or ($base -band 0x40) -ne 0 -or ($base -band 0x80) -ne 0)
}

function Get-CandidateCodProcess([string]$Path) {
    $resolved = [IO.Path]::GetFullPath($Path)
    $candidate = @(Get-CimInstance Win32_Process -Filter "Name='cod.exe'" |
        Where-Object {
            $_.ExecutablePath -and [string]::Equals([IO.Path]::GetFullPath($_.ExecutablePath), $resolved, [StringComparison]::OrdinalIgnoreCase)
        } |
        Select-Object -First 1)
    if ($candidate.Count -eq 0) {
        return $null
    }
    Get-Process -Id $candidate[0].ProcessId -ErrorAction Stop
}

$process = Get-CandidateCodProcess $CodPath
if ($null -eq $process) {
    Write-Output 'CANDIDATE_SCALAR_CAPTURE_SKIPPED reason=COD_NOT_RUNNING'
    exit 0
}

$sha = (Get-CandidateFileSha256 $CodPath).ToUpperInvariant()
$imageSize = Get-CandidateImageSize $CodPath
$access = [Top250CandidateScalarNative]::PROCESS_VM_READ -bor [Top250CandidateScalarNative]::PROCESS_QUERY_LIMITED_INFORMATION
$handle = [Top250CandidateScalarNative]::OpenProcess($access, $false, $process.Id)
if ($handle -eq [IntPtr]::Zero) {
    throw ('OPEN_PROCESS_FAILED win32Error=' + [Runtime.InteropServices.Marshal]::GetLastWin32Error())
}

try {
    $moduleBase = [Int64][Top250CandidateScalarNative]::GetImageBaseFromPeb($handle)
    if ($moduleBase -le 0) {
        throw 'MAIN_MODULE_IDENTITY_UNAVAILABLE'
    }

    $pages = New-Object System.Collections.Generic.List[object]
    $readable = 0
    $unreadable = 0
    foreach ($rva in $candidatePageRvas) {
        if ($rva + $pageBytes -gt $imageSize) {
            throw 'CANDIDATE_PAGE_OUTSIDE_IMAGE'
        }
        $address = $moduleBase + $rva
        $region = [Top250CandidateScalarNative]::Query($handle, $address)
        $eligible = ($null -ne $region) -and
            $region.State -eq [Top250CandidateScalarNative]::MEM_COMMIT -and
            (Test-CandidateReadableWritable $region.Protect) -and
            (($region.BaseAddress + $region.RegionSize) -ge ($address + $pageBytes))
        if (-not $eligible) {
            $unreadable++
            [void]$pages.Add([ordered]@{ rva = Format-CandidateHex $rva; size = $pageBytes; readable = $false; readError = $null; uint32 = @(); uint16 = @(); pointers = @() })
            continue
        }

        $read = [Top250CandidateScalarNative]::Read($handle, $address, $pageBytes)
        if ($read.BytesRead -ne $pageBytes) {
            $unreadable++
            [void]$pages.Add([ordered]@{ rva = Format-CandidateHex $rva; size = $pageBytes; readable = $false; readError = $read.Error; uint32 = @(); uint16 = @(); pointers = @() })
            continue
        }

        # All small values, including zero, are intentionally retained as scalar metadata.
        $u32 = New-Object System.Collections.Generic.List[object]
        for ($offset = 0; $offset -le $pageBytes - 4; $offset += 4) {
            $value = [BitConverter]::ToUInt32($read.Bytes, $offset)
            if ($value -le 250) {
                [void]$u32.Add([ordered]@{ offset = ('0x{0:X}' -f $offset); value = $value })
            }
        }
        $u16 = New-Object System.Collections.Generic.List[object]
        for ($offset = 0; $offset -le $pageBytes - 2; $offset += 2) {
            $value = [BitConverter]::ToUInt16($read.Bytes, $offset)
            if ($value -le 250) {
                [void]$u16.Add([ordered]@{ offset = ('0x{0:X}' -f $offset); value = $value })
            }
        }
        $pointers = New-Object System.Collections.Generic.List[object]
        for ($offset = 0; $offset -le $pageBytes - 8; $offset += 8) {
            $value = [BitConverter]::ToInt64($read.Bytes, $offset)
            if ($value -eq 0) {
                continue
            }
            if ($value -ge $moduleBase -and $value -lt ($moduleBase + $imageSize)) {
                [void]$pointers.Add([ordered]@{ offset = ('0x{0:X}' -f $offset); classification = 'module-relative'; targetRva = Format-CandidateHex ($value - $moduleBase) })
            }
            elseif ($value -ge 0x10000L -and $value -lt 0x7FFF00000000L) {
                [void]$pointers.Add([ordered]@{ offset = ('0x{0:X}' -f $offset); classification = 'external-canonical'; targetRva = $null })
            }
        }

        $readable++
        [void]$pages.Add([ordered]@{
            rva = Format-CandidateHex $rva; size = $pageBytes; readable = $true; readError = $null
            uint32 = @($u32.ToArray()); uint16 = @($u16.ToArray()); pointers = @($pointers.ToArray())
        })
    }

    $timestamp = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss')
    $labelPopulation = if ($populationSupplied) { [string]$ObservedPopulation } else { 'unknown' }
    $label = 'season06_candidate_scalars_' + $labelPopulation
    $outputDirectory = Join-Path (Join-Path $OutputRoot $sha) ($timestamp + '-' + $label)
    New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null
    $capture = [ordered]@{
        schemaVersion = 1
        capturedAtUtc = [DateTime]::UtcNow.ToString('o')
        label = $label
        source = 'live-read-only-top250-candidate-scalars'
        executable = [ordered]@{ sha256 = $sha; pid = $process.Id; moduleBase = Format-CandidateHex $moduleBase; moduleSize = $imageSize }
        observation = [ordered]@{
            uiState = $UiState
            observedPopulation = if ($populationSupplied) { $ObservedPopulation } else { $null }
            populationVerified = [bool]$PopulationVerified
        }
        scope = [ordered]@{ moduleRelative = $true; pageBytes = $pageBytes; candidatePageRvas = @($candidatePageRvas | ForEach-Object { Format-CandidateHex $_ }); readablePages = $readable; unreadablePages = $unreadable }
        pages = @($pages.ToArray())
        privacy = [ordered]@{ rawPageBytesStored = $false; stringsStored = $false; playerNamesStored = $false; playerXuidsStored = $false; playerRecordBytesStored = $false }
    }
    $path = Join-Path $outputDirectory 'candidate-scalar-evidence.json'
    $capture | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $path -Encoding UTF8

    $uint32Count = @($capture.pages | ForEach-Object { @($_.uint32) }).Count
    $uint16Count = @($capture.pages | ForEach-Object { @($_.uint16) }).Count
    $pointerCount = @($capture.pages | ForEach-Object { @($_.pointers) }).Count
    Write-Output ('CANDIDATE_SCALAR_CAPTURED uiState={0} observedPopulation={1} populationVerified={2} sha={3} output={4} readablePages={5} uint32={6} uint16={7} pointers={8}' -f $UiState, $labelPopulation, [bool]$PopulationVerified, $sha, $outputDirectory, $readable, $uint32Count, $uint16Count, $pointerCount)
}
finally {
    [void][Top250CandidateScalarNative]::CloseHandle($handle)
}
