[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })]
    [string]$CodPath,

    [Int64]$RvaStart = 0x128BE000L,
    [Int64]$RvaEndExclusive = 0x12DBE000L,
    [ValidateRange(2, 32)]
    [int]$MinimumContiguousRows = 2,
    [string]$OutputRoot = (Join-Path $env:LOCALAPPDATA 'CODMunity\Top250Collector\research\layout-evidence')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($RvaStart -lt 0 -or $RvaEndExclusive -le $RvaStart) {
    throw 'RECORD_CONTRACT_RVA_RANGE_INVALID'
}

if (-not ('Top250RecordContractNative' -as [type])) {
    Add-Type @"
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

public sealed class Top250RecordContractRead { public byte[] Bytes; public int BytesRead, Error; }
public sealed class Top250RecordContractCandidate { public long Entry0Rva; public int Rows; public int FirstNativeRank; public int FirstSr; public int LastSr; public int CountAtEntryMinus4; public int StateAtHistoricalDelta; }
public static class Top250RecordContractNative {
    public const uint PROCESS_VM_READ = 0x0010, PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    [DllImport("kernel32.dll", SetLastError=true)] public static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] public static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] public static extern bool ReadProcessMemory(IntPtr process, IntPtr address, byte[] buffer, UIntPtr size, out UIntPtr bytesRead);
    [DllImport("ntdll.dll")] static extern int NtQueryInformationProcess(IntPtr process, int processInformationClass, out PROCESS_BASIC_INFORMATION processInformation, int processInformationLength, out int returnLength);
    [StructLayout(LayoutKind.Sequential)] struct PROCESS_BASIC_INFORMATION { public IntPtr Reserved1, PebBaseAddress, Reserved2_0, Reserved2_1, UniqueProcessId, Reserved3; }
    public static Top250RecordContractRead Read(IntPtr process, long address, int count) { var bytes = new byte[count]; UIntPtr read; bool ok = ReadProcessMemory(process, new IntPtr(address), bytes, (UIntPtr)count, out read); int actual = (int)read.ToUInt64(); if (actual != count) Array.Resize(ref bytes, Math.Max(0, actual)); return new Top250RecordContractRead { Bytes = bytes, BytesRead = actual, Error = (ok && actual == count) ? 0 : Marshal.GetLastWin32Error() }; }
    public static long GetImageBaseFromPeb(IntPtr process) { PROCESS_BASIC_INFORMATION info; int returned; if (NtQueryInformationProcess(process, 0, out info, Marshal.SizeOf(typeof(PROCESS_BASIC_INFORMATION)), out returned) != 0 || info.PebBaseAddress == IntPtr.Zero) return 0; var read = Read(process, info.PebBaseAddress.ToInt64() + 0x10, IntPtr.Size); if (read.BytesRead != IntPtr.Size) return 0; return IntPtr.Size == 8 ? BitConverter.ToInt64(read.Bytes, 0) : BitConverter.ToInt32(read.Bytes, 0); }
    static bool IsName(byte[] bytes, int offset) {
        int length = 0; while (length < 0x40 && bytes[offset + length] != 0) length++;
        if (length < 2) return false;
        string name; try { name = Encoding.UTF8.GetString(bytes, offset, length); } catch { return false; }
        if (name.IndexOf('\uFFFD') >= 0 || String.IsNullOrWhiteSpace(name)) return false;
        foreach (char character in name) if (Char.IsControl(character)) return false;
        return true;
    }
    static bool IsRecord(byte[] bytes, int offset, out ulong xuid, out int rank, out int sr) {
        xuid = BitConverter.ToUInt64(bytes, offset); rank = BitConverter.ToInt32(bytes, offset + 0x08); sr = BitConverter.ToInt32(bytes, offset + 0x0C);
        return xuid != 0 && rank >= 0 && rank <= 250 && sr >= 10000 && sr <= 500000 && IsName(bytes, offset + 0x10);
    }
    public static Top250RecordContractCandidate[] Scan(IntPtr process, long moduleBase, long rvaStart, long rvaEndExclusive, int minimumRows, out int readError) {
        readError = 0; int bytesToRead = checked((int)(rvaEndExclusive - rvaStart)); var read = Read(process, moduleBase + rvaStart, bytesToRead); if (read.BytesRead != bytesToRead) { readError = read.Error; return new Top250RecordContractCandidate[0]; }
        const int stride = 0x458, recordBytes = 0x50, historicalStateToCount = 0x3B74C; var candidates = new List<Top250RecordContractCandidate>();
        for (int offset = 0; offset <= bytesToRead - (minimumRows - 1) * stride - recordBytes; offset += 8) {
            ulong previousXuid = 0; int previousSr = Int32.MaxValue, firstRank = -1, firstSr = 0, rows = 0;
            for (int row = 0; row < 250 && offset + row * stride + recordBytes <= bytesToRead; row++) {
                ulong xuid; int rank, sr; if (!IsRecord(read.Bytes, offset + row * stride, out xuid, out rank, out sr) || (row > 0 && (xuid == previousXuid || sr > previousSr))) { break; }
                if (row == 0) { firstRank = rank; firstSr = sr; }
                previousXuid = xuid; previousSr = sr; rows++;
            }
            if (rows < minimumRows) continue;
            if (offset >= stride) { ulong previousXuidCandidate; int previousRankCandidate, previousSrCandidate; if (IsRecord(read.Bytes, offset - stride, out previousXuidCandidate, out previousRankCandidate, out previousSrCandidate)) continue; }
            long entry0Rva = rvaStart + offset; int count = offset >= 4 ? BitConverter.ToInt32(read.Bytes, offset - 4) : -1;
            int stateOffset = offset - historicalStateToCount - 4; int state = stateOffset >= 0 ? read.Bytes[stateOffset] : -1;
            candidates.Add(new Top250RecordContractCandidate { Entry0Rva = entry0Rva, Rows = rows, FirstNativeRank = firstRank, FirstSr = firstSr, LastSr = previousSr, CountAtEntryMinus4 = count, StateAtHistoricalDelta = state });
        }
        return candidates.ToArray();
    }
}
"@
}

function Format-RecordContractHex([Int64]$Value) { '0x{0:X}' -f $Value }
function Get-RecordContractCodProcess([string]$Path) {
    $resolved = [IO.Path]::GetFullPath($Path)
    $candidate = @(Get-CimInstance Win32_Process -Filter "Name='cod.exe'" | Where-Object { $_.ExecutablePath -and [string]::Equals([IO.Path]::GetFullPath($_.ExecutablePath), $resolved, [StringComparison]::OrdinalIgnoreCase) } | Select-Object -First 1)
    if ($candidate.Count -eq 0) { return $null }
    Get-Process -Id $candidate[0].ProcessId -ErrorAction Stop
}

$process = Get-RecordContractCodProcess $CodPath
if ($null -eq $process) { Write-Output 'RECORD_CONTRACT_PROBE_SKIPPED reason=COD_NOT_RUNNING'; exit 0 }
$sha = (Get-FileHash -LiteralPath $CodPath -Algorithm SHA256).Hash.ToUpperInvariant()
$access = [Top250RecordContractNative]::PROCESS_VM_READ -bor [Top250RecordContractNative]::PROCESS_QUERY_LIMITED_INFORMATION
$handle = [Top250RecordContractNative]::OpenProcess($access, $false, $process.Id)
if ($handle -eq [IntPtr]::Zero) { throw ('OPEN_PROCESS_FAILED win32Error=' + [Runtime.InteropServices.Marshal]::GetLastWin32Error()) }

try {
    $moduleBase = [Int64][Top250RecordContractNative]::GetImageBaseFromPeb($handle)
    if ($moduleBase -le 0) { throw 'MAIN_MODULE_IDENTITY_UNAVAILABLE' }
    [int]$readError = 0
    $candidates = [Top250RecordContractNative]::Scan($handle, $moduleBase, $RvaStart, $RvaEndExclusive, $MinimumContiguousRows, [ref]$readError)
    if ($readError -ne 0) { throw ('READ_FAILED win32Error=' + $readError) }
    $timestamp = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss')
    $outputDirectory = Join-Path (Join-Path $OutputRoot $sha) ($timestamp + '-season06-record-contract-probe')
    New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null
    $evidence = [ordered]@{
        schemaVersion = 1; capturedAtUtc = [DateTime]::UtcNow.ToString('o'); source = 'live-read-only-known-season05-record-contract-probe'
        executable = [ordered]@{ sha256 = $sha; pid = $process.Id; moduleBase = Format-RecordContractHex $moduleBase }
        scope = [ordered]@{ rvaStart = Format-RecordContractHex $RvaStart; rvaEndExclusive = Format-RecordContractHex $RvaEndExclusive; recordStrideHypothesis = '0x458'; minimumContiguousRows = $MinimumContiguousRows }
        recordContract = [ordered]@{ xuidOffset = '0x0'; nativeRankOffset = '0x8'; srOffset = '0xC'; nameOffset = '0x10'; nameMaxBytes = '0x40'; namesStored = $false; xuidsStored = $false }
        candidates = @($candidates | ForEach-Object { [ordered]@{ entry0Rva = Format-RecordContractHex $_.Entry0Rva; contiguousRows = $_.Rows; firstNativeRank = $_.FirstNativeRank; firstSr = $_.FirstSr; lastSr = $_.LastSr; countAtEntryMinus4 = $_.CountAtEntryMinus4; stateAtHistoricalDelta = $_.StateAtHistoricalDelta } })
        privacy = [ordered]@{ rawMemoryStored = $false; playerNamesStored = $false; playerXuidsStored = $false; playerRecordBytesStored = $false }
    }
    $path = Join-Path $outputDirectory 'record-contract-probe.json'
    $evidence | ConvertTo-Json -Depth 7 | Set-Content -LiteralPath $path -Encoding UTF8
    Write-Output ('RECORD_CONTRACT_PROBE_COMPLETED sha={0} candidates={1} output={2}' -f $sha, $candidates.Count, $outputDirectory)
}
finally {
    [void][Top250RecordContractNative]::CloseHandle($handle)
}
