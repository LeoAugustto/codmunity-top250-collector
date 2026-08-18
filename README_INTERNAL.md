# Historical 0.1.0-alpha notes

> This document is retained only as historical context. It does not describe the current application. Use [README.md](README.md) for current build, recovery, Closed Beta, OCI, and private GitHub mirror instructions.

## Architecture

- `CODMunityTop250Collector.App`: WPF tray host and compact status window.
- `CODMunityTop250Collector.Core`: scheduling, local settings, log rotation, archive retention, engine-status parsing, and PowerShell process host.
- `CODMunityTop250Collector.Tests`: dependency-free local unit-test runner.
- `engine`: copied at build/publish time from `../../Current/Top250Collector_Universal_Test`; it contains only the proven orchestrator, build manifest, and exporters.

The app invokes Windows PowerShell 5.1 once per cycle with `-NoProfile -NonInteractive -ExecutionPolicy Bypass -Once -InitialDelaySeconds 0 -Quiet`. It never calls `Set-ExecutionPolicy`. The engine remains responsible for platform/build detection and read-only collection.

## Local paths and retention

- Settings: `%LOCALAPPDATA%\CODMunity\Top250Collector\settings.json`
- Data: `%LOCALAPPDATA%\CODMunity\Top250Collector\data`
- App log: `%LOCALAPPDATA%\CODMunity\Top250Collector\logs\app.log`
- Engine status: `%LOCALAPPDATA%\CODMunity\Top250Collector\orchestrator_status.json`

Settings default to collector enabled, Start with Windows disabled, and seven-day archive retention. Corrupt settings are backed up and replaced with defaults. Only known `data/<mode>/archive` roots are cleaned; latest files are never removed.

## Tray and startup

The app starts in the tray, is single-instance, and closing the status window hides it. Tray **Exit** terminates it. Start with Windows is opt-in through `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`; it does not require administrator privileges and never starts COD.

## Standard build and portable distribution

The standard distribution is framework-dependent for `win-x64`. It requires **Microsoft .NET 8 Desktop Runtime x64** on the target PC. The runtime is not downloaded or installed by the collector.

```powershell
dotnet test .\CODMunityTop250Collector.Tests\CODMunityTop250Collector.Tests.csproj
dotnet publish .\CODMunityTop250Collector.App\CODMunityTop250Collector.App.csproj -c Release -p:PublishProfile=PortableFrameworkDependent -p:DebugType=None -p:DebugSymbols=false
```

This creates `dist\portable\CODMunityTop250Collector.exe` plus the required WPF assemblies and `engine\` folder. The official logo source is `../../Brand/codmunity-logo.webp`; this project uses derived PNG/ICO copies without redesigning the logo. Replace those generated assets if the official source changes.

For diagnostic or emergency use only, a self-contained build can still be made explicitly:

```powershell
dotnet publish .\CODMunityTop250Collector.App\CODMunityTop250Collector.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:PublishTrimmed=false -o .\dist\self-contained-diagnostic
```

## Per-user installer

The official installer is WiX Toolset 7 MSI source in `Installer.Wix`. Its interactive wizard lets the user choose the install directory, optionally create a Desktop shortcut (checked by default), and launch the collector after a first interactive installation (also checked by default). The Start Menu shortcut is always created. It installs per user at `%LOCALAPPDATA%\Programs\CODMunity Top 250 Collector` by default, uses the fixed UpgradeCode `{6E04B728-7C32-48E9-9174-EEDB73E098C9}`, blocks downgrades through `MajorUpgrade`, and never enables Start with Windows. Silent, passive, maintenance, uninstall, and upgrade flows do not launch the app. The MSI ProductVersion is numeric `0.1.0`; the app continues to display `0.1.0-alpha` / Private Alpha.

The MSI requires Microsoft .NET 8 Desktop Runtime x64. It checks for a compatible desktop runtime but never downloads or installs it. The MSI stores no user data: `%LOCALAPPDATA%\CODMunity\Top250Collector` settings, logs, data, and history are preserved across upgrade and uninstall.

For the first manual migration, exit the currently installed Inno version, uninstall it, confirm `%LOCALAPPDATA%\CODMunity\Top250Collector` remains, then install the MSI and confirm the prior settings/history remain. No automatic Inno-to-MSI migration is included.

`dist\installer\CODMunityTop250Collector.iss` remains the temporary/historical Inno Setup 7 source for the private alpha; it is no longer the primary installer.

The installer checks for Microsoft .NET 8 Desktop Runtime x64 before installation and stops with a clear message if it is absent. It does not download or silently install the runtime. Uninstall removes only installed program files and shortcuts; `%LOCALAPPDATA%\CODMunity\Top250Collector` settings, logs, data, and history remain intact. A compiled setup is written to `dist\installer\output\CODMunityTop250Collector-0.1.0-alpha-Setup.exe`.

To build the MSI after the WiX 7 EULA has been explicitly accepted by the developer:

```powershell
dotnet build .\Installer.Wix\CODMunityTop250Collector.Installer.wixproj -c Release
```

The output is `dist\msi\CODMunityTop250Collector-0.1.0-alpha-win-x64.msi`.

To compile the historical temporary Inno installer when Inno Setup 7 is installed:

```powershell
& 'C:\Program Files\Inno Setup 7\ISCC.exe' .\dist\installer\CODMunityTop250Collector.iss
```

## v0.1 limitations

No installer updater, API, upload, telemetry, signature scanner, remote manifest, service, administrator elevation, driver, hook, injection, memory rewrite, gameplay automation, or direct C# memory collector is included.
