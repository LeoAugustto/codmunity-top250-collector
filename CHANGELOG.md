# Changelog

## 0.1.24-alpha

- Add fail-closed Battle.net layout autodiscovery framework with exact-SHA cache revalidation and deterministic multi-anchor requirements.
- Prevent Watcher restart loops when a build/layout is explicitly unsupported.
- Restore Battle.net Season 06 Top 250 collection through the validated `bnet-20260918` layout.
- Support progressively populated Top 250 datasets: sync 1–250 validated rows, and treat zero rows as a valid no-sync state.

## 0.1.23-alpha

- Add an opt-in simplified station watcher that uses local collection health only and restarts Battle.net Call of Duty through the validated Play flow.
- Add Safe Mode dialog handling, bounded recovery cooldown, and crash-loop backoff for dedicated stations.

## 0.1.22-alpha

- Accept SemVer build metadata during update checks while preserving version precedence.
- Log invalid current updater versions before returning the update-check error.

## 0.1.21-alpha

- Add sanitized API and updater network failure diagnostics for local troubleshooting.
- Include runtime and collection/API status in Copy Build Info.
- Fix Start with Windows registration and startup reconciliation from Settings and the tray.
- Launch the Collector after a successful interactive MSI install or upgrade when selected.

## 0.1.20-alpha

- Route the Battle.net Season 5 Reloaded layout through the dynamic exporter.

## 0.1.19-alpha

- Add Battle.net Season 5 Reloaded layout support.

## 0.1.18-alpha

- Prioritize Resurgence synchronization before Multiplayer and Battle Royale.
- Improve Closed Beta build tooling to consume an existing secure build environment credential.

## 0.1.17-alpha

- Preserve Unicode player names across the machine-readable engine transport.

## 0.1.16-alpha

- Reuse verified Battle.net and Steam build identities during an active game session.
- Avoid redundant game executable hashing and EmitJson-only persistence allocations.
- Run managed exporters at BelowNormal priority.

## 0.1.15-alpha

- Send validated in-memory Top 250 snapshots to CODMunity.
- Remove local standings archives and streamline collector actions.
- Confirm updates found through a manual check.

## 0.1.14-alpha

- Refine update settings and improve Settings layout.

## 0.1.13-alpha

- Run automatic updates silently in the background.
- Add manual update checks to Settings and the system tray.

## 0.1.12-alpha

- Windows Installer API end-to-end validation release.

## 0.1.11-alpha

- Package the corrected Windows Installer API UpdateHost in application releases.
- Improve automatic update installation diagnostics.

## 0.1.10-alpha

- Windows Installer API update validation release.

## 0.1.9-alpha

- Install automatic updates through the Windows Installer API without native installer UI.

## 0.1.8-alpha

- Check for trusted updates every 10 minutes.
- Keep automatic update installation fully within the branded update experience.

## 0.1.7-alpha

- Automatic updater end-to-end validation release.

## 0.1.6-alpha

- Automatically download and install trusted updater releases.
- Add branded dark update installation experience.
- Improve updater version logging.

## 0.1.5-alpha

- Complete the first signed remote updater end-to-end validation.

## 0.1.4-alpha

- Move private updater distribution to OCI Object Storage.
- Improve handling of unpublished update manifests.

## 0.1.3-alpha

- Show localized Last sync and Last saved date and time.
- Add signed private update checks and discrete update availability UI.
- Download and verify MSI updates before a safe MajorUpgrade handoff and restart.

## 0.1.2-alpha

- Restore the latest saved leaderboard state during startup.
- Preserve the last successful snapshots while Call of Duty is closed.
- Display the application version automatically from assembly metadata.
- Add the branded WiX Burn installer experience.
- Offer Launch after successful setup.

## 0.1.0-alpha

- First private WPF tray app for local CODMunity Top 250 collection.
- Hosts the proven Battle.net, Steam, and Xbox PowerShell engine locally.
- Sequential five-minute cycles with lightweight `cod.exe` monitoring.
- Local archive retention, status window, tray controls, and opt-in Start with Windows.
- Framework-dependent `win-x64` portable distribution is the standard build and requires Microsoft .NET 8 Desktop Runtime x64.
- Per-user Inno Setup installer source with runtime detection and preserved user data on uninstall.
- No network, API, upload, telemetry, updater, or installer-side runtime download.
