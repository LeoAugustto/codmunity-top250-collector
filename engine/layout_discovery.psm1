Set-StrictMode -Version Latest

$script:DiscoverySchemaVersion = 2
$script:ExpectedModes = @('mp', 'br', 'resurgence')
$script:ExpectedFields = @('stateRva', 'countRva', 'entry0Rva')
$script:DefaultEntryStride = 0x458
$script:KnownStateToCount = 0x3B74C
$script:KnownModeBlockSpacing = 0x820F0

function Test-BnetTransientLiveValidationReason {
    param([string]$Reason)
    return $Reason -in @('LIVE_SAMPLE_ONE_FAILED', 'LIVE_SAMPLE_TWO_FAILED', 'REPEATED_READ_INCONSISTENT', 'MODULE_REGION_UNAVAILABLE', 'LIVE_VALIDATION_EXCEPTION')
}

function Get-BnetCacheValidationDisposition {
    param([string]$CacheStatus, [string]$ValidationReason)
    if ($CacheStatus -ne 'OK') { return 'REJECTED' }
    if (Test-BnetTransientLiveValidationReason -Reason $ValidationReason) { return 'PENDING' }
    return 'REJECTED'
}

function Initialize-BnetStructuralDiscoveryNative {
    if ('Top250StructuralDiscoveryNative' -as [type]) { return }
    Add-Type @"
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

public static class Top250StructuralDiscoveryNative
{
    public const uint PROCESS_VM_READ = 0x0010;
    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    public const uint MEM_COMMIT = 0x1000;
    [StructLayout(LayoutKind.Sequential)]
    public struct MEMORY_BASIC_INFORMATION {
        public IntPtr BaseAddress;
        public IntPtr AllocationBase;
        public uint AllocationProtect;
        public UIntPtr RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ReadProcessMemory(IntPtr h, IntPtr address, [Out] byte[] buffer, UIntPtr size, out UIntPtr read);
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr VirtualQueryEx(IntPtr h, IntPtr address, out MEMORY_BASIC_INFORMATION mbi, IntPtr length);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CloseHandle(IntPtr h);
    public static byte[] ReadExact(IntPtr h, long address, int size) {
        var bytes = new byte[size]; UIntPtr read;
        if (!ReadProcessMemory(h, new IntPtr(address), bytes, (UIntPtr)size, out read) || read.ToUInt64() != (ulong)size)
            throw new Win32Exception(Marshal.GetLastWin32Error(), String.Format("ReadProcessMemory failed at 0x{0:X}", address));
        return bytes;
    }
    public static int[] FindPositiveSmallUInt32Offsets(byte[] bytes) {
        var matches = new System.Collections.Generic.List<int>();
        for (int i = 0; i <= bytes.Length - 4; i += 4) {
            uint value = BitConverter.ToUInt32(bytes, i);
            if (value > 0 && value <= 250) matches.Add(i);
        }
        return matches.ToArray();
    }
}
public sealed class Top250DiscoveryChunk
{
    public long Rva;
    public byte[] Bytes;
    public Top250DiscoveryChunk(long rva, byte[] bytes) { Rva = rva; Bytes = bytes; }
}
public sealed class Top250GeometryFilterResult
{
    public long[] CandidateCountRvas;
    public long RawCountCandidates;
    public long TripleAddressCandidates;
    public long CountRangeCandidates;
    public long StateGeometryCandidates;
    public long TripleLookupMilliseconds;
    public long GeometryValidationMilliseconds;
    public bool TimedOut;
}
public static class Top250StructuralGeometryFilter
{
    static System.Collections.Generic.Dictionary<long, Top250DiscoveryChunk> BuildPageIndex(System.Collections.Generic.List<Top250DiscoveryChunk> chunks)
    {
        var pages = new System.Collections.Generic.Dictionary<long, Top250DiscoveryChunk>();
        foreach (var chunk in chunks) {
            long first = chunk.Rva & ~0xFFFL;
            long end = chunk.Rva + chunk.Bytes.Length;
            for (long page = first; page < end; page += 0x1000) pages[page] = chunk;
        }
        return pages;
    }
    static bool TryReadU32(System.Collections.Generic.Dictionary<long, Top250DiscoveryChunk> pages, long rva, out uint value)
    {
        value = 0; Top250DiscoveryChunk chunk;
        if (!pages.TryGetValue(rva & ~0xFFFL, out chunk) || rva < chunk.Rva || rva + 4 > chunk.Rva + chunk.Bytes.Length) return false;
        value = BitConverter.ToUInt32(chunk.Bytes, (int)(rva - chunk.Rva)); return true;
    }
    static bool TryReadByte(System.Collections.Generic.Dictionary<long, Top250DiscoveryChunk> pages, long rva, out byte value)
    {
        value = 0; Top250DiscoveryChunk chunk;
        if (!pages.TryGetValue(rva & ~0xFFFL, out chunk) || rva < chunk.Rva || rva >= chunk.Rva + chunk.Bytes.Length) return false;
        value = chunk.Bytes[(int)(rva - chunk.Rva)]; return true;
    }
    public static byte[] CreateRepeatedUInt32Buffer(int length, uint value)
    {
        if (length < 4 || (length & 3) != 0) throw new ArgumentOutOfRangeException("length");
        var bytes = new byte[length]; var encoded = BitConverter.GetBytes(value);
        for (int offset = 0; offset < length; offset += 4) Buffer.BlockCopy(encoded, 0, bytes, offset, 4);
        return bytes;
    }
    public static Top250GeometryFilterResult FindTriples(Top250DiscoveryChunk[] source, long moduleSize, long stateToCount, long spacing, long maxMilliseconds)
    {
        var chunks = new System.Collections.Generic.List<Top250DiscoveryChunk>(source);
        chunks.Sort((a,b) => a.Rva.CompareTo(b.Rva));
        var pages = BuildPageIndex(chunks);
        var seen = new System.Collections.Generic.HashSet<long>(); var countRangeBases = new System.Collections.Generic.List<long>(); var survivors = new System.Collections.Generic.List<long>();
        long raw = 0, addressable = 0, inRange = 0, states = 0;
        var started = System.Diagnostics.Stopwatch.StartNew();
        foreach (var chunk in chunks) {
            for (int offset = 0; offset <= chunk.Bytes.Length - 4; offset += 4) {
                uint value = BitConverter.ToUInt32(chunk.Bytes, offset);
                if (value == 0 || value > 250) continue;
                raw++; long candidate = chunk.Rva + offset;
                for (int mode = 0; mode < 3; mode++) {
                    long count0 = candidate - (mode * spacing);
                    if (!seen.Add(count0)) continue;
                    if (count0 < stateToCount || count0 + (2 * spacing) + 4 >= moduleSize) continue;
                    addressable++;
                    uint c0, c1, c2;
                    if (!TryReadU32(pages, count0, out c0) || !TryReadU32(pages, count0 + spacing, out c1) || !TryReadU32(pages, count0 + (2 * spacing), out c2)) continue;
                    if (c0 > 250 || c1 > 250 || c2 > 250 || (c0 == 0 && c1 == 0 && c2 == 0)) continue;
                    inRange++;
                    countRangeBases.Add(count0);
                }
                if ((raw & 0xFFF) == 0 && maxMilliseconds > 0 && started.ElapsedMilliseconds >= maxMilliseconds)
                    return new Top250GeometryFilterResult { CandidateCountRvas = survivors.ToArray(), RawCountCandidates = raw, TripleAddressCandidates = addressable, CountRangeCandidates = inRange, StateGeometryCandidates = states, TripleLookupMilliseconds = started.ElapsedMilliseconds, TimedOut = true };
            }
        }
        long lookupMs = started.ElapsedMilliseconds; var geometryStarted = System.Diagnostics.Stopwatch.StartNew(); int geometryIndex = 0;
        foreach (long count0 in countRangeBases) {
            geometryIndex++;
            byte s0, s1, s2, e0, e1, e2;
            if (!TryReadByte(pages, count0 - stateToCount, out s0) || !TryReadByte(pages, count0 + spacing - stateToCount, out s1) || !TryReadByte(pages, count0 + (2 * spacing) - stateToCount, out s2) || !TryReadByte(pages, count0 + 4, out e0) || !TryReadByte(pages, count0 + spacing + 4, out e1) || !TryReadByte(pages, count0 + (2 * spacing) + 4, out e2)) continue;
            if (s0 != 3 || s1 != 3 || s2 != 3) continue;
            states++; survivors.Add(count0);
            if ((geometryIndex & 0xFFF) == 0 && maxMilliseconds > 0 && started.ElapsedMilliseconds >= maxMilliseconds)
                return new Top250GeometryFilterResult { CandidateCountRvas = survivors.ToArray(), RawCountCandidates = raw, TripleAddressCandidates = addressable, CountRangeCandidates = inRange, StateGeometryCandidates = states, TripleLookupMilliseconds = lookupMs, GeometryValidationMilliseconds = geometryStarted.ElapsedMilliseconds, TimedOut = true };
        }
        return new Top250GeometryFilterResult { CandidateCountRvas = survivors.ToArray(), RawCountCandidates = raw, TripleAddressCandidates = addressable, CountRangeCandidates = inRange, StateGeometryCandidates = states, TripleLookupMilliseconds = lookupMs, GeometryValidationMilliseconds = geometryStarted.ElapsedMilliseconds, TimedOut = false };
    }
}
"@
}

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
    param([Parameter(Mandatory)][hashtable]$Anchors, [Parameter(Mandatory)][string]$Sha256, [Parameter(Mandatory)][string]$MethodVersion, [string]$Source = 'static-anchor-set')
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
        source = $Source
        methodVersion = $MethodVersion
        entryStride = ('0x{0:X}' -f $script:DefaultEntryStride)
        datasets = [pscustomobject]$datasets
        anchorIds = @($Anchors.Keys | Sort-Object)
    })
}

function Read-BnetDiscoveryBytes {
    param([Parameter(Mandatory)][scriptblock]$ReadBytes, [Parameter(Mandatory)][Int64]$Rva, [Parameter(Mandatory)][int]$Size)
    $value = & $ReadBytes $Rva $Size
    if ($value -is [byte[]]) { return $value }
    return [byte[]]@($value)
}

function Test-BnetRowsWithReader {
    param([Parameter(Mandatory)][object]$Definition, [Parameter(Mandatory)][scriptblock]$ReadBytes)
    $shape = Test-BnetLayoutDefinition -Definition $Definition
    if (-not $shape.Valid) { return [pscustomobject]@{ Valid = $false; Reason = $shape.Reason; PopulatedDatasets = 0; Counts = @{} } }
    $counts = @{}; $populated = 0
    try {
        foreach ($mode in $script:ExpectedModes) {
            $set = $Definition.datasets.$mode
            $countRva = ConvertTo-BnetLayoutRva $set.countRva
            $entryRva = ConvertTo-BnetLayoutRva $set.entry0Rva
            $stateRva = ConvertTo-BnetLayoutRva $set.stateRva
            $state = (Read-BnetDiscoveryBytes -ReadBytes $ReadBytes -Rva $stateRva -Size 1)[0]
            if ($state -ne 3) { return [pscustomobject]@{ Valid = $false; Reason = "STATE_NOT_READY_$mode"; PopulatedDatasets = $populated; Counts = $counts } }
            $count = [BitConverter]::ToUInt32((Read-BnetDiscoveryBytes -ReadBytes $ReadBytes -Rva $countRva -Size 4), 0)
            if ($count -gt 250) { return [pscustomobject]@{ Valid = $false; Reason = "COUNT_OUT_OF_RANGE_$mode"; PopulatedDatasets = $populated; Counts = $counts } }
            $counts[$mode] = [int]$count
            if ($count -eq 0) { continue }
            $populated++
            $seen = @{}; $lastSr = [int]::MaxValue
            for ($i = 0; $i -lt [int]$count; $i++) {
                $record = $entryRva + ([Int64]$i * $script:DefaultEntryStride)
                $bytes = Read-BnetDiscoveryBytes -ReadBytes $ReadBytes -Rva $record -Size $script:DefaultEntryStride
                $xuid = [BitConverter]::ToUInt64($bytes, 0)
                $rank0 = [BitConverter]::ToUInt32($bytes, 8)
                $sr = [BitConverter]::ToInt32($bytes, 12)
                $nameBytes = New-Object byte[] 0x40; [Array]::Copy($bytes, 0x10, $nameBytes, 0, $nameBytes.Length)
                $nameLength = 0; while ($nameLength -lt $nameBytes.Length -and $nameBytes[$nameLength] -ne 0) { $nameLength++ }
                if ($xuid -eq 0 -or $rank0 -gt 249 -or $sr -le 0 -or $sr -gt $lastSr -or $nameLength -eq 0) { return [pscustomobject]@{ Valid = $false; Reason = "ROW_CONTRACT_INVALID_${mode}_$i"; PopulatedDatasets = $populated; Counts = $counts } }
                try { $name = [Text.UTF8Encoding]::new($false, $true).GetString($nameBytes, 0, $nameLength) } catch { return [pscustomobject]@{ Valid = $false; Reason = "ROW_NAME_ENCODING_INVALID_${mode}_$i"; PopulatedDatasets = $populated; Counts = $counts } }
                if ([string]::IsNullOrWhiteSpace($name) -or $seen.ContainsKey($xuid)) { return [pscustomobject]@{ Valid = $false; Reason = "ROW_IDENTITY_INVALID_${mode}_$i"; PopulatedDatasets = $populated; Counts = $counts } }
                $seen[$xuid] = $true; $lastSr = $sr
            }
        }
    }
    catch { return [pscustomobject]@{ Valid = $false; Reason = 'ROW_READ_FAILED'; PopulatedDatasets = $populated; Counts = $counts } }
    if ($populated -eq 0) { return [pscustomobject]@{ Valid = $false; Reason = 'ALL_DATASETS_EMPTY_UNPROVEN'; PopulatedDatasets = 0; Counts = $counts } }
    [pscustomobject]@{ Valid = $true; Reason = 'OK'; PopulatedDatasets = $populated; Counts = $counts }
}

function Get-BnetGeometryDiscoveryCandidates {
    param(
        [Parameter(Mandatory)][Int64]$ModuleSize,
        [Parameter(Mandatory)][Int64[]]$StateCandidates,
        [Parameter(Mandatory)][scriptblock]$ReadBytes,
        [Parameter(Mandatory)][string]$Sha256
    )
    $geometry = 0; $triples = 0; $semantic = @(); $seen = @{}
    foreach ($stateRva in $StateCandidates) {
        if ($stateRva -lt 0 -or ($stateRva + (2 * $script:KnownModeBlockSpacing) + $script:KnownStateToCount + 4) -ge $ModuleSize) { continue }
        try {
            $states = @($stateRva, ($stateRva + $script:KnownModeBlockSpacing), ($stateRva + (2 * $script:KnownModeBlockSpacing)))
            if (@($states | Where-Object { (Read-BnetDiscoveryBytes -ReadBytes $ReadBytes -Rva $_ -Size 1)[0] -ne 3 }).Count -ne 0) { continue }
            $geometry++
            $anchors = @{}
            for ($index = 0; $index -lt $script:ExpectedModes.Count; $index++) {
                $mode = $script:ExpectedModes[$index]; $state = $states[$index]; $count = $state + $script:KnownStateToCount
                $nativeCount = [BitConverter]::ToUInt32((Read-BnetDiscoveryBytes -ReadBytes $ReadBytes -Rva $count -Size 4), 0)
                if ($nativeCount -gt 250) { $anchors = $null; break }
                $anchors["$mode/stateRva"] = $state; $anchors["$mode/countRva"] = $count; $anchors["$mode/entry0Rva"] = $count + 4
            }
            if ($null -eq $anchors) { continue }
            $triples++
            $definition = New-BnetLayoutDefinitionFromAnchors -Anchors $anchors -Sha256 $Sha256 -MethodVersion 'geometry-v1'
            $rowResult = Test-BnetRowsWithReader -Definition $definition -ReadBytes $ReadBytes
            if ($rowResult.Valid) {
                $key = $definition.datasets.mp.stateRva
                if (-not $seen.ContainsKey($key)) { $seen[$key] = $true; $semantic += [pscustomobject]@{ Valid = $true; Definition = $definition; MethodVersion = 'geometry-v1'; Counts = $rowResult.Counts } }
            }
        }
        catch { continue }
    }
    [pscustomobject]@{ Candidates = @($semantic); GeometryCandidates = $geometry; ThreeModeCandidates = $triples; SemanticCandidates = $semantic.Count }
}

function Read-BnetLocalDiscoveryBytes {
    param([Parameter(Mandatory)][object[]]$Chunks, [Parameter(Mandatory)][Int64]$Rva, [Parameter(Mandatory)][int]$Size)
    foreach ($chunk in $Chunks) {
        $start = [Int64]$chunk.Rva; $end = $start + $chunk.Bytes.Length
        if ($Rva -ge $start -and ($Rva + $Size) -le $end) {
            $result = New-Object byte[] $Size
            [Array]::Copy($chunk.Bytes, [int]($Rva - $start), $result, 0, $Size)
            return $result
        }
    }
    throw 'LOCAL_CHUNK_ADDRESS_UNAVAILABLE'
}

function Get-BnetGeometryCandidatesFromCounts {
    param([Parameter(Mandatory)][Int64]$ModuleSize, [Parameter(Mandatory)][Int64[]]$CountCandidates, [Parameter(Mandatory)][scriptblock]$ReadLocalBytes, [Parameter(Mandatory)][string]$Sha256, [scriptblock]$ShouldStop)
    $geometry = 0; $triples = 0; $candidates = @(); $seen = @{}
    foreach ($candidateCount in $CountCandidates) {
        if ($null -ne $ShouldStop -and (& $ShouldStop)) { return [pscustomobject]@{ Candidates=@(); GeometryCandidates=$geometry; ThreeModeCandidates=$triples; TimedOut=$true } }
        for ($modeIndex = 0; $modeIndex -lt 3; $modeIndex++) {
            $mpCount = $candidateCount - ($modeIndex * $script:KnownModeBlockSpacing)
            if ($mpCount -lt $script:KnownStateToCount -or ($mpCount + (2 * $script:KnownModeBlockSpacing) + 4) -ge $ModuleSize -or $seen.ContainsKey($mpCount)) { continue }
            $seen[$mpCount] = $true
            try {
                $counts = @($mpCount, ($mpCount + $script:KnownModeBlockSpacing), ($mpCount + (2 * $script:KnownModeBlockSpacing)))
                $values = @($counts | ForEach-Object { [BitConverter]::ToUInt32((Read-BnetDiscoveryBytes -ReadBytes $ReadLocalBytes -Rva $_ -Size 4), 0) })
                if (@($values | Where-Object { $_ -gt 250 }).Count -ne 0 -or @($values | Where-Object { $_ -gt 0 }).Count -eq 0) { continue }
                $geometry++
                $states = @($counts | ForEach-Object { $_ - $script:KnownStateToCount })
                if (@($states | Where-Object { (Read-BnetDiscoveryBytes -ReadBytes $ReadLocalBytes -Rva $_ -Size 1)[0] -ne 3 }).Count -ne 0) { continue }
                $triples++
                $anchors = @{}
                for ($index = 0; $index -lt $script:ExpectedModes.Count; $index++) {
                    $mode = $script:ExpectedModes[$index]
                    $anchors["$mode/stateRva"] = $states[$index]; $anchors["$mode/countRva"] = $counts[$index]; $anchors["$mode/entry0Rva"] = $counts[$index] + 4
                }
                $definition = New-BnetLayoutDefinitionFromAnchors -Anchors $anchors -Sha256 $Sha256 -MethodVersion 'geometry-v2'
                $candidates += [pscustomobject]@{ Definition=$definition; Counts=$values; MethodVersion='geometry-v2' }
            } catch { continue }
        }
    }
    [pscustomobject]@{ Candidates=@($candidates); GeometryCandidates=$geometry; ThreeModeCandidates=$triples; TimedOut=$false }
}

function Invoke-BnetStructuralDiscovery {
    param([Parameter(Mandatory)][Diagnostics.Process]$Cod, [Parameter(Mandatory)][string]$Sha256, [int]$MaxDiscoverySeconds = 0, [scriptblock]$ProgressCallback)
    $watch = [Diagnostics.Stopwatch]::StartNew()
    # Progress must never enter this function's output pipeline: callers receive exactly one result object.
    $progress = { param([string]$Stage,[hashtable]$Data) if ($null -ne $ProgressCallback) { [void](& $ProgressCallback $Stage $Data $watch.Elapsed) } }.GetNewClosure()
    & $progress 'MODULE_BASE_RESOLVE_START' @{}
    Initialize-BnetStructuralDiscoveryNative
    $base = [Int64]0; $moduleSize = [Int64]0
    try { $module = $Cod.MainModule; $base = [Int64]$module.BaseAddress.ToInt64(); $moduleSize = [Int64]$module.ModuleMemorySize }
    catch { return [pscustomobject]@{ Candidates=@(); Reason='MODULE_BASE_RESOLVE_FAILED'; RawCountLikeCandidates=0; TripleAddressCandidates=0; CountRangeCandidates=0; GeometryCandidates=0; ThreeModeCandidates=0; SemanticCandidates=0; WritableRegions=0; BytesScanned=0; ModuleBase=$base; ModuleSize=$moduleSize; CandidateScanMilliseconds=0; TripleLookupMilliseconds=0; GeometryValidationMilliseconds=0; SemanticValidationMilliseconds=0; TotalMilliseconds=$watch.ElapsedMilliseconds; LastStage='MODULE_BASE_RESOLVE' } }
    $moduleIdentity = [pscustomobject]@{ ProcessId=[int]$Cod.Id; ModuleBase=$base; ModuleSize=$moduleSize; Sha256=$Sha256 }
    $base = $moduleIdentity.ModuleBase; $moduleSize = $moduleIdentity.ModuleSize
    & $progress 'MODULE_BASE_RESOLVED' @{ moduleBase=$base; moduleSize=$moduleSize }
    if ($base -le 0 -or $moduleSize -le 0) { return [pscustomobject]@{ Candidates=@(); Reason='MODULE_REGION_UNAVAILABLE'; RawCountLikeCandidates=0; TripleAddressCandidates=0; CountRangeCandidates=0; GeometryCandidates=0; ThreeModeCandidates=0; SemanticCandidates=0; WritableRegions=0; BytesScanned=0; ModuleBase=$base; ModuleSize=$moduleSize; CandidateScanMilliseconds=0; TripleLookupMilliseconds=0; GeometryValidationMilliseconds=0; SemanticValidationMilliseconds=0; TotalMilliseconds=$watch.ElapsedMilliseconds; LastStage='MODULE_BASE_RESOLVE' } }
    $access = [Top250StructuralDiscoveryNative]::PROCESS_VM_READ -bor [Top250StructuralDiscoveryNative]::PROCESS_QUERY_LIMITED_INFORMATION
    $handle = [Top250StructuralDiscoveryNative]::OpenProcess($access, $false, $Cod.Id)
    if ($handle -eq [IntPtr]::Zero) { return [pscustomobject]@{ Candidates=@(); Reason='OPEN_PROCESS_FAILED'; RawCountLikeCandidates=0; TripleAddressCandidates=0; CountRangeCandidates=0; GeometryCandidates=0; ThreeModeCandidates=0; SemanticCandidates=0; WritableRegions=0; BytesScanned=0; ModuleBase=$base; ModuleSize=$moduleSize; CandidateScanMilliseconds=0; TripleLookupMilliseconds=0; GeometryValidationMilliseconds=0; SemanticValidationMilliseconds=0; TotalMilliseconds=$watch.ElapsedMilliseconds; LastStage='OPEN_PROCESS' } }
    try {
        & $progress 'REGION_ENUMERATION_START' @{}
        $ranges = @(); $cursor = $base; $end = $base + $moduleSize; $mbiSize = [Runtime.InteropServices.Marshal]::SizeOf([type][Top250StructuralDiscoveryNative+MEMORY_BASIC_INFORMATION])
        while ($cursor -lt $end) {
            $mbi = New-Object Top250StructuralDiscoveryNative+MEMORY_BASIC_INFORMATION
            $queried = [Top250StructuralDiscoveryNative]::VirtualQueryEx($handle, [IntPtr]$cursor, [ref]$mbi, [IntPtr]$mbiSize)
            if ($queried -eq [IntPtr]::Zero -or $mbi.RegionSize.ToUInt64() -eq 0) { break }
            $regionStart = [Int64]$mbi.BaseAddress.ToInt64(); $regionEnd = $regionStart + [Int64]$mbi.RegionSize.ToUInt64(); $protect = $mbi.Protect -band 0xFF
            if ($mbi.State -eq [Top250StructuralDiscoveryNative]::MEM_COMMIT -and $regionEnd -gt $base -and $regionStart -lt $end -and @(0x04,0x08,0x40,0x80) -contains $protect) {
                $ranges += [pscustomobject]@{ Start = [Math]::Max($regionStart,$base); End = [Math]::Min($regionEnd,$end) }
            }
            $cursor = $regionEnd
        }
        $reader = { param([Int64]$Rva,[int]$Size) [Top250StructuralDiscoveryNative]::ReadExact($handle, $base + $Rva, $Size) }.GetNewClosure()
        $bytesScanned = [Int64]0; foreach ($range in $ranges) { $bytesScanned += [Int64]($range.End - $range.Start) }
        & $progress 'REGION_ENUMERATION_DONE' @{ writableRegions=$ranges.Count; writableBytes=$bytesScanned }
        & $progress 'CANDIDATE_SCAN_START' @{ writableRegions=$ranges.Count; writableBytes=$bytesScanned }
        $candidateScanStartMs = $watch.ElapsedMilliseconds
        $chunks = New-Object 'System.Collections.Generic.List[Top250DiscoveryChunk]'; $scanned = [Int64]0; $nextProgress = [Int64]16MB
        foreach ($range in $ranges) {
            for ($address = $range.Start; $address -lt $range.End; ) {
                $take = [int][Math]::Min(1048576, $range.End - $address); $bytes = [Top250StructuralDiscoveryNative]::ReadExact($handle, $address, $take)
                $chunkRva = [Int64]($address - $base); $chunks.Add([Top250DiscoveryChunk]::new($chunkRva, $bytes))
                $address += $take; $scanned += $take
                if ($scanned -ge $nextProgress) { & $progress 'CANDIDATE_SCAN_PROGRESS' @{ bytesScanned=$scanned }; $nextProgress += 16MB }
                if ($MaxDiscoverySeconds -gt 0 -and $watch.Elapsed.TotalSeconds -ge $MaxDiscoverySeconds) { return [pscustomobject]@{ Candidates=@(); Reason='TIMEOUT'; RawCountLikeCandidates=0; TripleAddressCandidates=0; CountRangeCandidates=0; GeometryCandidates=0; ThreeModeCandidates=0; SemanticCandidates=0; WritableRegions=$ranges.Count; BytesScanned=$scanned; ModuleBase=$base; ModuleSize=$moduleSize; CandidateScanMilliseconds=($watch.ElapsedMilliseconds-$candidateScanStartMs); TripleLookupMilliseconds=0; GeometryValidationMilliseconds=0; SemanticValidationMilliseconds=0; TotalMilliseconds=$watch.ElapsedMilliseconds; LastStage='CANDIDATE_SCAN' } }
            }
        }
        $candidateScanMs = $watch.ElapsedMilliseconds - $candidateScanStartMs
        & $progress 'CANDIDATE_SCAN_DONE' @{ bytesScanned=$scanned; candidateScanMs=$candidateScanMs }
        $remainingMs = if ($MaxDiscoverySeconds -gt 0) { [Int64][Math]::Max(1, ($MaxDiscoverySeconds * 1000) - $watch.ElapsedMilliseconds) } else { 0 }
        $found = [Top250StructuralGeometryFilter]::FindTriples($chunks.ToArray(), $moduleSize, $script:KnownStateToCount, $script:KnownModeBlockSpacing, $remainingMs)
        & $progress 'GEOMETRY_FILTER_DONE' @{ rawCountCandidates=$found.RawCountCandidates; tripleAddressCandidates=$found.TripleAddressCandidates; countRangeCandidates=$found.CountRangeCandidates; stateGeometryCandidates=$found.StateGeometryCandidates; tripleLookupMs=$found.TripleLookupMilliseconds; geometryValidationMs=$found.GeometryValidationMilliseconds }
        if ($found.TimedOut) { return [pscustomobject]@{ Candidates=@(); Reason='TIMEOUT'; RawCountLikeCandidates=$found.RawCountCandidates; TripleAddressCandidates=$found.TripleAddressCandidates; CountRangeCandidates=$found.CountRangeCandidates; GeometryCandidates=$found.CountRangeCandidates; ThreeModeCandidates=$found.StateGeometryCandidates; SemanticCandidates=0; WritableRegions=$ranges.Count; BytesScanned=$scanned; ModuleBase=$base; ModuleSize=$moduleSize; CandidateScanMilliseconds=$candidateScanMs; TripleLookupMilliseconds=$found.TripleLookupMilliseconds; GeometryValidationMilliseconds=$found.GeometryValidationMilliseconds; SemanticValidationMilliseconds=0; TotalMilliseconds=$watch.ElapsedMilliseconds; LastStage='GEOMETRY_FILTER' } }
        $structuralCandidates = @(); foreach ($mpCount in $found.CandidateCountRvas) { $anchors=@{}; for($index=0;$index -lt $script:ExpectedModes.Count;$index++){ $mode=$script:ExpectedModes[$index]; $count=$mpCount+($index*$script:KnownModeBlockSpacing); $anchors["$mode/stateRva"]=$count-$script:KnownStateToCount; $anchors["$mode/countRva"]=$count; $anchors["$mode/entry0Rva"]=$count+4 }; $structuralCandidates += [pscustomobject]@{ Definition=(New-BnetLayoutDefinitionFromAnchors -Anchors $anchors -Sha256 $Sha256 -MethodVersion 'geometry-v3' -Source 'structural-geometry'); MethodVersion='geometry-v3' } }
        $shouldStop = { ($MaxDiscoverySeconds -gt 0) -and ($watch.Elapsed.TotalSeconds -ge $MaxDiscoverySeconds) }.GetNewClosure()
        & $progress 'SEMANTIC_VALIDATION_START' @{ candidates=$structuralCandidates.Count }
        $semanticStartMs = $watch.ElapsedMilliseconds
        $semantic = @(); foreach ($candidate in $structuralCandidates) { if (& $shouldStop) { return [pscustomobject]@{ Candidates=@(); Reason='TIMEOUT'; RawCountLikeCandidates=$found.RawCountCandidates; TripleAddressCandidates=$found.TripleAddressCandidates; CountRangeCandidates=$found.CountRangeCandidates; GeometryCandidates=$found.CountRangeCandidates; ThreeModeCandidates=$found.StateGeometryCandidates; SemanticCandidates=$semantic.Count; WritableRegions=$ranges.Count; BytesScanned=$scanned; ModuleBase=$base; ModuleSize=$moduleSize; CandidateScanMilliseconds=$candidateScanMs; TripleLookupMilliseconds=$found.TripleLookupMilliseconds; GeometryValidationMilliseconds=$found.GeometryValidationMilliseconds; SemanticValidationMilliseconds=($watch.ElapsedMilliseconds-$semanticStartMs); TotalMilliseconds=$watch.ElapsedMilliseconds; LastStage='SEMANTIC_VALIDATION' } }; $validation = Test-BnetRowsWithReader -Definition $candidate.Definition -ReadBytes $reader; if ($validation.Valid) { $semantic += [pscustomobject]@{ Valid=$true; Definition=$candidate.Definition; MethodVersion=$candidate.MethodVersion; Counts=$validation.Counts } } }
        $semanticMs = $watch.ElapsedMilliseconds - $semanticStartMs
        & $progress 'SEMANTIC_VALIDATION_DONE' @{ semanticCandidates=$semantic.Count; semanticValidationMs=$semanticMs }
        [pscustomobject]@{ Candidates=@($semantic); Reason = if ($semantic.Count -gt 0) { 'OK' } else { 'NO_UNIQUE_VALID_GEOMETRY' }; RawCountLikeCandidates=$found.RawCountCandidates; TripleAddressCandidates=$found.TripleAddressCandidates; CountRangeCandidates=$found.CountRangeCandidates; GeometryCandidates=$found.CountRangeCandidates; ThreeModeCandidates=$found.StateGeometryCandidates; SemanticCandidates=$semantic.Count; WritableRegions=$ranges.Count; BytesScanned=$scanned; ModuleBase=$base; ModuleSize=$moduleSize; CandidateScanMilliseconds=$candidateScanMs; TripleLookupMilliseconds=$found.TripleLookupMilliseconds; GeometryValidationMilliseconds=$found.GeometryValidationMilliseconds; SemanticValidationMilliseconds=$semanticMs; TotalMilliseconds=$watch.ElapsedMilliseconds; LastStage='COMPLETE' }
    }
    catch { [pscustomobject]@{ Candidates=@(); Reason='STRUCTURAL_SCAN_FAILED'; RawCountLikeCandidates=0; TripleAddressCandidates=0; CountRangeCandidates=0; GeometryCandidates=0; ThreeModeCandidates=0; SemanticCandidates=0; WritableRegions=0; BytesScanned=0; ModuleBase=$base; ModuleSize=$moduleSize; CandidateScanMilliseconds=0; TripleLookupMilliseconds=0; GeometryValidationMilliseconds=0; SemanticValidationMilliseconds=0; TotalMilliseconds=$watch.ElapsedMilliseconds; LastStage='STRUCTURAL_SCAN' } }
    finally { [void][Top250StructuralDiscoveryNative]::CloseHandle($handle) }
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
    $methodProperty = $record.PSObject.Properties['methodVersion']; $validationProperty = $record.PSObject.Properties['validation']; $structuralProperty = $record.PSObject.Properties['structural']
    if (($null -eq $methodProperty) -or [string]::IsNullOrWhiteSpace([string]$methodProperty.Value) -or ($null -eq $validationProperty) -or ($null -eq $structuralProperty)) { return [pscustomobject]@{ Status = 'REJECTED'; Reason = 'CACHE_REQUIRED_METADATA_MISSING'; Definition = $null } }
    $validationMetadata = $validationProperty.Value; $structuralMetadata = $structuralProperty.Value
    $shape = Test-BnetLayoutDefinition -Definition $record.definition
    if (-not $shape.Valid) { return [pscustomobject]@{ Status = 'REJECTED'; Reason = ('CACHE_' + $shape.Reason); Definition = $null } }
    if (($validationMetadata.requiredAnchors -ne 9) -or ([string]$validationMetadata.entryStride -ne [string]$record.definition.entryStride) -or ([Int64]$structuralMetadata.stateToCount -ne [Int64]$shape.StateToCount) -or ([Int64]$structuralMetadata.modeSpacing -ne [Int64]$shape.ModeBlockSpacing)) { return [pscustomobject]@{ Status = 'REJECTED'; Reason = 'CACHE_STRUCTURAL_METADATA_MISMATCH'; Definition = $null } }
    [pscustomobject]@{ Status = 'OK'; Definition = $record.definition; MethodVersion = $record.methodVersion }
}

function Write-BnetDiscoveredLayout {
    param([Parameter(Mandatory)][string]$CollectorRoot, [Parameter(Mandatory)][string]$Sha256, [Parameter(Mandatory)][object]$Definition, [Parameter(Mandatory)][string]$MethodVersion)
    $shape = Test-BnetLayoutDefinition -Definition $Definition
    if (-not $shape.Valid) { throw ('DISCOVERY_CACHE_WRITE_REJECTED_' + $shape.Reason) }
    $path = Get-BnetDiscoveredLayoutPath -CollectorRoot $CollectorRoot -Sha256 $Sha256
    $directory = Split-Path -Parent $path; New-Item -ItemType Directory -Force -Path $directory | Out-Null
    $record = [ordered]@{ schemaVersion = $script:DiscoverySchemaVersion; executableSha256 = $Sha256.ToUpperInvariant(); discoveredAtUtc = [DateTime]::UtcNow.ToString('o'); methodVersion = $MethodVersion; validation = [ordered]@{ requiredAnchors = 9; repeatedReadSamples = 2; entryStride = $Definition.entryStride }; structural = [ordered]@{ stateToCount = ('0x{0:X}' -f [Int64]$shape.StateToCount); modeSpacing = ('0x{0:X}' -f [Int64]$shape.ModeBlockSpacing); entryStride = $Definition.entryStride }; definition = $Definition }
    $temp = $path + '.tmp'; $record | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $temp -Encoding UTF8; Move-Item -LiteralPath $temp -Destination $path -Force
    $path
}

Export-ModuleMember -Function ConvertTo-BnetLayoutRva,Test-BnetDiscoverySha,Test-BnetTransientLiveValidationReason,Get-BnetCacheValidationDisposition,Get-BnetDiscoveredLayoutPath,Test-BnetLayoutDefinition,New-BnetLayoutDefinitionFromAnchors,Test-BnetRowsWithReader,Get-BnetGeometryDiscoveryCandidates,Get-BnetGeometryCandidatesFromCounts,Invoke-BnetStructuralDiscovery,Select-BnetDiscoveryCandidate,Read-BnetDiscoveredLayout,Write-BnetDiscoveredLayout
