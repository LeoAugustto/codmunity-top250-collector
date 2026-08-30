# CODMunity Top 250 Collector

**PRIVATE INTERNAL / CLOSED BETA** Windows collector for CODMunity Top 250 leaderboards.

The application reads the supported local game layouts, keeps the active leaderboard data in memory, and synchronizes validated snapshots to CODMunity. The private updater consumes signed releases from OCI Object Storage. GitHub is a private source-control, backup, and release-mirror location; it is not the updater source.

## Requirements

- Windows 10/11 x64
- .NET 8 SDK for development; Microsoft .NET 8 Desktop Runtime x64 for framework-dependent builds
- Windows PowerShell 5.1 for the bundled engine
- WiX Toolset dependencies restored through the solution for MSI/Bundle builds
- Git and GitHub CLI (`gh`) for source backup and GitHub release mirroring

Secrets and credentials are deliberately excluded from this repository. Never commit API tokens, OCI credentials, signing keys, `.env` files, or logs.

## Structure

- `CODMunityTop250Collector.App` — WPF tray application.
- `CODMunityTop250Collector.Core` — scheduling, collection orchestration, API sync, settings, and logs.
- `CODMunityTop250Collector.Updater` / `CODMunityTop250Collector.UpdateHost` — signed update checks and local MSI handoff.
- `engine` — versioned PowerShell orchestrator, build layouts, and exporters used by the app.
- `Installer.Wix` / `Installer.Bundle` — internal per-user MSI update package and the branded user-facing Burn setup.
- `tools` — offline signing/release helpers and build helpers.

## Build

```powershell
dotnet build .\CODMunityTop250Collector.sln -c Release
dotnet test .\CODMunityTop250Collector.Tests\CODMunityTop250Collector.Tests.csproj -c Release
dotnet publish .\CODMunityTop250Collector.App\CODMunityTop250Collector.App.csproj -c Release -p:PublishProfile=PortableFrameworkDependent
dotnet build .\Installer.Wix\CODMunityTop250Collector.Installer.wixproj -c Release
dotnet build .\Installer.Bundle\CODMunityTop250Collector.Bundle.wixproj -c Release
```

The generated `dist/` directory is intentionally ignored. It is mirrored only as private GitHub Release assets after a validated release.

## Closed Beta build

For an internal build with the embedded Closed Beta API credential, run:

```powershell
.\tools\build-closed-beta.ps1
```

The helper accepts an already-provided `CODMUNITY_RANKED_SYNC_TOKEN_BUILD` environment credential, otherwise it prompts securely. It never prints or stores the token. Runtime environment credentials remain higher priority than the embedded Closed Beta credential.

## Installation

For manual installation or upgrade, use the versioned Setup executable:

```text
CODMunityTop250Collector-<version>-Setup.exe
```

The MSI is an internal package for the signed automatic-update flow (`manifest` → MSI → UpdateHost → silent Windows Installer). Do not use the MSI as the recommended manual installer.

### Same-version development validation

Windows Installer can preserve files when reinstalling a rebuilt package with the same product/file versions. For a local validation rebuild, do not install 0.1.24-alpha over an already installed 0.1.24-alpha in place.

After generating the portable, MSI, and Setup artifacts, close the Collector and run:

    .\tools\validate-local-install.ps1 -Version '0.1.24-alpha' -ReplaceAndValidate

The helper performs a clean registered Setup uninstall, opens the normal interactive Setup install, then SHA-256 compares every portable payload file with its installed counterpart. It fails if any installed file is stale, missing, or differs. This workflow is for same-version development validation only; normal releases and the signed MSI updater flow are unchanged.


### Battle.net layout discovery

Known executable SHA-256 values always use their versioned official layout. For an unknown Battle.net build, the discovery path accepts only one deterministic set of nine independently matched static anchors (state, count, and entry start for MP, BR, and Resurgence), then performs two complete live extraction validations before caching technical offsets at `%LOCALAPPDATA%\CODMunity\Top250Collector\layouts\discovered\<SHA256>.json`. Cache entries are revalidated on every use and do not store player data. If no unique validated candidate exists, collection fails closed and the Watcher pauses COD recovery for that explicit layout-support failure.

The 0.1.24 development catalog intentionally contains no unverified signature method; it will refuse an unknown build until reproducible signature evidence is added.

See [Battle.net layout evidence workflow](docs/BATTLE-NET-LAYOUT-EVIDENCE.md) for the static baseline capture and next-update procedure.
## Release flow

1. Build and validate locally.
2. Publish the signed updater release to OCI with `tools/release-update.ps1`. This is the official updater source.
3. Mirror the validated source, tag, and release assets to this private GitHub repository:

```powershell
.\tools\publish-github-release.ps1 -Version "0.1.24-alpha"
```

The GitHub helper requires `LeoAugustto/codmunity-top250-collector` to be `PRIVATE`, uploads the MSI, Setup, portable ZIP, `manifest.json`, `manifest.sig`, and `SHA256SUMS.txt`, and always treats `manifest.json` as an existing OCI announcement artifact rather than an updater publication action.

## Recovery on another PC

```powershell
git clone https://github.com/LeoAugustto/codmunity-top250-collector.git
cd codmunity-top250-collector
dotnet restore .\CODMunityTop250Collector.sln
dotnet build .\CODMunityTop250Collector.sln -c Release
```

Configure required local secrets separately. The repository intentionally does not contain API tokens, OCI credentials, private release keys, generated artifacts, or logs.
