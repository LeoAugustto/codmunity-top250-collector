[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })]
    [string]$CodPath,
    [string]$OutputRoot = (Join-Path $env:LOCALAPPDATA 'CODMunity\Top250Collector\research\layout-evidence'),
    [ValidateRange(16, 512)]
    [int]$WindowBytes = 96
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Development/research only: this script reads the executable from disk.  It never opens a
# process, reads process memory, writes game files, or adds methods to build_layouts.json.
$knownSha = '97413B95AA94A9694690BA70B16189E4827E2B363850F873387E8A88101D4FE4'
$knownAnchors = [ordered]@{
    mp = [ordered]@{ stateRva = 0x12895050L; countRva = 0x128D079CL; entry0Rva = 0x128D07A0L }
    br = [ordered]@{ stateRva = 0x12917140L; countRva = 0x1295288CL; entry0Rva = 0x12952890L }
    resurgence = [ordered]@{ stateRva = 0x12999230L; countRva = 0x129D497CL; entry0Rva = 0x129D4980L }
}

function Format-LayoutHex([Int64]$Value) { '0x{0:X}' -f $Value }
function Convert-BytesToHex([byte[]]$Bytes) { if ($null -eq $Bytes) { return $null }; [BitConverter]::ToString($Bytes).Replace('-', ' ') }
function Read-FileBytesAt {
    param([string]$Path, [Int64]$Offset, [int]$Count)
    $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try {
        if ($Offset -lt 0 -or $Offset -ge $stream.Length) { return $null }
        $actual = [Math]::Min([int64]$Count, $stream.Length - $Offset)
        $buffer = New-Object byte[] ([int]$actual)
        $stream.Position = $Offset
        $read = $stream.Read($buffer, 0, $buffer.Length)
        if ($read -ne $buffer.Length) { return $null }
        return $buffer
    }
    finally { $stream.Dispose() }
}
function Read-PeLayout {
    param([string]$Path)
    $header = Read-FileBytesAt -Path $Path -Offset 0 -Count 0x4000
    if ($null -eq $header -or [BitConverter]::ToUInt16($header, 0) -ne 0x5A4D) { throw 'PE_DOS_SIGNATURE_INVALID' }
    $peOffset = [BitConverter]::ToInt32($header, 0x3C)
    if ($peOffset -lt 0x40 -or ($peOffset + 0x108) -gt $header.Length -or [BitConverter]::ToUInt32($header, $peOffset) -ne 0x4550) { throw 'PE_SIGNATURE_INVALID' }
    $machine = [BitConverter]::ToUInt16($header, $peOffset + 4)
    $sectionCount = [BitConverter]::ToUInt16($header, $peOffset + 6)
    $timestamp = [BitConverter]::ToUInt32($header, $peOffset + 8)
    $optionalSize = [BitConverter]::ToUInt16($header, $peOffset + 20)
    $optionalOffset = $peOffset + 24
    $magic = [BitConverter]::ToUInt16($header, $optionalOffset)
    if ($magic -ne 0x20B) { throw 'PE64_REQUIRED' }
    $imageBase = [BitConverter]::ToInt64($header, $optionalOffset + 24)
    $sectionAlignment = [BitConverter]::ToUInt32($header, $optionalOffset + 32)
    $fileAlignment = [BitConverter]::ToUInt32($header, $optionalOffset + 36)
    $imageSize = [BitConverter]::ToUInt32($header, $optionalOffset + 56)
    $sectionTable = $optionalOffset + $optionalSize
    $sections = @()
    for ($i = 0; $i -lt $sectionCount; $i++) {
        $offset = $sectionTable + (40 * $i)
        if (($offset + 40) -gt $header.Length) { throw 'PE_SECTION_TABLE_INVALID' }
        $nameBytes = $header[$offset..($offset + 7)]
        $name = [Text.Encoding]::ASCII.GetString($nameBytes).Trim([char]0)
        $sections += [pscustomobject]@{
            name = $name; virtualAddress = [int64][BitConverter]::ToUInt32($header, $offset + 12); virtualSize = [int64][BitConverter]::ToUInt32($header, $offset + 8)
            rawOffset = [int64][BitConverter]::ToUInt32($header, $offset + 20); rawSize = [int64][BitConverter]::ToUInt32($header, $offset + 16)
            characteristics = [uint32][BitConverter]::ToUInt32($header, $offset + 36)
        }
    }
    [pscustomobject]@{ machine = ('0x{0:X4}' -f $machine); timestampUtc = [DateTimeOffset]::FromUnixTimeSeconds($timestamp).UtcDateTime.ToString('o'); imageBase = Format-LayoutHex $imageBase; imageSize = [int64]$imageSize; sectionAlignment = [int64]$sectionAlignment; fileAlignment = [int64]$fileAlignment; sections = $sections }
}
function Convert-RvaToFileOffset {
    param([object]$Pe, [Int64]$Rva)
    foreach ($section in $Pe.sections) {
        $span = [Math]::Max($section.virtualSize, $section.rawSize)
        if ($Rva -ge $section.virtualAddress -and $Rva -lt ($section.virtualAddress + $span)) {
            $delta = $Rva - $section.virtualAddress
            return [pscustomobject]@{ mapped = ($delta -lt $section.rawSize); fileOffset = $section.rawOffset + $delta; section = $section.name; sectionRva = $section.virtualAddress; rawSize = $section.rawSize }
        }
    }
    return [pscustomobject]@{ mapped = $false; fileOffset = $null; section = $null; sectionRva = $null; rawSize = $null }
}
function Convert-RawOffsetToRva {
    param([object]$Pe, [Int64]$RawOffset)
    foreach ($section in $Pe.sections) {
        if ($RawOffset -ge $section.rawOffset -and $RawOffset -lt ($section.rawOffset + $section.rawSize)) { return $section.virtualAddress + ($RawOffset - $section.rawOffset) }
    }
    return $null
}
function Get-AnchorWindow {
    param([string]$Path, [object]$Pe, [Int64]$Rva, [int]$Radius)
    $mapping = Convert-RvaToFileOffset -Pe $Pe -Rva $Rva
    if (-not $mapping.mapped) { return [ordered]@{ rva = Format-LayoutHex $Rva; section = $mapping.section; fileOffset = $null; bytes = $null; reason = 'RVA_NOT_FILE_BACKED' } }
    $startRva = [Math]::Max(0, $Rva - $Radius); $start = Convert-RvaToFileOffset -Pe $Pe -Rva $startRva
    if (-not $start.mapped) { $startRva = $Rva; $start = $mapping }
    $maxBytes = [Math]::Min([int64](2 * $Radius + 1), ($mapping.sectionRva + $mapping.rawSize) - $startRva)
    $bytes = Read-FileBytesAt -Path $Path -Offset $start.fileOffset -Count ([int]$maxBytes)
    [ordered]@{ rva = Format-LayoutHex $Rva; section = $mapping.section; fileOffset = Format-LayoutHex $mapping.fileOffset; windowStartRva = Format-LayoutHex $startRva; centerOffset = $Rva - $startRva; bytes = Convert-BytesToHex $bytes; reason = $null }
}
function Get-RipRelativeReference {
    param([byte[]]$Buffer, [int]$Index)
    if (($Index + 7) -gt $Buffer.Length) { return $null }
    $b0 = $Buffer[$Index]; $b1 = $Buffer[$Index + 1]; $b2 = $Buffer[$Index + 2]
    if ((($b0 -band 0xF0) -eq 0x40) -and ($b1 -eq 0x8D -or $b1 -eq 0x8B) -and (($b2 -band 0xC7) -eq 0x05)) { return [pscustomobject]@{ kind = if ($b1 -eq 0x8D) { 'LEA_RIP_REL32' } else { 'MOV_RIP_REL32' }; displacementOffset = 3; length = 7 } }
    if ($b0 -eq 0x83 -and $b1 -eq 0x3D) { return [pscustomobject]@{ kind = 'CMP_RIP_REL32_IMM8'; displacementOffset = 2; length = 7 } }
    if (($Index + 10) -le $Buffer.Length -and $b0 -eq 0xC7 -and $b1 -eq 0x05) { return [pscustomobject]@{ kind = 'MOV_RIP_REL32_IMM32'; displacementOffset = 2; length = 10 } }
    return $null
}
function Find-NearbyRipRelativeReferences {
    param([string]$Path, [object]$Pe, [hashtable]$AnchorMap, [int]$NearBytes = 0x200)
    if (-not ('LayoutEvidenceStaticScanner' -as [type])) {
        Add-Type @"
using System;
using System.Collections.Generic;
using System.IO;
public sealed class LayoutEvidenceReference { public long SourceRva; public long TargetRva; public int AnchorIndex; public string Kind = ""; public byte[] InstructionBytes = new byte[0]; public int SectionIndex; }
public static class LayoutEvidenceStaticScanner {
    public static LayoutEvidenceReference[] Find(string path, long[] rawOffsets, long[] rawSizes, long[] virtualAddresses, uint[] characteristics, long[] anchors, int nearBytes) {
        var results = new List<LayoutEvidenceReference>(); var seen = new HashSet<string>(); const int chunkSize = 1024 * 1024;
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)) {
            for (var section = 0; section < rawOffsets.Length; section++) {
                if ((characteristics[section] & 0x20000000u) == 0) continue;
                var remaining = rawSizes[section]; var consumed = 0L; var carry = new byte[0];
                stream.Position = rawOffsets[section];
                while (remaining > 0) {
                    var take = (int)Math.Min(chunkSize, remaining); var chunk = new byte[take]; var read = stream.Read(chunk, 0, take); if (read != take) throw new EndOfStreamException();
                    var buffer = new byte[carry.Length + chunk.Length]; if (carry.Length > 0) Buffer.BlockCopy(carry, 0, buffer, 0, carry.Length); Buffer.BlockCopy(chunk, 0, buffer, carry.Length, chunk.Length);
                    var bufferRaw = rawOffsets[section] + consumed - carry.Length;
                    for (var i = 0; i < buffer.Length; i++) {
                        string kind = null; var dispOffset = 0; var length = 0;
                        if (i + 7 <= buffer.Length && (buffer[i] & 0xF0) == 0x40 && (buffer[i + 1] == 0x8D || buffer[i + 1] == 0x8B) && (buffer[i + 2] & 0xC7) == 0x05) { kind = buffer[i + 1] == 0x8D ? "LEA_RIP_REL32" : "MOV_RIP_REL32"; dispOffset = 3; length = 7; }
                        else if (i + 7 <= buffer.Length && buffer[i] == 0x83 && buffer[i + 1] == 0x3D) { kind = "CMP_RIP_REL32_IMM8"; dispOffset = 2; length = 7; }
                        else if (i + 10 <= buffer.Length && buffer[i] == 0xC7 && buffer[i + 1] == 0x05) { kind = "MOV_RIP_REL32_IMM32"; dispOffset = 2; length = 10; }
                        if (kind == null) continue;
                        var sourceRaw = bufferRaw + i; var sourceRva = virtualAddresses[section] + (sourceRaw - rawOffsets[section]); var target = sourceRva + length + BitConverter.ToInt32(buffer, i + dispOffset);
                        for (var anchor = 0; anchor < anchors.Length; anchor++) {
                            if (Math.Abs(target - anchors[anchor]) > nearBytes) continue;
                            var key = sourceRaw.ToString() + "/" + anchor.ToString(); if (!seen.Add(key)) continue;
                            var instruction = new byte[length]; Buffer.BlockCopy(buffer, i, instruction, 0, length);
                            results.Add(new LayoutEvidenceReference { SourceRva = sourceRva, TargetRva = target, AnchorIndex = anchor, Kind = kind, InstructionBytes = instruction, SectionIndex = section });
                        }
                    }
                    var carryLength = Math.Min(9, buffer.Length); carry = new byte[carryLength]; if (carryLength > 0) Buffer.BlockCopy(buffer, buffer.Length - carryLength, carry, 0, carryLength);
                    remaining -= take; consumed += take;
                }
            }
        }
        return results.ToArray();
    }
}
"@
    }
    $keys = @($AnchorMap.Keys | Sort-Object); $anchors = [int64[]]@($keys | ForEach-Object { [int64]$AnchorMap[$_] })
    $sections = @($Pe.sections); $rawOffsets = [int64[]]@($sections | ForEach-Object { [int64]$_.rawOffset }); $rawSizes = [int64[]]@($sections | ForEach-Object { [int64]$_.rawSize }); $virtualAddresses = [int64[]]@($sections | ForEach-Object { [int64]$_.virtualAddress }); $characteristics = [uint32[]]@($sections | ForEach-Object { [uint32]$_.characteristics })
    $rawReferences = [LayoutEvidenceStaticScanner]::Find($Path, $rawOffsets, $rawSizes, $virtualAddresses, $characteristics, $anchors, $NearBytes)
    return @($rawReferences | ForEach-Object { [ordered]@{ anchor = $keys[$_.AnchorIndex]; kind = $_.Kind; sourceRva = Format-LayoutHex $_.SourceRva; targetRva = Format-LayoutHex $_.TargetRva; targetDistance = $_.TargetRva - [int64]$AnchorMap[$keys[$_.AnchorIndex]]; section = $sections[$_.SectionIndex].name; instructionBytes = Convert-BytesToHex $_.InstructionBytes; trust = 'RESEARCH_ONLY_SINGLE_BUILD' } })
}

$resolvedPath = [IO.Path]::GetFullPath($CodPath)
$sha = (Get-FileHash -LiteralPath $resolvedPath -Algorithm SHA256).Hash.ToUpperInvariant()
$pe = Read-PeLayout -Path $resolvedPath
$outputDirectory = Join-Path (Join-Path $OutputRoot $sha) ([DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null
$anchorMap = @{}; $anchorWindows = [ordered]@{}
foreach ($mode in $knownAnchors.Keys) {
    $anchorWindows[$mode] = [ordered]@{}
    foreach ($field in $knownAnchors[$mode].Keys) {
        $rva = [int64]$knownAnchors[$mode][$field]; $anchorMap["$mode/$field"] = $rva
        $anchorWindows[$mode][$field] = Get-AnchorWindow -Path $resolvedPath -Pe $pe -Rva $rva -Radius $WindowBytes
    }
}
$references = @(Find-NearbyRipRelativeReferences -Path $resolvedPath -Pe $pe -AnchorMap $anchorMap)
$relationships = [ordered]@{
    entry0MinusCount = [ordered]@{ mp = 4; br = 4; resurgence = 4 }
    stateToCount = [ordered]@{ mp = $knownAnchors.mp.countRva - $knownAnchors.mp.stateRva; br = $knownAnchors.br.countRva - $knownAnchors.br.stateRva; resurgence = $knownAnchors.resurgence.countRva - $knownAnchors.resurgence.stateRva }
    modeBlockSpacing = [ordered]@{ mpToBr = $knownAnchors.br.stateRva - $knownAnchors.mp.stateRva; brToResurgence = $knownAnchors.resurgence.stateRva - $knownAnchors.br.stateRva }
    entryStride = 0x458
}
$researchCandidates = @($references | ForEach-Object { [ordered]@{ anchor = $_.anchor; kind = $_.kind; sourceRva = $_.sourceRva; instructionBytes = $_.instructionBytes; rationale = 'Potential RIP-relative anchor; requires exact cross-build reproduction and independent runtime validation.'; trust = 'NOT_TRUSTED_ONE_BUILD' } })
$bundle = [ordered]@{
    schemaVersion = 1; capturedAtUtc = [DateTime]::UtcNow.ToString('o'); source = 'static-file-only'; executable = [ordered]@{ sha256 = $sha; pathLeaf = [IO.Path]::GetFileName($resolvedPath); fileSize = (Get-Item -LiteralPath $resolvedPath).Length; knownBaselineShaMatch = ($sha -eq $knownSha) }
    pe = [ordered]@{ machine = $pe.machine; timestampUtc = $pe.timestampUtc; imageBase = $pe.imageBase; imageSize = $pe.imageSize; sectionAlignment = $pe.sectionAlignment; fileAlignment = $pe.fileAlignment; sections = @($pe.sections | ForEach-Object { [ordered]@{ name = $_.name; virtualAddress = Format-LayoutHex $_.virtualAddress; virtualSize = $_.virtualSize; rawOffset = Format-LayoutHex $_.rawOffset; rawSize = $_.rawSize; characteristics = ('0x{0:X8}' -f $_.characteristics) } }) }
    knownAnchors = $anchorWindows; relationships = $relationships; ripRelativeReferences = $references; researchCandidates = $researchCandidates; trustedCatalogModified = $false
    nextUpdateWorkflow = @('Detect and record the unknown SHA while leaving collection fail-closed.', 'Capture this same bundle for the new executable.', 'Compare current and new bundles; require reproducible cross-build instruction/reference evidence.', 'Validate a candidate against both executables and live MP/BR/Resurgence extraction.', 'Only then add one deterministic trusted method and run the unknown-build diagnostic before production enablement.')
}
$jsonPath = Join-Path $outputDirectory 'layout-evidence.json'; $textPath = Join-Path $outputDirectory 'layout-evidence.txt'
$bundle | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $jsonPath -Encoding UTF8
$lines = New-Object System.Collections.Generic.List[string]
$lines.Add('CODMunity Top 250 Battle.net layout evidence (research only)'); $lines.Add("SHA-256: $sha"); $lines.Add("PE image size: $(Format-LayoutHex $pe.imageSize)"); $lines.Add("Sections: $($pe.sections.Count)"); $lines.Add("Anchor windows: 9; RIP-relative references near anchors: $($references.Count)"); $lines.Add('Trusted discovery catalog modified: no')
foreach ($candidate in $researchCandidates) { $lines.Add("RESEARCH CANDIDATE anchor=$($candidate.anchor) kind=$($candidate.kind) source=$($candidate.sourceRva) trust=NOT_TRUSTED_ONE_BUILD") }
if ($researchCandidates.Count -eq 0) { $lines.Add('No recognised RIP-relative reference reached the capture window; this is not evidence to invent a signature.') }
$lines.Add('Next update: capture new bundle, compare, validate both builds, then consider a trusted method.')
$lines | Set-Content -LiteralPath $textPath -Encoding UTF8
Write-Output ("LAYOUT_EVIDENCE_CAPTURED sha={0} json={1} summary={2} references={3}" -f $sha, $jsonPath, $textPath, $references.Count)