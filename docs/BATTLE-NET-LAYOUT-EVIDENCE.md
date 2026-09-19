# Battle.net layout evidence workflow

`tools/capture-layout-evidence.ps1` is a development-only, static-file research tool. It does not open the COD process, read process memory, modify COD, or change the trusted discovery catalog. Its default output is outside the repository:

`%LOCALAPPDATA%\CODMunity\Top250Collector\research\layout-evidence\<SHA256>\<timestamp>\`

Each bundle contains `layout-evidence.json` and `layout-evidence.txt`: executable SHA-256, PE metadata and section map, RVA/file-offset mappings, bounded windows around the nine known MP/BR/Resurgence anchors, recognised nearby RIP-relative instruction references, and only technical structural relationships. It never stores player data.

## Next COD update procedure

1. Let the Collector identify the new SHA and remain fail-closed; do not add offsets or enable API sync.
2. Capture the static executable bundle with:

   ```powershell
   .\tools\capture-layout-evidence.ps1 -CodPath 'C:\path\to\cod.exe'
   ```

3. With the same game running, capture bounded mapped-memory and executable-reference evidence. This opens the process only with read/query access and stores no player content:

   ```powershell
   .\tools\capture-live-layout-evidence.ps1 -CodPath 'C:\path\to\cod.exe'
   ```

4. Compare the new JSON bundles with the baseline for stable instruction/reference structure, section mapping, and independently resolvable targets.
5. Derive a proposed signature only when the evidence reproduces across both builds; wildcard only relocation-sensitive bytes that differ between them.
6. Validate the candidate against both executables and independently validate all nine anchors plus live 250-row MP, BR, and Resurgence reads.
7. Add a single deterministic method to `battlenetDiscovery.methods` only after those checks pass.
8. Run `top250_orchestrator.ps1 -StaticDiscoveryOnly` against the new executable as an unknown build and require one unambiguous candidate.
9. Require exact rediscovery of the independently validated new layout and repeated live validation before production use.

A one-build pattern, a repeated RVA delta, a count of 250, or plausible rows alone are never sufficient evidence.

## Season 06 live structural proof

The read-only cold-flow harness was validated against the Season 06 Battle.net
build (`DA8DE033EFE34B77DA0D6014EFFA9C551C0E30A3CFB4A3B39A52D6B86AD03A09`).
It excluded the trusted static layout and started with no discovered-layout
cache. Structural discovery found one semantically valid MP/BR/Resurgence
triple in about 1.8 seconds, and normal read-only extraction succeeded with
partially populated leaderboards. The validated layout was written to an
exact-SHA temporary cache; a second pass loaded and revalidated that cache,
then matched the trusted Season 06 layout exactly.

This proof contains no player records, names, or identifiers. It validates the
discovery workflow only; production release integration remains a separate step.
