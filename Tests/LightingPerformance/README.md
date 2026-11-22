# Lighting performance harness

Run from the mod root after building the matching mod assembly:

```powershell
Tests/LightingPerformance/Run-LightingPerformance.ps1 -RimWorldPath $game
Tests/LightingPerformance/Run-LightingPerformance.ps1 -RimWorldPath $game -Full
```

The default is a smoke fixture: 30 warmup ticks, 120 measured ticks, one repeat,
zero and one lamp, and the static workload. `-Full` uses the plan's 600 warmup
ticks, 3,600 measured ticks and three repeats for zero, ten and fifty lamps in
static, rotating, moving-target and toggled-door scenes. Each mode pair runs on
the same map in one isolated quicktest session; the fixture also saves that map
as `lighting-performance-baseline` in the isolated profile. Odd-numbered
repeats reverse mode order to limit ordering bias. The fixture patches quicktest
world generation with `-Seed` and seeds the random starting-tile choice from the
same value, then checks and records the resulting world seed and map tile.

Use `-WarmupTicks`, `-SampleTicks`, `-Repeats`, `-Counts` and `-Workloads` for
smaller targeted runs. Workload names are comma-separated values from
`Static,Rotating,MovingTarget,ToggledDoor`. The runner finds Harmony in the installation's Mods folder
or Steam Workshop (`294100/2009463077/<version>`); pass `-HarmonyPath` when it
is elsewhere.

Each sample records per-tick whole `DoSingleTick` median, p95 and mean, GC
allocation deltas (or a managed-heap estimate on runtimes without per-thread
allocation counters), collection counts, working-set change, beam sample-generation,
aggregation, invalidation-queue and deferred GlowGrid flush timings, plus cell
counts. The result also records three temporary map creation/removal cycles and
the retained managed-memory delta after collection. The runner uses a unique
save-data profile and temporary mod junctions, then removes only junctions it
created. It leaves the profile and result file under `%TEMP%` for review.

For each workload the gate pairs each precise run with its legacy run and
takes the median of the three percentage changes for median tick time and
p95 tick time. All four ten-lamp workloads must stay below 10% and 20%,
respectively. The comparison uses unrounded values. The zero-lamp baseline
and fifty-lamp stress test are reported separately. Rerun the comparison on
an existing result with:

```powershell
./Tests/LightingPerformance/Summarize-LightingPerformance.ps1 -ResultPath $result -EnforceGate
```
