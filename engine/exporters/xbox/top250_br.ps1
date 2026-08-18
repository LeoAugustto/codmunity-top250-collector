param(
    [string]$OutputDir = (Join-Path $env:LOCALAPPDATA "CODMunity\Top250Collector\data\br"),
    [switch]$Quiet,
    [switch]$EmitJson
)

$ErrorActionPreference = "Stop"
if ($EmitJson) { $script:MachineReadableUtf8 = [System.Text.UTF8Encoding]::new($false); [Console]::OutputEncoding = $script:MachineReadableUtf8; $OutputEncoding = $script:MachineReadableUtf8 }

# Warzone Ranked Battle Royale / selector 1 (Xbox sparse-dump layout)
$ManagerRva = [Int64]0x12171C00
$CountRva   = [Int64]0x121AD34C
$Entry0Rva  = [Int64]0x121AD350

$RecordStride = 0x458
$MaxRecords   = 260

$EntityIdOffset   = 0x00
$NativeRank0Offset = 0x08
$SrOffset         = 0x0C
$NameOffset       = 0x10
$NameSize         = 0x40

if (-not ("Top250ExporterNative" -as [type])) {
    Add-Type @"
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

public static class Top250ExporterNative
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
            int err = Marshal.GetLastWin32Error();
            throw new Win32Exception(
                err,
                String.Format("ReadProcessMemory failed at 0x{0:X}", address)
            );
        }

        return buffer;
    }
}
"@
}

function Say([string]$Text, [ConsoleColor]$Color = [ConsoleColor]::Gray) {
    if (-not $Quiet) {
        Write-Host $Text -ForegroundColor $Color
    }
}

function Get-XboxCodProcess {
    $prefix = "C:\Program Files\WindowsApps\"
    $matches = @(Get-Process -Name cod -ErrorAction SilentlyContinue)
    if ($matches.Count -ne 1) { return $null }
    try {
        if ($matches[0].Path -and ([IO.Path]::GetFileName($matches[0].Path) -ieq "cod.exe") -and $matches[0].Path.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { return $matches[0] }
    } catch {}
    return $null
}

function Read-U8([IntPtr]$h, [Int64]$a) {
    [Top250ExporterNative]::ReadBytes($h,$a,1)[0]
}
function Read-U32([IntPtr]$h, [Int64]$a) {
    [BitConverter]::ToUInt32([Top250ExporterNative]::ReadBytes($h,$a,4),0)
}
function Read-I32([IntPtr]$h, [Int64]$a) {
    [BitConverter]::ToInt32([Top250ExporterNative]::ReadBytes($h,$a,4),0)
}
function Read-U64([IntPtr]$h, [Int64]$a) {
    [BitConverter]::ToUInt64([Top250ExporterNative]::ReadBytes($h,$a,8),0)
}
function Read-Name([IntPtr]$h, [Int64]$a) {
    $b = [Top250ExporterNative]::ReadBytes($h,$a,$NameSize)
    $len = 0
    while ($len -lt $b.Length -and $b[$len] -ne 0) { $len++ }
    if ($len -eq 0) { return "" }
    $s = [Text.Encoding]::UTF8.GetString($b,0,$len)
    $hash = $s.IndexOf("#")
    if ($hash -ge 0) { $s = $s.Substring(0,$hash) }
    return $s
}
function TsvClean([object]$v) {
    if ($null -eq $v) { return "" }
    ([string]$v) -replace "`r|`n|`t"," "
}
function Write-AtomicText([string]$Path,[string[]]$Lines) {
    $tmp = "$Path.tmp"
    $Lines | Set-Content -LiteralPath $tmp -Encoding UTF8
    Move-Item -LiteralPath $tmp -Destination $Path -Force
}

function Read-XboxRuntimeBuildId([IntPtr]$h, [Int64]$base) {
    $header = [Top250ExporterNative]::ReadBytes($h,$base,0x1000)
    if ([BitConverter]::ToUInt16($header,0) -ne 0x5A4D) { throw "INVALID_PE_DOS_SIGNATURE" }
    $pe = [int][BitConverter]::ToUInt32($header,0x3C)
    if (($pe -lt 0x40) -or ($pe + 24 -gt $header.Length) -or ([BitConverter]::ToUInt32($header,$pe) -ne 0x00004550)) { throw "INVALID_PE_SIGNATURE" }
    $machine = [BitConverter]::ToUInt16($header,$pe + 4)
    $sections = [BitConverter]::ToUInt16($header,$pe + 6)
    $timestamp = [BitConverter]::ToUInt32($header,$pe + 8)
    $optional = $pe + 24
    if ([BitConverter]::ToUInt16($header,$optional) -ne 0x20B) { throw "INVALID_PE_OPTIONAL_HEADER" }
    $entry = [BitConverter]::ToUInt32($header,$optional + 0x10)
    $size = [BitConverter]::ToUInt32($header,$optional + 0x38)
    $checksum = [BitConverter]::ToUInt32($header,$optional + 0x40)
    return ("pe64-{0:X8}-{1:X8}-{2:X8}-{3:X4}-{4}-{5:X8}" -f $timestamp,$size,$entry,$machine,$sections,$checksum)
}

$proc = Get-XboxCodProcess
if ($null -eq $proc) {
    Say "XBOX_COD_NOT_RUNNING_OR_AMBIGUOUS" Yellow
    exit 21
}

$moduleBase = [Int64]$proc.MainModule.BaseAddress.ToInt64()
$access = [Top250ExporterNative]::PROCESS_VM_READ -bor [Top250ExporterNative]::PROCESS_QUERY_LIMITED_INFORMATION
$handle = [Top250ExporterNative]::OpenProcess($access,$false,$proc.Id)

if ($handle -eq [IntPtr]::Zero) {
    Say "OPEN_PROCESS_FAILED" Red
    exit 22
}

try {
    $runtimeBuildId = Read-XboxRuntimeBuildId $handle $moduleBase
    $manager = $moduleBase + $ManagerRva
    $countAddr = $moduleBase + $CountRva
    $entry0 = $moduleBase + $Entry0Rva

    $state = Read-U8 $handle $manager
    $nativeCount = Read-U32 $handle $countAddr

    if ($state -ne 3) {
        Say "CACHE_NOT_READY state=$state" Yellow
        exit 23
    }

    if ($nativeCount -lt 1 -or $nativeCount -gt $MaxRecords) {
        Say "INVALID_COUNT count=$nativeCount" Red
        exit 24
    }

    $rows = New-Object System.Collections.Generic.List[object]
    $previousSr = $null
    $uiPosition = 1

    for ($i=0; $i -lt [int]$nativeCount; $i++) {
        $record = $entry0 + ([Int64]$i * $RecordStride)
        $xuid = Read-U64 $handle ($record + $EntityIdOffset)
        if ($xuid -eq 0) { break }

        $nativeRank0 = Read-U32 $handle ($record + $NativeRank0Offset)
        $sr = Read-I32 $handle ($record + $SrOffset)
        $name = Read-Name $handle ($record + $NameOffset)

        $ordinal = $i + 1
        if ($ordinal -eq 1) {
            $uiPosition = 1
        }
        elseif ($previousSr -ne $sr) {
            $uiPosition = $ordinal
        }

        if ($uiPosition -gt 250) { break }

        $rows.Add([pscustomobject]@{
            Position = [int]$uiPosition
            Name = $name
            SR = [int]$sr
            XUID = [string]$xuid
            NativeRank0 = [int]$nativeRank0
            Ordinal = [int]$ordinal
        })

        $previousSr = $sr
    }

    if ($rows.Count -lt 1 -or $rows.Count -gt 260) {
        Say "INVALID_EXPORTED_COUNT count=$($rows.Count)" Red
        exit 25
    }

    # Production validation.
    $seen = @{}
    $lastSr = [int]::MaxValue
    $expectedOrdinal = 1

    foreach ($r in $rows) {
        if ([string]::IsNullOrWhiteSpace($r.Name)) {
            Say "VALIDATION_FAILED empty name at ordinal $($r.Ordinal)" Red
            exit 26
        }
        if ($seen.ContainsKey($r.XUID)) {
            Say "VALIDATION_FAILED duplicate XUID $($r.XUID)" Red
            exit 27
        }
        $seen[$r.XUID] = $true

        if ($r.SR -gt $lastSr) {
            Say "VALIDATION_FAILED SR order at ordinal $($r.Ordinal)" Red
            exit 28
        }
        $lastSr = $r.SR

        if ($r.Ordinal -ne $expectedOrdinal) {
            Say "VALIDATION_FAILED ordinal sequence" Red
            exit 29
        }
        $expectedOrdinal++
    }

    if (-not $EmitJson) {
        New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
        $archiveDir = Join-Path $OutputDir "archive"
        New-Item -ItemType Directory -Force -Path $archiveDir | Out-Null
    }

    $capturedUtc = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ")
    if (-not $EmitJson) {
    $stamp = Get-Date -Format "yyyyMMdd_HHmmss"
    # Clean site TSV: only the fields CODMunity should need.
    $cleanLines = New-Object System.Collections.Generic.List[string]
    $cleanLines.Add("Position`tName`tSR`tXUID")
    foreach ($r in $rows) {
        $cleanLines.Add(
            (@($r.Position,(TsvClean $r.Name),$r.SR,$r.XUID) -join "`t")
        )
    }

    # Audit TSV retains native/physical fields.
    $auditLines = New-Object System.Collections.Generic.List[string]
    $auditLines.Add("Position`tName`tSR`tXUID`tNativeRank0`tOrdinal")
    foreach ($r in $rows) {
        $auditLines.Add(
            (@($r.Position,(TsvClean $r.Name),$r.SR,$r.XUID,$r.NativeRank0,$r.Ordinal) -join "`t")
        )
    }
    }

    $payload = [ordered]@{
        mode = "br"
        capturedAt = $capturedUtc
        buildSha256 = $null
        runtimeBuildId = $runtimeBuildId
        nativeCount = [int]$nativeCount
        rowCount = [int]$rows.Count
        players = @(
            foreach ($r in $rows) {
                [ordered]@{
                    position = [int]$r.Position
                    name = [string]$r.Name
                    sr = [int]$r.SR
                    xuid = [string]$r.XUID
                }
            }
        )
    }

    $json = $payload | ConvertTo-Json -Depth 5
    if ($EmitJson) { [Console]::Out.Write($json); exit 0 }

    $latestTsv = Join-Path $OutputDir "latest.tsv"
    $latestJson = Join-Path $OutputDir "latest.json"
    $latestAudit = Join-Path $OutputDir "latest_audit.tsv"

    Write-AtomicText $latestTsv ([string[]]$cleanLines.ToArray())
    Write-AtomicText $latestAudit ([string[]]$auditLines.ToArray())
    Write-AtomicText $latestJson @($json)

    Write-AtomicText (Join-Path $archiveDir "br_top250_$stamp.tsv") ([string[]]$cleanLines.ToArray())
    Write-AtomicText (Join-Path $archiveDir "br_top250_$stamp.audit.tsv") ([string[]]$auditLines.ToArray())
    Write-AtomicText (Join-Path $archiveDir "br_top250_$stamp.json") @($json)

    Say "OK rows=$($rows.Count) nativeCount=$nativeCount capturedAt=$capturedUtc" Green
    Say "latest.tsv:       $latestTsv" Cyan
    Say "latest.json:      $latestJson" Cyan
    Say "latest_audit.tsv: $latestAudit" DarkCyan

    exit 0
}
catch {
    Say ("READ_FAILED: " + $_.Exception.Message) Red
    exit 30
}
finally {
    if ($handle -ne [IntPtr]::Zero) {
        [void][Top250ExporterNative]::CloseHandle($handle)
    }
}
