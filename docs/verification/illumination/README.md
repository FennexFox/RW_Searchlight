# Per-cell illumination verification

## Endpoint reference

On RimWorld 1.6.4871 (Version.txt rev590, runtime rev591), CE 16.7.4.0,
the isolated optics fixture passed on the pre-change assembly
`c349eb94-bbee-476d-bf49-2d011a0a8863`. A fixed light aimed 15 cells away
gave 0.5 ground glow and 0.5 combat glow at the endpoint at sky glow zero.
The unaided lighting shift was 0.5. At seven cells the ground glow was zero
both on and off. Reproduce from the mod root with
`./Tests/Optics/Run-Optics.ps1 -RimWorldDir $game16`.

## Installation paths used

- 1.6.4871 rev590: Steam `steamapps/content/app_294100/depot_294104`.
- 1.7.5014 rev1195: Steam `steamapps/common/RimWorld`.

Both installed game assemblies expose the integer-index overload of
`Verse.GlowGrid.GetAccumulatedGlowAt`. Vanilla gameplay brightness and visual
RGB read this value. The section lighting overlay regenerates from
`VisualGlowAt`. `DirtyCell` marks ground-glow meshes and publishes a cell
change event. The getter preserves cave-plant filtering and checks vacuum
terrain before returning the stored color. These are static inspection
results. Runtime checks are recorded separately.

The endpoint visual fixtures also passed before the adapter change on both
installations. At aim distances of 18 and 60 the spot was at the aim cell.
A wall at distance 30 moved the spot to cell 29. All three endpoint samples
measured 0.5 artificial ground glow at sky glow zero. Reproduce with
`./Tests/Visual/Run-Visual.ps1 -RimWorldPath $game`.

CE 16.7.4.0 reads `GroundGlowAt` in its lighting tracker and caches sampled
values. The integration fixture allows simulation ticks between transitions
so CE can refresh and the game can rebuild lighting sections.

## One-cell adapter

The Release adapter passed fresh-game and second-process reload checks on
1.6.4871 with Harmony (0Harmony 2.4.3.0), both with and without CE 16.7.4.0,
and on 1.7.5014 with Harmony (0Harmony 3.0.0.0). Each runner checked the
loaded mod MVID. A Harmony postfix augments the indexed accumulated-color
query. A prefix on the grid update drains changed cell indices and invokes
the game's own invalidation. No game or Harmony DLL is distributed.

| Night sample | Field off | Field on | Removed |
|---|---:|---:|---:|
| Target artificial ground glow | 0 | 0.5 | 0 |
| Target visual RGB, each channel | 0 | 64 | 0 |
| Target overlay center vertex, each channel | 0 | 16 | 0 |
| 1.6 CE combat glow | 0 | 0.0806 | 0 |
| 1.6 CE lighting shift | 1 | 0.9194 | 1 |

A lower field value of 20 produced ground glow 0.2824. Adjacent cells across a
section boundary retained zero artificial ground glow while their shared mesh
corners refreshed. The daylight sample remained 1, and the torch-only sample
returned to 0.1553 after removing the field. Reload restored the torch and
dark field baseline, and republishing the transient cell reproduced the light.

Reproduce with `./Tests/Lighting/Run-Lighting.ps1 -RimWorldPath $game -Case vanilla`
on both games and `-Case ce` on 1.6. The initial fixture exposed an invalid
argument order in the game's debug mesh reporter. The passing fixture reads
the finalized overlay mesh's actual vertex colors and verifies both sides of
a section boundary.
