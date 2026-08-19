# CODMunity COD Watchdog Prototype

Isolated diagnostic prototype for a future dedicated-PC Call of Duty and Battle.net watchdog. It is not part of the Collector application and has no network, Collector-control, process-kill, click, reboot, or game-launch action.

The default is a continuous dry run with a 10-second poll interval. Stop it cleanly with `Ctrl+C`. `--active` is retained for exercising the state-machine contract, but remains intentionally non-destructive until a Battle.net launch mechanism and recovery actions have been locally validated.

Commands from the repository root:

```powershell
dotnet run --project tools\CODWatchdogPrototype\CODWatchdogPrototype.csproj -- --diagnose
dotnet run --project tools\CODWatchdogPrototype\CODWatchdogPrototype.csproj
dotnet run --project tools\CODWatchdogPrototype\CODWatchdogPrototype.csproj -- --simulate no-data --once
dotnet run --project tools\CODWatchdogPrototype\CODWatchdogPrototype.csproj -- --capture-state
dotnet run --project tools\CODWatchdogPrototype\CODWatchdogPrototype.csproj -- --trace-bnet-launch
dotnet run --project tools\CODWatchdogPrototype\CODWatchdogPrototype.csproj -- --inspect-bnet-ui
dotnet run --project tools\CODWatchdogPrototype\CODWatchdogPrototype.csproj -- --active
```

`--capture-state` only captures an existing Call of Duty or Battle.net window and keeps the latest ten captures under the prototype diagnostics directory. Visual detectors use the empty folders in `templates` as future local template locations; no OCR is used.

Collection health is inferred read-only from the most recent `CYCLE_OK` in the Collector's local `app.log`. The Collector process and its last `COLLECTOR_ENABLED`/`COLLECTOR_DISABLED` event are evaluated before stale COD data, so an absent Collector is never treated as a COD failure. A newly started COD process waits three minutes for its first in-session cycle before normal stale-data thresholds apply. API sync failures are deliberately not treated as game failure. Battle.net Agent/log update keywords are advisory only until a current, local update signal is validated.

`--trace-bnet-launch` requires Battle.net present and COD closed. It captures read-only process/file metadata, waits for the user to press Play manually, then reports new processes, changed Battle.net files and sanitized launch-hint categories. It never starts the launcher or the game.

`--inspect-bnet-ui` performs a bounded, read-only Microsoft UI Automation traversal of the currently visible Battle.net main window. It reports only elements related to Call of Duty, Play, update, restart, install, and scan/repair; it never invokes a pattern or clicks a control.

`battlenet://game/auks` is a locally validated launcher URI only: it opens Battle.net but does not start Call of Duty. The dynamic `cod.exe` path is diagnostic/monitoring information only and must never be used for process launch, shell execution, or recovery.
## Active startup recovery (revision 5)

`--active` enables only the narrowly-scoped COD startup path: with the Collector running and enabled, it opens the locally validated `battlenet://game/auks` URI only when the Battle.net app is absent, waits up to 60 seconds for its primary window, detects the local Play template at a confidence of at least `0.95`, revalidates it, and sends one left click at the current match center. It then waits up to 120 seconds for `cod.exe` and requires a later local `CYCLE_OK` before reporting recovery success.

Default execution remains dry-run and logs only `WATCHDOG_ACTION_PLANNED`. The prototype never starts `cod.exe` directly. A missing or disabled Collector blocks all actions. Failed starts wait five minutes before another attempt; the existing budget enters cooldown after three failures in 30 minutes. No close, kill, restart, update-handling, keyboard, mouse automation beyond that single Play click, or reboot path is enabled.

## Collection-stale recovery

With the Collector running and enabled, a local `CYCLE_OK` age of 5-10 minutes is only a warning. At 10 minutes the state becomes `NoDataConfirmed`; only its following still-stale evaluation (`CheckingGameState`) is eligible for generic COD recovery. Before closing COD, the watchdog rereads the Collector log and process state. A fresh cycle, missing Collector, disabled Collector, or missing COD cancels the close.

In `--active`, the prototype requests a normal COD window close first, waits 5 seconds, then uses a PID-scoped termination fallback only if COD remains alive. It waits at most 30 seconds for COD to exit. Startup then reuses the validated Battle.net URI and visual Play flow; it never launches `cod.exe` directly. A restarted COD becomes healthy only after a new `CYCLE_OK` after its start time. The resume allowance is 6 minutes, preserving the normal collection interval. Each failed recovery waits 5 minutes; three failures within 30 minutes enter the existing 30-minute cooldown.

For an accelerated local test, `--test-stale-recovery` bypasses only the collection age check. Without `--active` it remains dry-run; do not combine it with `--simulate`.

```powershell
dotnet run --project tools\CODWatchdogPrototype\CODWatchdogPrototype.csproj -- --test-stale-recovery
dotnet run --project tools\CODWatchdogPrototype\CODWatchdogPrototype.csproj -- --active --test-stale-recovery
```
