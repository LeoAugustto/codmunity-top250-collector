# CODMunity COD Watchdog Prototype

Isolated diagnostic prototype for a future dedicated-PC Call of Duty and Battle.net watchdog. It is not part of the Collector application and has no network, Collector-control, process-kill, click, reboot, or game-launch action.

The default is a continuous dry run with a 10-second poll interval. Stop it cleanly with `Ctrl+C`. `--active` is retained for exercising the state-machine contract, but remains intentionally non-destructive until a Battle.net launch mechanism and recovery actions have been locally validated.

Commands from the repository root:

```powershell
dotnet run --project tools\CODWatchdogPrototype\CODWatchdogPrototype.csproj -- --diagnose
dotnet run --project tools\CODWatchdogPrototype\CODWatchdogPrototype.csproj
dotnet run --project tools\CODWatchdogPrototype\CODWatchdogPrototype.csproj -- --simulate no-data --once
dotnet run --project tools\CODWatchdogPrototype\CODWatchdogPrototype.csproj -- --capture-state
dotnet run --project tools\CODWatchdogPrototype\CODWatchdogPrototype.csproj -- --active
```

`--capture-state` only captures an existing Call of Duty or Battle.net window and keeps the latest ten captures under the prototype diagnostics directory. Visual detectors use the empty folders in `templates` as future local template locations; no OCR is used.

Collection health is inferred read-only from the most recent `CYCLE_OK` in the Collector's local `app.log`. A newly started COD process waits three minutes for its first in-session cycle before normal stale-data thresholds apply. API sync failures are deliberately not treated as game failure. Battle.net Agent/log update keywords are advisory only until a current, local update signal is validated.
