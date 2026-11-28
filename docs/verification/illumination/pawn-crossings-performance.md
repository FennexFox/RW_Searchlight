# Pawn-crossing performance benchmark

## Fixture and run

The harness ran ten lamps with ten seeded pawns moving transversely across the beam path at distinct forward cells. `CrossingStationary` held the lamps fixed, `CrossingRotating` swept them, and `CrossingTracking` assigned one hostile crossing pawn to each lamp. The stationary and rotating movers used player-faction pawns so sweep lamps did not acquire them automatically.

Each run used seed `SearchlightPawnCrossings`, 60 warmup ticks, 240 measured ticks, one repeat, ten lamps, and both providers (`LegacyEndpoint` and `PreciseField`). All four runs reported `PASS`; every sample observed all ten pawns moving, with 121–133 pawn cell transitions. Whole-tick median and p95 are calculated over the 240 measured `DoSingleTick` samples.

| Game | Build | Scenario | Legacy median / p95 (ms) | Precise median / p95 (ms) | Pawn moves, legacy / precise |
|---|---|---|---:|---:|---:|
| 1.6 | de9a507 baseline | Stationary | 0.34770 / 1.12060 | 0.41520 / 1.18140 | 131 / 132 |
| 1.6 | de9a507 baseline | Rotating | 2.82550 / 4.53760 | 6.54910 / 14.38430 | 131 / 131 |
| 1.6 | de9a507 baseline | Tracking | 0.82820 / 3.37470 | 1.53300 / 3.85640 | 131 / 133 |
| 1.6 | candidate | Stationary | 0.46920 / 1.57900 | 1.37260 / 3.32260 | 128 / 126 |
| 1.6 | candidate | Rotating | 3.66760 / 6.08820 | 13.26540 / 17.33800 | 125 / 124 |
| 1.6 | candidate | Tracking | 0.72560 / 1.70830 | 5.31850 / 6.82530 | 121 / 122 |
| 1.7 | de9a507 baseline | Stationary | 0.86420 / 2.50970 | 0.55750 / 1.64220 | 131 / 132 |
| 1.7 | de9a507 baseline | Rotating | 2.82390 / 5.28850 | 5.58880 / 8.57000 | 132 / 133 |
| 1.7 | de9a507 baseline | Tracking | 0.67440 / 1.76510 | 1.83330 / 2.55930 | 131 / 132 |
| 1.7 | candidate | Stationary | 0.81780 / 2.06010 | 1.60060 / 3.75530 | 132 / 130 |
| 1.7 | candidate | Rotating | 3.15210 / 5.97940 | 10.09810 / 16.93970 | 131 / 128 |
| 1.7 | candidate | Tracking | 0.40930 / 1.12550 | 4.08460 / 6.25380 | 128 / 132 |

## Shadow rebuilds per pawn move

The candidate exposes shadow invalidation rebuild counters. This table reports actual precise-mode rebuilds divided by observed pawn cell moves. Legacy mode recorded zero candidate shadow rebuilds. The de9a507 assemblies predate these counters, so their values are `unsupported`, not zero.

| Game | Scenario | Candidate precise shadow rebuilds / move | Affected lamp notifications / move |
|---|---|---:|---:|
| 1.6 | Stationary | 0.60317 | 0.60317 |
| 1.6 | Rotating | 0.22581 | 0.22581 |
| 1.6 | Tracking | 1.34426 | 1.34426 |
| 1.7 | Stationary | 0.64615 | 0.64615 |
| 1.7 | Rotating | 0.22656 | 0.22656 |
| 1.7 | Tracking | 1.44697 | 1.44697 |

## Precise-mode comparisons

These percentages compare the candidate's `PreciseField` sample with de9a507's `PreciseField` sample under the same game version and seeded harness.

| Game | Scenario | Median change | p95 change |
|---|---|---:|---:|
| 1.6 | Stationary | +230.59% | +181.24% |
| 1.6 | Rotating | +102.55% | +20.53% |
| 1.6 | Tracking | +246.93% | +76.99% |
| 1.7 | Stationary | +187.10% | +128.67% |
| 1.7 | Rotating | +80.68% | +97.66% |
| 1.7 | Tracking | +122.80% | +144.36% |

Cross-version precise-mode change from 1.6 to 1.7:

| Build | Scenario | Median change | p95 change |
|---|---|---:|---:|
| de9a507 baseline | Stationary | +34.27% | +39.00% |
| de9a507 baseline | Rotating | -14.66% | -40.42% |
| de9a507 baseline | Tracking | +19.59% | -33.63% |
| candidate | Stationary | +16.61% | +13.02% |
| candidate | Rotating | -23.88% | -2.30% |
| candidate | Tracking | -23.20% | -8.37% |

The baseline `PreciseField` implementation performs full-field illumination sampling; the final candidate uses sparse pawn-shadow invalidations. These paired measurements therefore compare the requested workload across builds, but the precise-mode work is not identical. Treat the percentage changes as diagnostic measurements of the current behavior, not as a direct isolated cost of shadow invalidation.

## Build and harness evidence

| Game | Baseline assembly MVID | Candidate assembly MVID used for timing |
|---|---|---|
| 1.6 | `d5b7a1c0-4f6a-461e-a840-1116f383bfbe` | `2b9de7b2-cef2-4902-82d3-058e95824aa5` |
| 1.7 | `4472a310-f3f4-4449-b84c-86b47b53f9bd` | `fd97c7fe-8ffe-49bc-8621-03a64a5cc4ad` |

After the 1.6 timing, the candidate received a CE active-mod ID lookup normalization at initial map binding. The final 1.6 package MVID is `c994cbd8-c03d-4e0c-82de-066f3a0f0995`; the vanilla timing path does not use that lookup, so the 1.6 run was not repeated. The 1.7 timing used the final candidate MVID shown above.

The identical harness was copied into the de9a507 worktree and candidate checkout; SHA-256 hashes matched for the harness, summarizer, and README at run time. The raw `harness-created`, sample, and `PASS` records for all four runs are in [pawn-crossings-performance-raw.txt](pawn-crossings-performance-raw.txt).

Command used in each checkout (only the game path and checkout root differed):

```powershell
Tests/LightingPerformance/Run-LightingPerformance.ps1 -RimWorldPath $game -Seed SearchlightPawnCrossings -WarmupTicks 60 -SampleTicks 240 -Repeats 1 -Counts 10 -Workloads 'CrossingStationary,CrossingRotating,CrossingTracking'
```
