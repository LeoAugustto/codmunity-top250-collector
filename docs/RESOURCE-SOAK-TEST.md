# Resource telemetry soak procedure

Use a portable 0.1.27-alpha build on the dedicated station for 12–24 hours. Close the installed Collector first, then run only the portable build from its extracted folder. Keep the existing `%LOCALAPPDATA%\CODMunity\Top250Collector` state, logs, settings, and discovered layout cache; do not uninstall the production installation or modify COD.

1. Start the portable Collector and confirm the build information identifies the intended source revision.
2. Let the normal Watcher, Battle.net, and collection schedule operate without manual restarts.
3. Periodically inspect `%LOCALAPPDATA%\CODMunity\Top250Collector\logs\app.log` for `RESOURCE_SAMPLE`, `ENGINE_PROCESS_START`, and `ENGINE_PROCESS_EXIT`.
4. If `RESOURCE_PRESSURE_CRITICAL` occurs, verify that the log records `RESOURCE_PRESSURE_DEFERRED`, no engine starts during the deferred interval, and the Watcher does not start COD recovery solely from the deferred collection.
5. At the end, retain only the relevant log excerpts for review. Do not include player data, tokens, or cache contents in reports.

The following command shows only resource and engine-lifecycle records:

```powershell
Select-String -LiteralPath "$env:LOCALAPPDATA\CODMunity\Top250Collector\logs\app.log" -Pattern 'RESOURCE_SAMPLE|RESOURCE_PRESSURE|ENGINE_PROCESS_(START|EXIT)'
```

Stable working set/private memory, handle count, and managed heap measurements across samples are reassuring. A sustained upward trend is evidence to investigate, not by itself proof of a Collector leak; compare it with commit headroom and the top-process snapshot captured at critical pressure.
