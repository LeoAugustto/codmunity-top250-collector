[CmdletBinding()]
param(
    [string]$Version,
    [switch]$NoInvoke
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:ProjectRoot = Split-Path -Parent $PSScriptRoot
$script:Top250Root = Split-Path -Parent (Split-Path -Parent $script:ProjectRoot)
$script:ReleaseRoot = Join-Path $script:Top250Root 'Releases'
$script:Repository = 'LeoAugustto/codmunity-top250-collector'
$script:RemoteUrl = 'https://github.com/LeoAugustto/codmunity-top250-collector.git'
$script:PublicKeyRelativePath = 'CODMunityTop250Collector.App/Updater/codmunity-top250-release-public.pem'
$script:AlphaVersionPattern = '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)-alpha$'

function Assert-AlphaVersion {
    param([Parameter(Mandatory)][string]$Value)
    if ($Value -notmatch $script:AlphaVersionPattern) {
        throw 'Version must be in X.Y.Z-alpha format.'
    }
}

function Resolve-GhExecutable {
    $command = Get-Command gh -ErrorAction SilentlyContinue
    if ($null -ne $command) { return $command.Source }

    $fallback = 'C:\Program Files\GitHub CLI\gh.exe'
    if (Test-Path -LiteralPath $fallback) { return $fallback }
    throw 'GitHub CLI (gh) is required. Install it and run gh auth login before publishing.'
}

function Invoke-Git {
    param([Parameter(Mandatory)][string[]]$Arguments)
    & git @Arguments
    if ($LASTEXITCODE -ne 0) { throw "git command failed: git $($Arguments -join ' ')" }
}

function Assert-PrivateRepository {
    param([Parameter(Mandatory)][string]$GhPath)

    $login = (& $GhPath api user --jq '.login').Trim()
    if ($LASTEXITCODE -ne 0 -or $login -ne 'LeoAugustto') {
        throw 'GitHub CLI must be authenticated as LeoAugustto.'
    }

    $repository = & $GhPath repo view $script:Repository --json nameWithOwner,visibility,url | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0 -or $repository.nameWithOwner -ne $script:Repository -or $repository.visibility -ne 'PRIVATE') {
        throw 'GitHub repository validation failed: the expected repository must be PRIVATE.'
    }
}

function Assert-Origin {
    $origin = (& git remote get-url origin).Trim()
    if ($LASTEXITCODE -ne 0 -or $origin -ne $script:RemoteUrl) {
        throw "origin must be exactly $script:RemoteUrl"
    }
}

function Assert-NoSensitiveStagedContent {
    $staged = @(& git diff --cached --name-only)
    if ($LASTEXITCODE -ne 0) { throw 'Unable to inspect staged files.' }

    $forbidden = @(
        '(^|/)dist/', '(^|/)bin/', '(^|/)obj/', '(^|/)\.vs/',
        '(^|/)\.env($|\.)', '\.(key|pfx|p12)$', '\.pem$',
        '(^|/)(logs?|releases?|staging|updates?)/', '\.log$'
    )
    foreach ($path in $staged) {
        if ($path -eq $script:PublicKeyRelativePath) { continue }
        foreach ($pattern in $forbidden) {
            if ($path -match $pattern) { throw "Sensitive or generated file staged: $path" }
        }
    }

    $secretPatterns = 'github_pat_[A-Za-z0-9_]{20,}|ghp_[A-Za-z0-9]{20,}|gho_[A-Za-z0-9]{20,}|BEGIN (ENCRYPTED )?PRIVATE KEY|Authorization:[[:space:]]*Bearer[[:space:]]+[A-Za-z0-9._-]{12,}'
    $matches = @(& git grep --cached -I -E $secretPatterns 2>$null)
    if ($LASTEXITCODE -eq 0 -and $matches.Count -gt 0) {
        throw 'A secret-like literal was found in staged text. Remove it before publishing.'
    }
    if ($LASTEXITCODE -gt 1) { throw 'Unable to complete staged secret audit.' }
}

function Get-ReleaseNotes {
    param([Parameter(Mandatory)][string]$TargetVersion)

    $changelog = Join-Path $script:ProjectRoot 'CHANGELOG.md'
    if (-not (Test-Path -LiteralPath $changelog)) { throw 'CHANGELOG.md is required.' }
    $lines = Get-Content -LiteralPath $changelog
    $header = "## $TargetVersion"
    $start = [Array]::IndexOf($lines, $header)
    if ($start -lt 0) { throw "CHANGELOG.md has no entry for $TargetVersion." }
    $end = $lines.Count
    for ($index = $start + 1; $index -lt $lines.Count; $index++) {
        if ($lines[$index] -like '## *') { $end = $index; break }
    }
    return ($lines[$start..($end - 1)] -join [Environment]::NewLine).Trim() + [Environment]::NewLine
}

function Get-ReleaseContext {
    param([Parameter(Mandatory)][string]$TargetVersion)

    Assert-AlphaVersion -Value $TargetVersion
    $msiName = "CODMunityTop250Collector-$TargetVersion-win-x64.msi"
    $setupName = "CODMunityTop250Collector-$TargetVersion-Setup.exe"
    $releaseDirectory = Join-Path $script:ReleaseRoot $TargetVersion
    $portableZipName = "CODMunityTop250Collector-$TargetVersion-portable.zip"
    [pscustomobject]@{
        Tag = "v$TargetVersion"
        Version = $TargetVersion
        Msi = Join-Path $script:ProjectRoot (Join-Path 'dist\msi' $msiName)
        Setup = Join-Path $script:ProjectRoot (Join-Path 'dist\setup' $setupName)
        PortableDirectory = Join-Path $script:ProjectRoot 'dist\portable'
        ReleaseDirectory = $releaseDirectory
        Manifest = Join-Path $releaseDirectory 'manifest.json'
        Signature = Join-Path $releaseDirectory 'manifest.sig'
        PortableZip = Join-Path $releaseDirectory $portableZipName
        Sha256Sums = Join-Path $releaseDirectory 'SHA256SUMS.txt'
    }
}

function New-PortableZip {
    param([Parameter(Mandatory)]$Context)
    if (Test-Path -LiteralPath $Context.PortableZip) { throw "Portable ZIP already exists: $($Context.PortableZip)" }
    New-Item -ItemType Directory -Path $Context.ReleaseDirectory -Force | Out-Null
    Compress-Archive -LiteralPath (Get-ChildItem -LiteralPath $Context.PortableDirectory -Force | ForEach-Object FullName) -DestinationPath $Context.PortableZip -CompressionLevel Optimal
}

function New-Sha256Sums {
    param([Parameter(Mandatory)]$Context)
    $files = @($Context.Setup, $Context.Msi, $Context.PortableZip, $Context.Manifest, $Context.Signature)
    $lines = foreach ($file in $files) {
        "$(Split-Path -Leaf $file)  $((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash)"
    }
    [System.IO.File]::WriteAllText($Context.Sha256Sums, (($lines -join "`n") + "`n"), [System.Text.UTF8Encoding]::new($false))
}

function Publish-GitHubReleaseMirror {
    param([Parameter(Mandatory)][string]$TargetVersion)

    $gh = Resolve-GhExecutable
    Assert-PrivateRepository -GhPath $gh
    Set-Location -LiteralPath $script:ProjectRoot
    Assert-Origin
    $context = Get-ReleaseContext -TargetVersion $TargetVersion
    foreach ($file in @($context.Msi, $context.Setup, $context.PortableDirectory, $context.Manifest, $context.Signature)) {
        if (-not (Test-Path -LiteralPath $file)) { throw "Required release input is missing: $file" }
    }

    $notes = Get-ReleaseNotes -TargetVersion $TargetVersion
    $pending = @(& git status --porcelain)
    if ($LASTEXITCODE -ne 0) { throw 'Unable to inspect working tree.' }
    if ($pending.Count -gt 0) {
        Invoke-Git -Arguments @('add', '--all')
        Assert-NoSensitiveStagedContent
        Invoke-Git -Arguments @('commit', '-m', "Release mirror source backup - $TargetVersion")
    }

    Invoke-Git -Arguments @('push', 'origin', 'main')
    $remoteTag = & git ls-remote --tags origin "refs/tags/$($context.Tag)"
    if ($LASTEXITCODE -ne 0) { throw 'Unable to inspect remote tags.' }
    if ([string]::IsNullOrWhiteSpace(($remoteTag -join ''))) {
        $localTag = @(& git tag --list $context.Tag)
        if ($localTag.Count -eq 0) {
            Invoke-Git -Arguments @('tag', '-a', $context.Tag, '-m', "CODMunity Top 250 Collector $TargetVersion")
        }
        Invoke-Git -Arguments @('push', 'origin', $context.Tag)
    }

    # Revalidate private visibility immediately before creating or uploading release assets.
    Assert-PrivateRepository -GhPath $gh
    if (-not (Test-Path -LiteralPath $context.PortableZip)) { New-PortableZip -Context $context }
    New-Sha256Sums -Context $context
    $assets = @($context.Setup, $context.Msi, $context.PortableZip, $context.Manifest, $context.Signature, $context.Sha256Sums)

    # `gh release view` returns a non-zero exit code for an expected first-release
    # absence. List tags instead so the idempotency probe itself cannot abort.
    $releaseTags = @((& $gh release list --repo $script:Repository --limit 100 --json tagName | ConvertFrom-Json) | ForEach-Object tagName)
    if ($LASTEXITCODE -ne 0) { throw 'Unable to inspect existing GitHub Releases.' }
    $releaseExists = $releaseTags -contains $context.Tag
    if (-not $releaseExists) {
        $notesFile = Join-Path ([System.IO.Path]::GetTempPath()) ("codmunity-release-notes-$([guid]::NewGuid().ToString('N')).md")
        try {
            [System.IO.File]::WriteAllText($notesFile, $notes, [System.Text.UTF8Encoding]::new($false))
            & $gh release create $context.Tag --repo $script:Repository --title "CODMunity Top 250 Collector $TargetVersion" --notes-file $notesFile --verify-tag
            if ($LASTEXITCODE -ne 0) { throw 'GitHub Release creation failed.' }
        }
        finally {
            Remove-Item -LiteralPath $notesFile -Force -ErrorAction SilentlyContinue
        }
    }

    $existingAssets = @((& $gh release view $context.Tag --repo $script:Repository --json assets | ConvertFrom-Json).assets | ForEach-Object name)
    if ($LASTEXITCODE -ne 0) { throw 'Unable to inspect GitHub Release assets.' }
    $missingAssets = @($assets | Where-Object { $existingAssets -notcontains (Split-Path -Leaf $_) })
    if ($missingAssets.Count -gt 0) {
        & $gh release upload $context.Tag @missingAssets --repo $script:Repository
        if ($LASTEXITCODE -ne 0) { throw 'GitHub Release asset upload failed. Existing assets were not deleted or rolled back.' }
    }

    $finalAssets = @((& $gh release view $context.Tag --repo $script:Repository --json assets | ConvertFrom-Json).assets | ForEach-Object name)
    foreach ($asset in $assets) {
        if ($finalAssets -notcontains (Split-Path -Leaf $asset)) { throw "GitHub Release is missing expected asset: $(Split-Path -Leaf $asset)" }
    }
    Write-Output "GITHUB_RELEASE_MIRROR_OK version=$TargetVersion"
}

if (-not $NoInvoke) {
    if ([string]::IsNullOrWhiteSpace($Version)) {
        throw 'Version is required.'
    }
    Publish-GitHubReleaseMirror -TargetVersion $Version
}
