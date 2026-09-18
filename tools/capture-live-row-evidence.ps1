[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })]
    [string]$CodPath,
    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$FirstVisibleName,
    [Parameter(Mandatory)]
    [ValidateRange(0, 1000000)]
    [int]$FirstVisibleSr,
    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$SecondVisibleName,
    [Parameter(Mandatory)]
    [ValidateRange(0, 1000000)]
    [int]$SecondVisibleSr,
    [ValidateRange(1, 2048)]
    [int]$MaxScanMiB = 1024,
    [string]$OutputRoot = (Join-Path $env:LOCALAPPDATA 'CODMunity\Top250Collector\research\layout-evidence')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not ('Top250LiveRowEvidenceNative' -as [type])) {
    Add-Type @"
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public sealed class Top250LiveRowEvidenceHit { public string Label = ""; public string Encoding = ""; public int ExpectedSr; public long Address; public long RegionBase; public long AllocationBase; public uint Type; public uint Protect; public int[] Sr32Offsets = new int[0]; public int[] Sr16Offsets = new int[0]; }
public sealed class Top250LiveRowEvidencePointerHit { public string Label = ""; public long SourceAddress; public long TargetAddress; public int[] Sr32Offsets = new int[0]; public int[] Sr16Offsets = new int[0]; }
public sealed class Top250LiveRowEvidenceResult { public Top250LiveRowEvidenceHit[] Hits = new Top250LiveRowEvidenceHit[0]; public Top250LiveRowEvidencePointerHit[] PointerHits = new Top250LiveRowEvidencePointerHit[0]; public bool PointerHitLimitReached; public long ScannedBytes; public long PointerScannedBytes; public int ReadFailures; public int AccessDeniedReads; public int ScannedRegions; }
public static class Top250LiveRowEvidenceNative {
    const uint PROCESS_VM_READ = 0x0010, PROCESS_QUERY_LIMITED_INFORMATION = 0x1000, MEM_COMMIT = 0x1000, MEM_PRIVATE = 0x20000, MEM_MAPPED = 0x40000, PAGE_GUARD = 0x100, PAGE_NOACCESS = 0x01;
    [DllImport("kernel32.dll", SetLastError=true)] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError=true)] static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError=true)] static extern bool ReadProcessMemory(IntPtr process, IntPtr address, byte[] buffer, UIntPtr size, out UIntPtr bytesRead);
    [DllImport("kernel32.dll", SetLastError=true)] static extern IntPtr VirtualQueryEx(IntPtr process, IntPtr address, out MEMORY_BASIC_INFORMATION info, UIntPtr length);
    [StructLayout(LayoutKind.Sequential)] struct MEMORY_BASIC_INFORMATION { public IntPtr BaseAddress, AllocationBase; public uint AllocationProtect; public ushort PartitionId, Reserved0; public UIntPtr RegionSize; public uint State, Protect, Type; }
    static bool IsReadable(uint protect) { uint value = protect & 0xFF; return (protect & PAGE_GUARD) == 0 && value != 0 && value != PAGE_NOACCESS; }
    static int[] FindSrOffsets(byte[] bytes, int nameOffset, int expectedSr, int width) {
        var values = new List<int>(); int start = Math.Max(0, nameOffset - 0x800), end = Math.Min(bytes.Length - width, nameOffset + 0x800);
        for (int offset = start; offset <= end; offset++) {
            int value = width == 4 ? BitConverter.ToInt32(bytes, offset) : BitConverter.ToUInt16(bytes, offset);
            if (value == expectedSr) values.Add(offset - nameOffset);
        }
        return values.ToArray();
    }
    static void Find(byte[] bytes, byte[] pattern, string label, string encoding, int expectedSr, long baseAddress, MEMORY_BASIC_INFORMATION region, List<Top250LiveRowEvidenceHit> hits) {
        for (int i = 0; i <= bytes.Length - pattern.Length; i++) {
            bool match = true;
            for (int j = 0; j < pattern.Length; j++) if (bytes[i + j] != pattern[j]) { match = false; break; }
            if (match) hits.Add(new Top250LiveRowEvidenceHit { Label = label, Encoding = encoding, ExpectedSr = expectedSr, Address = baseAddress + i, RegionBase = region.BaseAddress.ToInt64(), AllocationBase = region.AllocationBase.ToInt64(), Type = region.Type, Protect = region.Protect, Sr32Offsets = FindSrOffsets(bytes, i, expectedSr, 4), Sr16Offsets = FindSrOffsets(bytes, i, expectedSr, 2) });
        }
    }
    static Top250LiveRowEvidencePointerHit[] FindPointers(IntPtr handle, Dictionary<long, Top250LiveRowEvidenceHit> targets, long maxBytes, int maxHits, out long pointerScanned, out bool limitReached, ref int failures, ref int denied, ref int regions) {
        var pointers = new List<Top250LiveRowEvidencePointerHit>(); long address = 0x10000, limit = 0x7FFFFFFF0000; pointerScanned = 0; limitReached = false; const int chunkSize = 1024 * 1024;
        while (address < limit && pointerScanned < maxBytes) {
            MEMORY_BASIC_INFORMATION region;
            if (VirtualQueryEx(handle, new IntPtr(address), out region, (UIntPtr)Marshal.SizeOf(typeof(MEMORY_BASIC_INFORMATION))) == IntPtr.Zero) break;
            long regionBase = region.BaseAddress.ToInt64(), regionSize = (long)region.RegionSize.ToUInt64(), next = regionBase + regionSize;
            if (next <= address) break;
            if (region.State == MEM_COMMIT && (region.Type == MEM_PRIVATE || region.Type == MEM_MAPPED) && IsReadable(region.Protect)) {
                regions++; byte[] carry = new byte[8]; int carryCount = 0;
                for (long position = regionBase; position < next && pointerScanned < maxBytes; ) {
                    int take = (int)Math.Min(chunkSize, Math.Min(next - position, maxBytes - pointerScanned)); var chunk = new byte[take]; UIntPtr read;
                    if (!ReadProcessMemory(handle, new IntPtr(position), chunk, (UIntPtr)take, out read) || (int)read.ToUInt64() != take) { failures++; if (Marshal.GetLastWin32Error() == 5) denied++; break; }
                    var buffer = new byte[carryCount + chunk.Length]; if (carryCount > 0) Buffer.BlockCopy(carry, 0, buffer, 0, carryCount); Buffer.BlockCopy(chunk, 0, buffer, carryCount, chunk.Length);
                    long bufferAddress = position - carryCount; int first = (int)((8 - (bufferAddress & 7)) & 7);
                    for (int offset = first; offset <= buffer.Length - 8; offset += 8) {
                        long target = BitConverter.ToInt64(buffer, offset); Top250LiveRowEvidenceHit hit;
                        if (!targets.TryGetValue(target, out hit)) continue;
                        pointers.Add(new Top250LiveRowEvidencePointerHit { Label = hit.Label, SourceAddress = bufferAddress + offset, TargetAddress = target, Sr32Offsets = FindSrOffsets(buffer, offset, hit.ExpectedSr, 4), Sr16Offsets = FindSrOffsets(buffer, offset, hit.ExpectedSr, 2) });
                        if (pointers.Count >= maxHits) { limitReached = true; break; }
                    }
                    carryCount = Math.Min(8, buffer.Length); if (carryCount > 0) Buffer.BlockCopy(buffer, buffer.Length - carryCount, carry, 0, carryCount);
                    position += take; pointerScanned += take;
                }
            }
            if (limitReached) break;
            address = next;
        }
        return pointers.ToArray();
    }
    public static Top250LiveRowEvidenceResult Scan(int pid, string[] labels, string[] encodings, int[] expectedSrs, byte[][] patterns, long maxBytes) {
        var handle = OpenProcess(PROCESS_VM_READ | PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle == IntPtr.Zero) throw new InvalidOperationException("OPEN_PROCESS_FAILED win32Error=" + Marshal.GetLastWin32Error());
        try {
            var hits = new List<Top250LiveRowEvidenceHit>(); long address = 0x10000, limit = 0x7FFFFFFF0000, scanned = 0; int failures = 0, denied = 0, regions = 0; const int chunkSize = 1024 * 1024;
            while (address < limit && scanned < maxBytes) {
                MEMORY_BASIC_INFORMATION region;
                if (VirtualQueryEx(handle, new IntPtr(address), out region, (UIntPtr)Marshal.SizeOf(typeof(MEMORY_BASIC_INFORMATION))) == IntPtr.Zero) break;
                long regionBase = region.BaseAddress.ToInt64(), regionSize = (long)region.RegionSize.ToUInt64(), next = regionBase + regionSize;
                if (next <= address) break;
                if (region.State == MEM_COMMIT && (region.Type == MEM_PRIVATE || region.Type == MEM_MAPPED) && IsReadable(region.Protect)) {
                    regions++; byte[] carry = new byte[0];
                    for (long position = regionBase; position < next && scanned < maxBytes; ) {
                        int take = (int)Math.Min(chunkSize, Math.Min(next - position, maxBytes - scanned)); var chunk = new byte[take]; UIntPtr read;
                        if (!ReadProcessMemory(handle, new IntPtr(position), chunk, (UIntPtr)take, out read) || (int)read.ToUInt64() != take) { failures++; if (Marshal.GetLastWin32Error() == 5) denied++; break; }
                        var buffer = new byte[carry.Length + chunk.Length]; if (carry.Length > 0) Buffer.BlockCopy(carry, 0, buffer, 0, carry.Length); Buffer.BlockCopy(chunk, 0, buffer, carry.Length, chunk.Length);
                        long bufferAddress = position - carry.Length;
                        for (int pattern = 0; pattern < patterns.Length; pattern++) Find(buffer, patterns[pattern], labels[pattern], encodings[pattern], expectedSrs[pattern], bufferAddress, region, hits);
                        int keep = Math.Min(64, buffer.Length); carry = new byte[keep]; if (keep > 0) Buffer.BlockCopy(buffer, buffer.Length - keep, carry, 0, keep);
                        position += take; scanned += take;
                    }
                }
                address = next;
            }
            var targets = new Dictionary<long, Top250LiveRowEvidenceHit>(); foreach (var hit in hits) if (!targets.ContainsKey(hit.Address)) targets.Add(hit.Address, hit);
            long pointerScanned = 0; bool pointerHitLimitReached = false; var pointerHits = targets.Count == 0 ? new Top250LiveRowEvidencePointerHit[0] : FindPointers(handle, targets, maxBytes, 4096, out pointerScanned, out pointerHitLimitReached, ref failures, ref denied, ref regions);
            return new Top250LiveRowEvidenceResult { Hits = hits.ToArray(), PointerHits = pointerHits, PointerHitLimitReached = pointerHitLimitReached, ScannedBytes = scanned, PointerScannedBytes = pointerScanned, ReadFailures = failures, AccessDeniedReads = denied, ScannedRegions = regions };
        }
        finally { CloseHandle(handle); }
    }
}
"@
}

function Format-RowEvidenceHex([Int64]$Value) { '0x{0:X}' -f $Value }
function Get-CodProcessForEvidence {
    param([string]$Path)
    $resolved = [IO.Path]::GetFullPath($Path)
    $candidate = @(Get-CimInstance Win32_Process -Filter "Name='cod.exe'" | Where-Object { $_.ExecutablePath -and [string]::Equals([IO.Path]::GetFullPath($_.ExecutablePath), $resolved, [StringComparison]::OrdinalIgnoreCase) } | Select-Object -First 1)
    if ($candidate.Count -eq 0) { return $null }
    Get-Process -Id $candidate[0].ProcessId -ErrorAction Stop
}

$process = Get-CodProcessForEvidence -Path $CodPath
if ($null -eq $process) { Write-Output 'LIVE_ROW_EVIDENCE_SKIPPED reason=COD_NOT_RUNNING'; exit 0 }
$sha = (Get-FileHash -LiteralPath $CodPath -Algorithm SHA256).Hash.ToUpperInvariant()
$labels = @('visible-row-1', 'visible-row-2', 'visible-row-1', 'visible-row-2')
$encodings = @('ascii-or-utf8', 'ascii-or-utf8', 'utf16le', 'utf16le')
$patterns = @([Text.Encoding]::UTF8.GetBytes($FirstVisibleName), [Text.Encoding]::UTF8.GetBytes($SecondVisibleName), [Text.Encoding]::Unicode.GetBytes($FirstVisibleName), [Text.Encoding]::Unicode.GetBytes($SecondVisibleName))
$expectedSrs = @($FirstVisibleSr, $SecondVisibleSr, $FirstVisibleSr, $SecondVisibleSr)
$result = [Top250LiveRowEvidenceNative]::Scan($process.Id, $labels, $encodings, $expectedSrs, $patterns, ([Int64]$MaxScanMiB * 1MB))
$timestamp = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss')
$outputDirectory = Join-Path (Join-Path $OutputRoot $sha) $timestamp
New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null
$safeHits = @($result.Hits | ForEach-Object { [ordered]@{ row = $_.Label; encoding = $_.Encoding; address = Format-RowEvidenceHex $_.Address; regionBase = Format-RowEvidenceHex $_.RegionBase; allocationBase = Format-RowEvidenceHex $_.AllocationBase; type = ('0x{0:X}' -f $_.Type); protect = ('0x{0:X}' -f $_.Protect); sr32OffsetsRelativeToName = @($_.Sr32Offsets | ForEach-Object { '0x{0:X}' -f $_ }); sr16OffsetsRelativeToName = @($_.Sr16Offsets | ForEach-Object { '0x{0:X}' -f $_ }) } })
$safePointerHits = @($result.PointerHits | ForEach-Object { [ordered]@{ row = $_.Label; sourceAddress = Format-RowEvidenceHex $_.SourceAddress; targetAddress = Format-RowEvidenceHex $_.TargetAddress; sr32OffsetsRelativeToPointer = @($_.Sr32Offsets | ForEach-Object { '0x{0:X}' -f $_ }); sr16OffsetsRelativeToPointer = @($_.Sr16Offsets | ForEach-Object { '0x{0:X}' -f $_ }) } })
$evidence = [ordered]@{
    schemaVersion = 1; capturedAtUtc = [DateTime]::UtcNow.ToString('o'); source = 'live-read-only-visible-row-name-scan'
    executable = [ordered]@{ sha256 = $sha; pid = $process.Id }
    visibleRows = @([ordered]@{ row = 'visible-row-1'; nameStored = $false; sr = $FirstVisibleSr }, [ordered]@{ row = 'visible-row-2'; nameStored = $false; sr = $SecondVisibleSr })
    scan = [ordered]@{ maxBytes = ([Int64]$MaxScanMiB * 1MB); scannedBytes = $result.ScannedBytes; pointerScannedBytes = $result.PointerScannedBytes; pointerHitLimitReached = $result.PointerHitLimitReached; committedReadableRegions = $result.ScannedRegions; readFailures = $result.ReadFailures; accessDeniedReads = $result.AccessDeniedReads }
    hits = $safeHits; pointerHits = $safePointerHits; privacy = [ordered]@{ playerNamesStored = $false; playerXuidsStored = $false; playerRecordBytesStored = $false }
}
$jsonPath = Join-Path $outputDirectory 'live-row-evidence.json'
$evidence | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $jsonPath -Encoding UTF8
Write-Output ('LIVE_ROW_EVIDENCE_CAPTURED sha={0} output={1} hits={2} pointerHits={3} scannedBytes={4} pointerScannedBytes={5} accessDeniedReads={6}' -f $sha, $outputDirectory, $safeHits.Count, $safePointerHits.Count, $result.ScannedBytes, $result.PointerScannedBytes, $result.AccessDeniedReads)
