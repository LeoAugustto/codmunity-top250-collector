[CmdletBinding()]
param(
    [string]$Version,
    [string]$Branch,
    [ValidateSet('Full', 'Prepare', 'Mirror')]
    [string]$Mode = 'Full',
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

function Assert-GitHubAlphaVersion {
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

function Get-CurrentBranch {
    $branch = (& git branch --show-current).Trim()
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($branch)) { throw 'A named Git branch is required for release publishing.' }
    return $branch
}

function Assert-ReleaseBranch {
    param([string]$ExpectedBranch)
    $current = Get-CurrentBranch
    if (-not [string]::IsNullOrWhiteSpace($ExpectedBranch) -and $current -ne $ExpectedBranch) { throw "Current branch '$current' does not match requested release branch '$ExpectedBranch'." }
    return $current
}

function Assert-NoUncommittedTrackedChanges {
    & git diff --quiet
    if ($LASTEXITCODE -ne 0) { throw 'Tracked unstaged changes must be committed before publishing a release.' }
    & git diff --cached --quiet
    if ($LASTEXITCODE -ne 0) { throw 'Staged changes must be committed before publishing a release.' }
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
    $matches = @(& git grep --cached -I -n -E $secretPatterns 2>$null | Where-Object { $_ -notmatch 'RELEASE_SECRET_AUDIT_SYNTHETIC_FIXTURE$' })
    if ($LASTEXITCODE -eq 0 -and $matches.Count -gt 0) {
        throw 'A secret-like literal was found in staged text. Remove it before publishing.'
    }
    if ($LASTEXITCODE -gt 1) { throw 'Unable to complete staged secret audit.' }
}

function Get-RemoteTagCommit {
    param([Parameter(Mandatory)][string]$Tag)
    $lines = @(& git ls-remote --tags origin "refs/tags/$Tag" "refs/tags/$Tag^{}")
    if ($LASTEXITCODE -ne 0) { throw 'Unable to inspect remote tags.' }
    $peeled = @($lines | Where-Object { $_ -match ("\srefs/tags/" + [regex]::Escape($Tag) + "\^\{\}$") })
    $selected = if ($peeled.Count -gt 0) { $peeled[0] } elseif ($lines.Count -gt 0) { $lines[0] } else { $null }
    if ($null -eq $selected) { return $null }
    return ($selected -split '\s+')[0]
}

function Ensure-ReleaseSourceAndTag {
    param([Parameter(Mandatory)][string]$TargetVersion, [string]$ExpectedBranch)
    Set-Location -LiteralPath $script:ProjectRoot
    Assert-Origin
    $branch = Assert-ReleaseBranch -ExpectedBranch $ExpectedBranch
    Assert-NoUncommittedTrackedChanges
    $head = (& git rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0) { throw 'Unable to resolve the release commit.' }
    Invoke-Git -Arguments @('push', 'origin', $branch)
    $tag = "v$TargetVersion"
    $remoteCommit = Get-RemoteTagCommit -Tag $tag
    if ($null -ne $remoteCommit -and $remoteCommit -ne $head) { throw "Remote tag $tag points to a different commit and will not be replaced." }
    if ($null -eq $remoteCommit) {
        $localOutput = @(& git rev-list -n 1 $tag 2>$null)
        $localCommit = if ($localOutput.Count -gt 0) { [string]$localOutput[0] } else { $null }
        if ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace($localCommit) -and $localCommit -ne $head) { throw "Local tag $tag points to a different commit and will not be replaced." }
        if ([string]::IsNullOrWhiteSpace($localCommit)) { Invoke-Git -Arguments @('tag', '-a', $tag, '-m', "CODMunity Top 250 Collector $TargetVersion", $head) }
        Invoke-Git -Arguments @('push', 'origin', "refs/tags/$tag")
    }
    return [pscustomobject]@{ Branch = $branch; Commit = $head; Tag = $tag }
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

    Assert-GitHubAlphaVersion -Value $TargetVersion
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

function Test-GitHubReleaseExists {
    [OutputType([bool])]
    param(
        [Parameter(Mandatory)][string]$GhPath,
        [Parameter(Mandatory)][string]$Tag
    )

    # A missing release is expected for a new tag.  Keep native stderr out of
    # the error pipeline while probing, then accept only GitHub CLI's explicit
    # not-found result; every other failure remains fatal.
    $stderrPath = [System.IO.Path]::GetTempFileName()
    $exitCode = $null
    try {
        $previousErrorActionPreference = $ErrorActionPreference
        try {
            $ErrorActionPreference = 'Continue'
            & $GhPath release view $Tag --repo $script:Repository 2>$stderrPath | Out-Null
            $exitCode = $LASTEXITCODE
        }
        finally {
            $ErrorActionPreference = $previousErrorActionPreference
        }

        if ($exitCode -eq 0) { return $true }

        $stderr = [System.IO.File]::ReadAllText($stderrPath)
        if ($exitCode -eq 1 -and $stderr -match '(?i)release not found') { return $false }
        throw "Unable to inspect GitHub Release '$Tag' (gh exit code $exitCode)."
    }
    finally {
        Remove-Item -LiteralPath $stderrPath -Force -ErrorAction SilentlyContinue
    }
}

function New-PortableZip {
    param([Parameter(Mandatory)]$Context)
    if (Test-Path -LiteralPath $Context.PortableZip) { return }
    New-Item -ItemType Directory -Path $Context.ReleaseDirectory -Force | Out-Null
    Compress-Archive -LiteralPath (Get-ChildItem -LiteralPath $Context.PortableDirectory -Force | ForEach-Object FullName) -DestinationPath $Context.PortableZip -CompressionLevel Optimal
}

function New-Sha256Sums {
    param([Parameter(Mandatory)]$Context)
    $files = @($Context.Setup, $Context.Msi, $Context.PortableZip, $Context.Manifest, $Context.Signature)
    $lines = foreach ($file in $files) {
        "$(Split-Path -Leaf $file)  $((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash)"
    }
    $content = (($lines -join "`n") + "`n")
    if (Test-Path -LiteralPath $Context.Sha256Sums) {
        $existing = [System.IO.File]::ReadAllText($Context.Sha256Sums, [System.Text.UTF8Encoding]::new($false))
        if (-not [string]::Equals($existing, $content, [System.StringComparison]::Ordinal)) { throw 'Existing SHA256SUMS.txt does not match the release inputs and will not be replaced.' }
        return
    }
    [System.IO.File]::WriteAllText($Context.Sha256Sums, $content, [System.Text.UTF8Encoding]::new($false))
}

function Get-GitHubReleaseAssets {
    param([Parameter(Mandatory)][string]$GhPath, [Parameter(Mandatory)][string]$Tag)
    $release = & $GhPath api "repos/$script:Repository/releases/tags/$Tag" 2>$null | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0) { throw 'Unable to inspect GitHub Release assets.' }
    return @($release.assets)
}

function Assert-GitHubAssetHashes {
    param([Parameter(Mandatory)][string]$GhPath, [Parameter(Mandatory)][string]$Tag, [Parameter(Mandatory)][string[]]$Assets)
    $remoteAssets = Get-GitHubReleaseAssets -GhPath $GhPath -Tag $Tag
    foreach ($asset in $Assets) {
        $name = Split-Path -Leaf $asset
        $remote = @($remoteAssets | Where-Object { $_.name -eq $name })
        if ($remote.Count -ne 1) { throw "GitHub Release is missing expected asset: $name" }
        $digest = [string]$remote[0].digest
        if (-not [string]::IsNullOrWhiteSpace($digest)) {
            $expected = 'sha256:' + (Get-FileHash -LiteralPath $asset -Algorithm SHA256).Hash.ToLowerInvariant()
            if (-not [string]::Equals($digest, $expected, [System.StringComparison]::OrdinalIgnoreCase)) { throw "GitHub asset digest mismatch: $name" }
        }
    }
}

function Publish-GitHubReleaseMirror {
    param(
        [Parameter(Mandatory)][string]$TargetVersion,
        [string]$ExpectedBranch,
        [switch]$SkipPrepare
    )

    $gh = Resolve-GhExecutable
    Assert-PrivateRepository -GhPath $gh
    Set-Location -LiteralPath $script:ProjectRoot
    Assert-Origin
    if (-not $SkipPrepare) { [void](Ensure-ReleaseSourceAndTag -TargetVersion $TargetVersion -ExpectedBranch $ExpectedBranch) }
    $context = Get-ReleaseContext -TargetVersion $TargetVersion
    foreach ($file in @($context.Msi, $context.Setup, $context.PortableDirectory, $context.Manifest, $context.Signature)) {
        if (-not (Test-Path -LiteralPath $file)) { throw "Required release input is missing: $file" }
    }

    $notes = Get-ReleaseNotes -TargetVersion $TargetVersion

    # Revalidate private visibility immediately before creating or uploading release assets.
    Assert-PrivateRepository -GhPath $gh
    if (-not (Test-Path -LiteralPath $context.PortableZip)) { New-PortableZip -Context $context }
    New-Sha256Sums -Context $context
    $assets = @($context.Setup, $context.Msi, $context.PortableZip, $context.Manifest, $context.Signature, $context.Sha256Sums)

    $releaseExists = Test-GitHubReleaseExists -GhPath $gh -Tag $context.Tag
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

    $existingAssets = Get-GitHubReleaseAssets -GhPath $gh -Tag $context.Tag
    $existingNames = @($existingAssets | ForEach-Object name)
    $missingAssets = @($assets | Where-Object { $existingNames -notcontains (Split-Path -Leaf $_) })
    if ($missingAssets.Count -gt 0) {
        & $gh release upload $context.Tag @missingAssets --repo $script:Repository
        if ($LASTEXITCODE -ne 0) { throw 'GitHub Release asset upload failed. Existing assets were not deleted or rolled back.' }
    }

    Assert-GitHubAssetHashes -GhPath $gh -Tag $context.Tag -Assets $assets
    Write-Output "GITHUB_RELEASE_MIRROR_OK version=$TargetVersion"
}

if (-not $NoInvoke) {
    if ([string]::IsNullOrWhiteSpace($Version)) {
        throw 'Version is required.'
    }
    switch ($Mode) {
        'Prepare' { [void](Ensure-ReleaseSourceAndTag -TargetVersion $Version -ExpectedBranch $Branch) }
        'Mirror' { Publish-GitHubReleaseMirror -TargetVersion $Version -ExpectedBranch $Branch -SkipPrepare }
        default { Publish-GitHubReleaseMirror -TargetVersion $Version -ExpectedBranch $Branch }
    }
}
