param(
    [int]$IntervalSeconds = 300,
    [int]$InitialDelaySeconds = 30,
    [switch]$Once,
    [switch]$Quiet,
    [switch]$EmitJson,
    [int]$VerifiedCodPid = 0,
    [string]$VerifiedCodPath = "",
    [string]$VerifiedPlatform = "",
    [string]$VerifiedBuildSha256 = "",
    [string]$VerifiedLayout = ""
)

$ErrorActionPreference = "Stop"

# -EmitJson is an inter-process contract. A redirected Windows PowerShell console can otherwise select an OEM code page.
if ($EmitJson) {
    $script:MachineReadableUtf8 = [System.Text.UTF8Encoding]::new($false)
    [Console]::OutputEncoding = $script:MachineReadableUtf8
    $OutputEncoding = $script:MachineReadableUtf8
}

$WindowsPowerShell = Join-Path $env:SystemRoot "System32\WindowsPowerShell\v1.0\powershell.exe"
$CollectorRoot = Join-Path $env:LOCALAPPDATA "CODMunity\Top250Collector"
$DataRoot = Join-Path $CollectorRoot "data"
$LogsRoot = Join-Path $CollectorRoot "logs"
$LogPath = Join-Path $LogsRoot "orchestrator.log"
$StatusPath = Join-Path $CollectorRoot "orchestrator_status.json"
$ManifestPath = Join-Path $PSScriptRoot "build_layouts.json"
$DiscoveredLayoutsPath = Join-Path $CollectorRoot "discovered_layouts.json"

if (-not ("Top250OrchestratorNative" -as [type])) {
    Add-Type @"
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

public static class Top250OrchestratorNative
{
    public const uint PROCESS_VM_READ = 0x0010;
    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ReadProcessMemory(
        IntPtr hProcess,
        IntPtr address,
        [Out] byte[] buffer,
        UIntPtr size,
        out UIntPtr bytesRead
    );

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CloseHandle(IntPtr hObject);

    public static byte[] ReadBytes(IntPtr handle, long address, int size)
    {
        byte[] buffer = new byte[size];
        UIntPtr read;
        bool ok = ReadProcessMemory(handle, new IntPtr(address), buffer, (UIntPtr)size, out read);
        if (!ok || read.ToUInt64() != (ulong)size)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), String.Format("ReadProcessMemory failed at 0x{0:X}", address));
        }
        return buffer;
    }
}
"@
}

if (-not ("BnetLayoutStaticScanner" -as [type])) {
    Add-Type @"
using System;
using System.Collections.Generic;
using System.IO;
public static class BnetLayoutStaticScanner
{
    public static long[] Find(string path, byte[] pattern, bool[] fixedBytes)
    {
        const int ChunkSize = 1024 * 1024;
        var hits = new List<long>();
        var carry = new byte[Math.Max(0, pattern.Length - 1)];
        var carryCount = 0;
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var chunk = new byte[ChunkSize];
            long baseOffset = 0;
            int read;
            while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
            {
                var buffer = new byte[carryCount + read];
                if (carryCount > 0) Buffer.BlockCopy(carry, 0, buffer, 0, carryCount);
                Buffer.BlockCopy(chunk, 0, buffer, carryCount, read);
                for (var i = 0; i <= buffer.Length - pattern.Length; i++)
                {
                    var match = true;
                    for (var j = 0; j < pattern.Length; j++)
                    {
                        if (fixedBytes[j] && buffer[i + j] != pattern[j]) { match = false; break; }
                    }
                    if (match) hits.Add(baseOffset - carryCount + i);
                }
                carryCount = Math.Min(carry.Length, buffer.Length);
                if (carryCount > 0) Buffer.BlockCopy(buffer, buffer.Length - carryCount, carry, 0, carryCount);
                baseOffset += read;
            }
        }
        return hits.ToArray();
    }
}
"@
}

function Write-Log {
    param([string]$Message)
    $line = "{0} {1}" -f [DateTime]::UtcNow.ToString("o"), $Message
    Add-Content -LiteralPath $LogPath -Value $line -Encoding UTF8
    if (-not $Quiet) { Write-Host $line }
}

function Get-CodState {
    $candidates = @()
    foreach ($process in @(Get-Process -Name "cod" -ErrorAction SilentlyContinue)) {
        try {
            $path = $process.Path
            if ([string]::IsNullOrWhiteSpace($path)) { continue }
            if (([IO.Path]::GetFileName($path) -ine "cod.exe") -or -not (Test-Path -LiteralPath $path -PathType Leaf)) { continue }
            $candidates += [pscustomobject]@{ Process = $process; Path = [IO.Path]::GetFullPath($path) }
        }
        catch {
            Write-Log ("COD_PATH_UNAVAILABLE pid={0} reason={1}" -f $process.Id, $_.Exception.Message)
        }
    }
    if ($candidates.Count -eq 0) { return [pscustomobject]@{ State = "NONE"; Candidate = $null } }
    if ($candidates.Count -ne 1) {
        Write-Log ("COD_AMBIGUOUS candidateCount={0} pids={1}" -f $candidates.Count, (($candidates | ForEach-Object { $_.Process.Id }) -join ","))
        return [pscustomobject]@{ State = "AMBIGUOUS"; Candidate = $null }
    }
    return [pscustomobject]@{ State = "FOUND"; Candidate = $candidates[0] }
}

function Test-WindowsAppsCodPath {
    param([string]$Path)
    return ($Path.IndexOf("\WindowsApps\", [StringComparison]::OrdinalIgnoreCase) -ge 0)
}

function Get-PlatformHintForCodPath {
    param([string]$Path)
    $normalized = [IO.Path]::GetFullPath($Path)
    if ($normalized.IndexOf("\steamapps\common\Call of Duty HQ\", [StringComparison]::OrdinalIgnoreCase) -ge 0) { return "steam" }
    if ((Test-WindowsAppsCodPath -Path $normalized) -or ($normalized.IndexOf("\XboxGames\Call of Duty\Content\", [StringComparison]::OrdinalIgnoreCase) -ge 0)) { return "xbox" }
    if ($normalized.EndsWith("\Call of Duty\_retail_\cod.exe", [StringComparison]::OrdinalIgnoreCase)) { return "battlenet" }
    return $null
}

function Convert-LayoutRva {
    param([object]$Value)
    if ($Value -is [string] -and $Value.StartsWith("0x", [StringComparison]::OrdinalIgnoreCase)) { return [Convert]::ToInt64($Value.Substring(2), 16) }
    return [Int64]$Value
}

function Convert-RawOffsetToRva {
    param([byte[]]$Header, [Int64]$RawOffset)
    if (($Header.Length -lt 0x100) -or ([BitConverter]::ToUInt16($Header, 0) -ne 0x5A4D)) { throw "INVALID_PE_DOS_SIGNATURE" }
    $pe = [int][BitConverter]::ToUInt32($Header, 0x3C)
    if (($pe -lt 0x40) -or (($pe + 24) -gt $Header.Length) -or ([BitConverter]::ToUInt32($Header, $pe) -ne 0x00004550)) { throw "INVALID_PE_SIGNATURE" }
    $sectionCount = [int][BitConverter]::ToUInt16($Header, $pe + 6)
    $optionalSize = [int][BitConverter]::ToUInt16($Header, $pe + 20)
    $sectionOffset = $pe + 24 + $optionalSize
    for ($i = 0; $i -lt $sectionCount; $i++) {
        $offset = $sectionOffset + ($i * 40)
        if (($offset + 40) -gt $Header.Length) { throw "INVALID_PE_SECTION_TABLE" }
        $virtualAddress = [Int64][BitConverter]::ToUInt32($Header, $offset + 12)
        $rawSize = [Int64][BitConverter]::ToUInt32($Header, $offset + 16)
        $rawStart = [Int64][BitConverter]::ToUInt32($Header, $offset + 20)
        if (($RawOffset -ge $rawStart) -and ($RawOffset -lt ($rawStart + $rawSize))) { return $virtualAddress + $RawOffset - $rawStart }
    }
    throw "SIGNATURE_OUTSIDE_PE_SECTION"
}

function Get-BnetLayoutFromSignature {
    param([string]$CodPath)
    [byte[]]$pattern = @(0x0F,0xB6,0xC2,0x4C,0x8D,0x05,0,0,0,0,0x48,0x69,0xC8,0xF0,0x20,0x08,0,0x33,0xD2,0x49,0x8D,0x80,0x18,0xB8,0x03,0,0x48,0x03,0xC1,0x42,0x80,0xBC,0x01,0xC8,0,0,0,0x03,0x48,0x0F,0x45,0xC2,0xC3)
    [bool[]]$fixedBytes = @($true,$true,$true,$true,$true,$true,$false,$false,$false,$false,$true,$true,$true,$true,$true,$true,$true,$true,$true,$true,$true,$true,$true,$true,$true,$true,$true,$true,$true,$true,$true,$true,$true,$true,$true,$true,$true,$true,$true,$true,$true,$true,$true)
    $matches = [BnetLayoutStaticScanner]::Find($CodPath, $pattern, $fixedBytes)
    if ($matches.Count -ne 1) { throw "SIGNATURE_MATCH_COUNT_$($matches.Count)" }
    $stream = [IO.File]::Open($CodPath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try {
        $header = New-Object byte[] 0x1000; [void]$stream.Read($header, 0, $header.Length)
        $matchRaw = [Int64]$matches[0]; $matchRva = Convert-RawOffsetToRva -Header $header -RawOffset $matchRaw
        $stream.Position = $matchRaw + 6; $dispBytes = New-Object byte[] 4; [void]$stream.Read($dispBytes, 0, 4)
        $slotBase = $matchRva + 10 + [BitConverter]::ToInt32($dispBytes, 0)
        if ($slotBase -le 0) { throw "INVALID_SLOT_BASE" }
        $modes = [ordered]@{}
        foreach ($item in @(@("mp",0),@("br",1),@("resurgence",2))) {
            $slot = $slotBase + ([Int64]$item[1] * 0x820F0)
            $modes[$item[0]] = [pscustomobject]@{ stateRva = ("0x{0:X}" -f ($slot + 0xC8)); countRva = ("0x{0:X}" -f ($slot + 0x3B814)); entry0Rva = ("0x{0:X}" -f ($slot + 0x3B818)) }
        }
        return [pscustomobject]@{ name = "bnet-signature-discovered"; priority = 0; source = "signature-discovery"; signatureRva = ("0x{0:X}" -f $matchRva); datasets = [pscustomobject]$modes }
    }
    finally { $stream.Dispose() }
}

function Get-XboxRuntimeBuildId {
    param([Diagnostics.Process]$Cod)
    try { $moduleBase = [Int64]$Cod.MainModule.BaseAddress.ToInt64() }
    catch { throw "XBOX_MODULE_BASE_UNAVAILABLE: $($_.Exception.Message)" }
    if ($moduleBase -le 0) { throw "XBOX_MODULE_BASE_UNAVAILABLE" }

    $access = [Top250OrchestratorNative]::PROCESS_VM_READ -bor [Top250OrchestratorNative]::PROCESS_QUERY_LIMITED_INFORMATION
    $handle = [Top250OrchestratorNative]::OpenProcess($access, $false, $Cod.Id)
    if ($handle -eq [IntPtr]::Zero) { throw "XBOX_OPEN_PROCESS_FAILED" }
    try {
        $header = [Top250OrchestratorNative]::ReadBytes($handle, $moduleBase, 0x1000)
        if ([BitConverter]::ToUInt16($header, 0) -ne 0x5A4D) { throw "INVALID_PE_DOS_SIGNATURE" }
        $pe = [int][BitConverter]::ToUInt32($header, 0x3C)
        if (($pe -lt 0x40) -or (($pe + 24) -gt $header.Length) -or ([BitConverter]::ToUInt32($header, $pe) -ne 0x00004550)) { throw "INVALID_PE_SIGNATURE" }
        $machine = [BitConverter]::ToUInt16($header, $pe + 4)
        $sections = [BitConverter]::ToUInt16($header, $pe + 6)
        $timestamp = [BitConverter]::ToUInt32($header, $pe + 8)
        $optional = $pe + 24
        if ([BitConverter]::ToUInt16($header, $optional) -ne 0x20B) { throw "INVALID_PE_OPTIONAL_HEADER" }
        $entry = [BitConverter]::ToUInt32($header, $optional + 0x10)
        $size = [BitConverter]::ToUInt32($header, $optional + 0x38)
        $checksum = [BitConverter]::ToUInt32($header, $optional + 0x40)
        return ("pe64-{0:X8}-{1:X8}-{2:X8}-{3:X4}-{4}-{5:X8}" -f $timestamp, $size, $entry, $machine, $sections, $checksum)
    }
    finally {
        if ($handle -ne [IntPtr]::Zero) { [void][Top250OrchestratorNative]::CloseHandle($handle) }
    }
}

function Get-CodIdentity {
    param([Diagnostics.Process]$Cod, [string]$CodPath)
    $platformHint = Get-PlatformHintForCodPath -Path $CodPath
    if ($platformHint -eq "xbox" -and (Test-WindowsAppsCodPath -Path $CodPath)) {
        try { return [pscustomobject]@{ Type = "runtimeBuildId"; Value = (Get-XboxRuntimeBuildId -Cod $Cod); PlatformHint = $platformHint; Error = $null } }
        catch { return [pscustomobject]@{ Type = "unavailable"; Value = $null; PlatformHint = $platformHint; Error = $_.Exception.Message } }
    }
    try { return [pscustomobject]@{ Type = "sha256"; Value = (Get-FileHash -LiteralPath $CodPath -Algorithm SHA256 -ErrorAction Stop).Hash; PlatformHint = $platformHint; Error = $null } }
    catch { return [pscustomobject]@{ Type = "unavailable"; Value = $null; PlatformHint = $platformHint; Error = $_.Exception.Message } }
}

function Get-VerifiedBuildIdentity {
    param([Diagnostics.Process]$Cod, [string]$CodPath)
    if ($VerifiedCodPid -le 0 -and [string]::IsNullOrWhiteSpace($VerifiedBuildSha256)) { return $null }
    $platformHint = Get-PlatformHintForCodPath -Path $CodPath
    if (($VerifiedCodPid -ne $Cod.Id) -or
        [string]::IsNullOrWhiteSpace($VerifiedCodPath) -or
        (-not [string]::Equals([IO.Path]::GetFullPath($VerifiedCodPath), $CodPath, [StringComparison]::OrdinalIgnoreCase)) -or
        ($VerifiedPlatform -notin @("battlenet", "steam")) -or
        ($VerifiedPlatform -ne $platformHint) -or
        ($VerifiedBuildSha256 -notmatch '^[A-Fa-f0-9]{64}$')) {
        Write-Log "VERIFIED_BUILD_IDENTITY_REJECTED reason=pid-path-platform-or-sha-mismatch"
        return $null
    }
    return [pscustomobject]@{ Type = "sha256"; Value = $VerifiedBuildSha256.ToUpperInvariant(); PlatformHint = $platformHint; Error = $null }
}

function Get-BuildManifest {
    if (-not (Test-Path -LiteralPath $ManifestPath -PathType Leaf)) { throw "Build manifest not found: $ManifestPath" }
    try { return (Get-Content -LiteralPath $ManifestPath -Raw -Encoding UTF8 | ConvertFrom-Json -ErrorAction Stop) }
    catch { throw "Build manifest is invalid: $($_.Exception.Message)" }
}

function Get-BuildLayout {
    param([object]$Manifest, [object]$Identity)
    $collection = if ($Identity.Type -eq "sha256") { $Manifest.sha256Builds } elseif ($Identity.Type -eq "runtimeBuildId") { $Manifest.runtimeBuilds } else { $null }
    if ($null -eq $collection) { return $null }
    $property = $collection.PSObject.Properties[[string]$Identity.Value]
    if ($null -eq $property) { return $null }
    return $property.Value
}

function Get-Datasets {
    param([object]$Layout)
    $folder = Join-Path $PSScriptRoot (Join-Path "exporters" $Layout.exporterSet)
    $names = @("top250_mp.ps1", "top250_br.ps1", "top250_resurgence.ps1")
    $keys = @("mp", "br", "resurgence")
    $labels = @("MP", "BR", "RESURGENCE")
    $datasets = @()
    for ($i = 0; $i -lt $keys.Count; $i++) {
        $outputDir = Join-Path $DataRoot $keys[$i]
        $isDynamicBnet = $Layout.exporterSet -eq "bnet-dynamic"
        $exporter = if ($isDynamicBnet) { Join-Path $PSScriptRoot "exporters\top250_bnet_dynamic_layout.ps1" } else { Join-Path $folder $names[$i] }
        $datasets += [pscustomobject]@{
            Key = $keys[$i]; Label = $labels[$i]; Exporter = $exporter; OutputDir = $outputDir; DynamicBnet = $isDynamicBnet
            Rvas = if ($isDynamicBnet) { $Layout.bnetDefinition.datasets.($keys[$i]) } else { $null }
            LatestJson = (Join-Path $outputDir "latest.json"); ExpectedMode = $keys[$i]
        }
    }
    return $datasets
}

function Get-LastWriteUtc {
    param([string]$Path)
    if (Test-Path -LiteralPath $Path -PathType Leaf) { return (Get-Item -LiteralPath $Path).LastWriteTimeUtc }
    return $null
}

function Test-ExporterPayload {
    param([object]$json, [string]$ExpectedMode, [DateTime]$StartedAt, [object]$Identity)
    if ($json.mode -ne $ExpectedMode) { return [ordered]@{ Status = "FAILED"; Reason = "MODE_MISMATCH" } }
    if ($Identity.Type -eq "sha256") {
        if ([string]::IsNullOrWhiteSpace([string]$json.buildSha256)) { return [ordered]@{ Status = "FAILED"; Reason = "BUILD_HASH_MISSING" } }
        if ([string]$json.buildSha256 -ne [string]$Identity.Value) { return [ordered]@{ Status = "FAILED"; Reason = "BUILD_HASH_MISMATCH" } }
    }
    elseif ($Identity.Type -eq "runtimeBuildId") {
        if ([string]::IsNullOrWhiteSpace([string]$json.runtimeBuildId)) { return [ordered]@{ Status = "FAILED"; Reason = "RUNTIME_BUILD_ID_MISSING" } }
        if ([string]$json.runtimeBuildId -ne [string]$Identity.Value) { return [ordered]@{ Status = "FAILED"; Reason = "RUNTIME_BUILD_ID_MISMATCH" } }
    }
    try { $capturedAt = [DateTime]::Parse([string]$json.capturedAt).ToUniversalTime() }
    catch { return [ordered]@{ Status = "FAILED"; Reason = "CAPTURED_AT_INVALID" } }
    if ($capturedAt -lt $StartedAt.AddSeconds(-1)) { return [ordered]@{ Status = "FAILED"; Reason = "CAPTURED_AT_STALE" } }
    if (($json.nativeCount -lt 1) -or ($json.nativeCount -gt 260)) { return [ordered]@{ Status = "FAILED"; Reason = "NATIVE_COUNT_INVALID" } }
    if (($json.rowCount -lt 1) -or ($json.rowCount -gt 260)) { return [ordered]@{ Status = "FAILED"; Reason = "ROW_COUNT_INVALID" } }
    if ($null -eq $json.players) { return [ordered]@{ Status = "FAILED"; Reason = "PLAYERS_MISSING" } }
    $players = @($json.players)
    if ($players.Count -ne [int]$json.rowCount) { return [ordered]@{ Status = "FAILED"; Reason = "ROW_COUNT_MISMATCH" } }
    foreach ($player in $players) {
        foreach ($field in @("position", "name", "sr", "xuid")) {
            if (-not ($player.PSObject.Properties.Name -contains $field)) { return [ordered]@{ Status = "FAILED"; Reason = "PLAYER_FIELD_MISSING:$field" } }
        }
    }
    $safePlayers = @($players | ForEach-Object { [ordered]@{ position = [int]$_.position; sr = [int]$_.sr; name = [string]$_.name } })
    return [ordered]@{ Status = "OK"; Rows = [int]$json.rowCount; CapturedAt = $capturedAt.ToString("o"); NativeCount = [int]$json.nativeCount; Players = $safePlayers }
}

function Invoke-Exporter {
    param([object]$Dataset, [string]$ExpectedCodPath, [string]$ExporterSet, [object]$Identity)
    $startedAt = [DateTime]::UtcNow
    $stopwatch = [Diagnostics.Stopwatch]::StartNew()
    if (-not (Test-Path -LiteralPath $WindowsPowerShell -PathType Leaf)) { return [ordered]@{ Status = "FAILED"; Reason = "WINDOWS_POWERSHELL_NOT_FOUND"; DurationMs = 0 } }
    if (-not (Test-Path -LiteralPath $Dataset.Exporter -PathType Leaf)) { return [ordered]@{ Status = "FAILED"; Reason = "EXPORTER_NOT_FOUND"; DurationMs = 0 } }
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $WindowsPowerShell
    $args = '-NoProfile -ExecutionPolicy Bypass -File "{0}" -Quiet -EmitJson' -f $Dataset.Exporter
    if ($Dataset.DynamicBnet) {
        $args += ' -ExpectedCodPath "{0}" -Mode {1} -ExpectedSha256 {2} -ManagerRva {3} -CountRva {4} -Entry0Rva {5}' -f $ExpectedCodPath, $Dataset.Key, $Identity.Value, (Convert-LayoutRva $Dataset.Rvas.stateRva), (Convert-LayoutRva $Dataset.Rvas.countRva), (Convert-LayoutRva $Dataset.Rvas.entry0Rva)
    }
    elseif ($ExporterSet -ne "xbox") { $args += ' -ExpectedCodPath "{0}"' -f $ExpectedCodPath }
    if ($Identity.Type -eq "sha256") { $args += ' -VerifiedBuildSha256 {0}' -f $Identity.Value }
    $psi.Arguments = $args
    $psi.UseShellExecute = $false; $psi.CreateNoWindow = $true; $psi.RedirectStandardOutput = $true; $psi.RedirectStandardError = $true
    $psi.StandardOutputEncoding = [System.Text.UTF8Encoding]::new($false); $psi.StandardErrorEncoding = [System.Text.UTF8Encoding]::new($false)
    try {
        $child = [Diagnostics.Process]::Start($psi)
        if ($null -eq $child) { return [ordered]@{ Status = "FAILED"; Reason = "EXPORTER_START_FAILED"; DurationMs = 0 } }
        try { $child.PriorityClass = [Diagnostics.ProcessPriorityClass]::BelowNormal } catch { }
        $stdout = $child.StandardOutput.ReadToEnd(); $stderr = $child.StandardError.ReadToEnd(); $child.WaitForExit(); $stopwatch.Stop()
        if ($child.ExitCode -eq 23) {
            $reason = "CACHE_NOT_READY"
            if (-not [string]::IsNullOrWhiteSpace($stderr)) { $reason += " stderr=" + $stderr.Trim() }
            elseif (-not [string]::IsNullOrWhiteSpace($stdout)) { $reason += " stdout=" + $stdout.Trim() }
            return [ordered]@{ Status = "NOT_READY"; Reason = $reason; DurationMs = $stopwatch.ElapsedMilliseconds; ExitCode = 23 }
        }
        if ($child.ExitCode -ne 0) {
            $reason = "EXPORTER_EXIT_{0}" -f $child.ExitCode
            if (-not [string]::IsNullOrWhiteSpace($stderr)) { $reason += " stderr=" + $stderr.Trim() }
            elseif (-not [string]::IsNullOrWhiteSpace($stdout)) { $reason += " stdout=" + $stdout.Trim() }
            return [ordered]@{ Status = "FAILED"; Reason = $reason; DurationMs = $stopwatch.ElapsedMilliseconds; ExitCode = $child.ExitCode }
        }
        try { $payload = $stdout | ConvertFrom-Json -ErrorAction Stop } catch { return [ordered]@{ Status = "FAILED"; Reason = "EXPORTER_JSON_INVALID"; DurationMs = $stopwatch.ElapsedMilliseconds; ExitCode = 0 } }
        $validation = Test-ExporterPayload -json $payload -ExpectedMode $Dataset.ExpectedMode -StartedAt $startedAt -Identity $Identity
        $validation.DurationMs = $stopwatch.ElapsedMilliseconds; $validation.ExitCode = 0
        return $validation
    }
    catch {
        $stopwatch.Stop()
        return [ordered]@{ Status = "FAILED"; Reason = "EXPORTER_EXCEPTION: $($_.Exception.Message)"; DurationMs = $stopwatch.ElapsedMilliseconds }
    }
}

function New-DynamicBnetLayout {
    param([object]$Definition)
    return [pscustomobject]@{ platform = "battlenet"; layout = [string]$Definition.name; exporterSet = "bnet-dynamic"; bnetDefinition = $Definition }
}

function Invoke-BnetLayoutValidation {
    param([Diagnostics.Process]$Cod, [string]$CodPath, [object]$Identity, [object]$Definition)
    $layout = New-DynamicBnetLayout -Definition $Definition
    $results = @(); foreach ($dataset in @(Get-Datasets -Layout $layout)) { $results += Invoke-Exporter -Dataset $dataset -ExpectedCodPath $CodPath -ExporterSet $layout.exporterSet -Identity $Identity }
    $passed = ($results.Count -eq 3) -and (@($results | Where-Object { $_.Status -ne "OK" }).Count -eq 0) -and (@($results | Where-Object { $_.Rows -ne 250 -or $_.NativeCount -ne 250 }).Count -eq 0)
    return [pscustomobject]@{ Passed = $passed; Layout = $layout; Results = $results }
}

function Read-DiscoveredBnetDefinition {
    param([string]$Sha256)
    if (-not (Test-Path -LiteralPath $DiscoveredLayoutsPath -PathType Leaf)) { return $null }
    try { $cache = Get-Content -LiteralPath $DiscoveredLayoutsPath -Raw -Encoding UTF8 | ConvertFrom-Json -ErrorAction Stop }
    catch {
        $backup = "$DiscoveredLayoutsPath.corrupt.$([DateTime]::UtcNow.ToString('yyyyMMddHHmmss')).json"
        Move-Item -LiteralPath $DiscoveredLayoutsPath -Destination $backup -Force
        Write-Log "DISCOVERED_LAYOUT_CACHE_INVALID reason=CORRUPT_JSON backup=$backup"
        return $null
    }
    if ($null -eq $cache.battleNet) { return $null }
    $entry = $cache.battleNet.PSObject.Properties[$Sha256]
    if ($null -eq $entry) { return $null }
    return $entry.Value
}

function Get-VerifiedDiscoveredBnetLayout {
    param([object]$Identity)
    if ([string]::IsNullOrWhiteSpace($VerifiedLayout)) { return $null }
    $definition = Read-DiscoveredBnetDefinition -Sha256 ([string]$Identity.Value)
    if (($null -eq $definition) -or ([string]$definition.name -ne $VerifiedLayout)) { return $null }
    Write-Log "DISCOVERED_LAYOUT_SESSION_CACHE_HIT layout=$VerifiedLayout"
    return New-DynamicBnetLayout -Definition $definition
}

function Write-DiscoveredBnetDefinition {
    param([string]$Sha256, [object]$Definition, [string]$Source)
    $entries = [ordered]@{}
    if (Test-Path -LiteralPath $DiscoveredLayoutsPath -PathType Leaf) {
        try { $existing = Get-Content -LiteralPath $DiscoveredLayoutsPath -Raw -Encoding UTF8 | ConvertFrom-Json -ErrorAction Stop; if ($existing.battleNet) { foreach ($property in $existing.battleNet.PSObject.Properties) { $entries[$property.Name] = $property.Value } } }
        catch { }
    }
    $entries[$Sha256] = [ordered]@{ name = [string]$Definition.name; source = $Source; validatedAtUtc = [DateTime]::UtcNow.ToString("o"); signatureRva = $Definition.signatureRva; datasets = $Definition.datasets }
    $temp = "$DiscoveredLayoutsPath.tmp"; [ordered]@{ battleNet = $entries } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $temp -Encoding UTF8; Move-Item -LiteralPath $temp -Destination $DiscoveredLayoutsPath -Force
    Write-Log "DISCOVERED_LAYOUT_CACHED sha256=$Sha256 source=$Source"
}

function Resolve-UnknownBnetLayout {
    param([Diagnostics.Process]$Cod, [string]$CodPath, [object]$Identity, [object]$Manifest)
    $cached = Read-DiscoveredBnetDefinition -Sha256 ([string]$Identity.Value)
    if ($null -ne $cached) {
        $validation = Invoke-BnetLayoutValidation -Cod $Cod -CodPath $CodPath -Identity $Identity -Definition $cached
        if ($validation.Passed) { return $validation.Layout }
        Write-Log "DISCOVERED_LAYOUT_CACHE_INVALID sha256=$($Identity.Value) reason=RUNTIME_VALIDATION_FAILED"
    }
    foreach ($property in @($Manifest.battlenetLayoutDefinitions.PSObject.Properties | Sort-Object { [int]$_.Value.priority } -Descending)) {
        $definition = $property.Value; $definition | Add-Member -NotePropertyName name -NotePropertyValue $property.Name -Force
        Write-Log "KNOWN_LAYOUT_PROBE $($property.Name) START"
        $validation = Invoke-BnetLayoutValidation -Cod $Cod -CodPath $CodPath -Identity $Identity -Definition $definition
        Write-Log ("KNOWN_LAYOUT_PROBE {0} {1}" -f $property.Name, $(if ($validation.Passed) { "PASS" } else { "FAIL" }))
        if ($validation.Passed) { Write-DiscoveredBnetDefinition -Sha256 ([string]$Identity.Value) -Definition $definition -Source "known-layout-probe"; return $validation.Layout }
    }
    Write-Log "SIGNATURE_DISCOVERY_START sha256=$($Identity.Value)"
    try { $definition = Get-BnetLayoutFromSignature -CodPath $CodPath; Write-Log "SIGNATURE_DISCOVERY_MATCH signatureRva=$($definition.signatureRva)" }
    catch { Write-Log "SIGNATURE_DISCOVERY_FAIL reason=$($_.Exception.Message)"; return $null }
    $validation = Invoke-BnetLayoutValidation -Cod $Cod -CodPath $CodPath -Identity $Identity -Definition $definition
    if (-not $validation.Passed) { Write-Log "SIGNATURE_DISCOVERY_FAIL reason=RUNTIME_VALIDATION_FAILED"; return $null }
    Write-Log "DISCOVERED_LAYOUT_VALIDATED sha256=$($Identity.Value)"
    Write-DiscoveredBnetDefinition -Sha256 ([string]$Identity.Value) -Definition $definition -Source "signature-discovery"
    return $validation.Layout
}

function Write-Status {
    param([int]$CodPid, [string]$CodPath, [string]$CodSha256, [string]$RuntimeBuildId, [string]$Platform, [string]$Layout, [string]$CycleStatus, [object[]]$Datasets, [hashtable]$Results)
    $datasetStatuses = [ordered]@{}
    foreach ($dataset in $Datasets) {
        $result = $Results[$dataset.Key]
        if ($null -eq $result) { $result = [ordered]@{ Status = "FAILED"; Reason = "RESULT_MISSING" } }
        $datasetStatuses[$dataset.Key] = [ordered]@{
            status = $result.Status; reason = $result.Reason; exitCode = $result.ExitCode; rows = $result.Rows
            nativeCount = $result.NativeCount; capturedAt = $result.CapturedAt; durationMs = $result.DurationMs; players = $result.Players
        }
    }
    $status = [ordered]@{
        capturedAt = [DateTime]::UtcNow.ToString("o"); codPid = $CodPid; codPath = $CodPath; codSha256 = $CodSha256; runtimeBuildId = $RuntimeBuildId
        platform = $Platform; layout = $Layout; cycleStatus = $CycleStatus; datasets = $datasetStatuses
    }
    if ($EmitJson) { [Console]::Out.WriteLine(($status | ConvertTo-Json -Depth 7 -Compress)) }
}

function Write-IdentityStatus {
    param([int]$CodPid, [string]$CodPath, [object]$Identity, [string]$CycleStatus)
    $sha = if ($Identity.Type -eq "sha256") { [string]$Identity.Value } else { $null }
    $runtime = if ($Identity.Type -eq "runtimeBuildId") { [string]$Identity.Value } else { $null }
    $platform = if ($Identity.PlatformHint) { $Identity.PlatformHint } else { "unknown" }
    Write-Status -CodPid $CodPid -CodPath $CodPath -CodSha256 $sha -RuntimeBuildId $runtime -Platform $platform -Layout "unknown" -CycleStatus $CycleStatus -Datasets @() -Results @{}
}

function Invoke-CollectionCycle {
    param([Diagnostics.Process]$Cod, [string]$CodPath, [object]$Identity, [object]$Layout, [object[]]$Datasets)
    $cycle = [Diagnostics.Stopwatch]::StartNew(); $results = @{}
    Write-Log ("CYCLE_START pid={0} platform={1} layout={2}" -f $Cod.Id, $Layout.platform, $Layout.layout)
    foreach ($dataset in $Datasets) {
        $result = Invoke-Exporter -Dataset $dataset -ExpectedCodPath $CodPath -ExporterSet $Layout.exporterSet -Identity $Identity
        $results[$dataset.Key] = $result
        if ($result.Status -eq "OK") { Write-Log ("{0} OK rows={1} nativeCount={2} durationMs={3}" -f $dataset.Label, $result.Rows, $result.NativeCount, $result.DurationMs) }
        elseif ($result.Status -eq "NOT_READY") { Write-Log ("{0} NOT_READY reason={1} exitCode={2} durationMs={3}" -f $dataset.Label, $result.Reason, $result.ExitCode, $result.DurationMs) }
        else { Write-Log ("{0} FAILED reason={1} exitCode={2} durationMs={3}" -f $dataset.Label, $result.Reason, $result.ExitCode, $result.DurationMs) }
    }
    $cycle.Stop()
    $failed = @($results.Values | Where-Object { $_.Status -eq "FAILED" }).Count; $ok = @($results.Values | Where-Object { $_.Status -eq "OK" }).Count; $notReady = @($results.Values | Where-Object { $_.Status -eq "NOT_READY" }).Count
    if ($failed -gt 0) { $cycleStatus = if ($failed -eq $results.Count) { "FAILED" } else { "PARTIAL_FAILURE" } }
    elseif ($ok -eq $results.Count) { $cycleStatus = "OK" }
    elseif ($ok -gt 0 -and ($ok + $notReady -eq $results.Count)) { $cycleStatus = "PARTIAL_READY" }
    elseif ($notReady -eq $results.Count) { $cycleStatus = "WAITING_FOR_CACHE" }
    else { $cycleStatus = "PARTIAL_FAILURE" }
    $sha = if ($Identity.Type -eq "sha256") { [string]$Identity.Value } else { $null }
    $runtime = if ($Identity.Type -eq "runtimeBuildId") { [string]$Identity.Value } else { $null }
    Write-Status -CodPid $Cod.Id -CodPath $CodPath -CodSha256 $sha -RuntimeBuildId $runtime -Platform $Layout.platform -Layout $Layout.layout -CycleStatus $cycleStatus -Datasets $Datasets -Results $results
    Write-Log ("CYCLE_{0} durationMs={1}" -f $cycleStatus, $cycle.ElapsedMilliseconds)
}

if ($IntervalSeconds -lt 1) { throw "IntervalSeconds must be at least 1." }
if ($InitialDelaySeconds -lt 0) { throw "InitialDelaySeconds cannot be negative." }
New-Item -ItemType Directory -Force -Path $LogsRoot | Out-Null
$manifest = Get-BuildManifest
$trackedPid = $null; $lastWaitState = $null; $activeLayout = $null; $activeIdentity = $null

while ($true) {
    $codState = Get-CodState
    if ($codState.State -ne "FOUND") {
        if ($Once) { Write-Log ("COD_{0} once=true" -f $codState.State); exit 1 }
        if ($lastWaitState -ne $codState.State) { Write-Log ("COD_{0}" -f $codState.State); $lastWaitState = $codState.State }
        $trackedPid = $null; $activeLayout = $null; $activeIdentity = $null
        Start-Sleep -Seconds 5; continue
    }
    $lastWaitState = $null
    $cod = $codState.Candidate.Process; $codPath = $codState.Candidate.Path
    if ($trackedPid -ne $cod.Id) {
        $trackedPid = $cod.Id; $activeIdentity = Get-VerifiedBuildIdentity -Cod $cod -CodPath $codPath; if ($null -eq $activeIdentity) { $activeIdentity = Get-CodIdentity -Cod $cod -CodPath $codPath }; $activeLayout = $null; $initialDelayApplied = $false
        if ($activeIdentity.Type -eq "unavailable") {
            Write-Log ('COD_DETECTED pid={0} codPath="{1}" platform={2} buildIdentityUnavailable reason="{3}" osVersion="{4}" powerShellVersion={5}' -f $cod.Id, $codPath, $activeIdentity.PlatformHint, $activeIdentity.Error, [Environment]::OSVersion.VersionString, $PSVersionTable.PSVersion)
            Write-Log ("BUILD_ID_UNAVAILABLE codPath=`"{0}`" reason=`"{1}`"" -f $codPath, $activeIdentity.Error)
            Write-IdentityStatus -CodPid $cod.Id -CodPath $codPath -Identity $activeIdentity -CycleStatus "BUILD_ID_UNAVAILABLE"
            if ($Once) { exit 3 }
            Start-Sleep -Seconds 5; continue
        }
        $activeLayout = Get-BuildLayout -Manifest $manifest -Identity $activeIdentity
        if (($null -eq $activeLayout) -and ($activeIdentity.Type -eq "sha256") -and ($activeIdentity.PlatformHint -eq "battlenet")) {
            $activeLayout = Get-VerifiedDiscoveredBnetLayout -Identity $activeIdentity
            if ($null -ne $activeLayout) { Write-Log "KNOWN_LAYOUT_PROBE $VerifiedLayout PASS source=session-cache" }
        }
        if (($null -eq $activeLayout) -and ($activeIdentity.Type -eq "sha256") -and ($activeIdentity.PlatformHint -eq "battlenet")) {
            if ($InitialDelaySeconds -gt 0) {
                Start-Sleep -Seconds $InitialDelaySeconds; $initialDelayApplied = $true
                $check = Get-CodState
                if (($check.State -ne "FOUND") -or ($check.Candidate.Process.Id -ne $trackedPid)) { Write-Log "COD_CHANGED_DURING_INITIAL_DELAY"; $trackedPid = $null; $activeLayout = $null; $activeIdentity = $null; continue }
                $cod = $check.Candidate.Process; $codPath = $check.Candidate.Path
            }
            Write-Log "UNKNOWN_BUILD $($activeIdentity.Value)"
            $activeLayout = Resolve-UnknownBnetLayout -Cod $cod -CodPath $codPath -Identity $activeIdentity -Manifest $manifest
        }
        $platform = if ($null -eq $activeLayout) { if ($activeIdentity.PlatformHint) { $activeIdentity.PlatformHint } else { "unknown" } } else { $activeLayout.platform }
        $layoutName = if ($null -eq $activeLayout) { "unknown" } else { $activeLayout.layout }
        $identityLog = if ($activeIdentity.Type -eq "sha256") { "codSha256=$($activeIdentity.Value)" } else { "runtimeBuildId=$($activeIdentity.Value)" }
        Write-Log ('COD_DETECTED pid={0} codPath="{1}" {2} platform={3} layout={4} osVersion="{5}" powerShellVersion={6} initialDelaySeconds={7}' -f $cod.Id, $codPath, $identityLog, $platform, $layoutName, [Environment]::OSVersion.VersionString, $PSVersionTable.PSVersion, $InitialDelaySeconds)
        if ($null -eq $activeLayout) {
            Write-Log ("UNSUPPORTED_BUILD codPath=`"{0}`" {1}" -f $codPath, $identityLog)
            Write-IdentityStatus -CodPid $cod.Id -CodPath $codPath -Identity $activeIdentity -CycleStatus "UNSUPPORTED_BUILD"
            if ($Once) { exit 2 }
            Start-Sleep -Seconds 5; continue
        }
        if ((-not $initialDelayApplied) -and ($InitialDelaySeconds -gt 0)) {
            Start-Sleep -Seconds $InitialDelaySeconds
            $check = Get-CodState
            if (($check.State -ne "FOUND") -or ($check.Candidate.Process.Id -ne $trackedPid)) { Write-Log "COD_CHANGED_DURING_INITIAL_DELAY"; $trackedPid = $null; $activeLayout = $null; $activeIdentity = $null; continue }
            $cod = $check.Candidate.Process; $codPath = $check.Candidate.Path
        }
    }
    if ($null -eq $activeLayout) { if ($Once) { exit 2 }; Start-Sleep -Seconds 5; continue }
    $datasets = Get-Datasets -Layout $activeLayout
    Invoke-CollectionCycle -Cod $cod -CodPath $codPath -Identity $activeIdentity -Layout $activeLayout -Datasets $datasets
    if ($Once) { exit 0 }
    Start-Sleep -Seconds $IntervalSeconds
}
