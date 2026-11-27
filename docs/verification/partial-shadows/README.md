# Directional shadow verification

The control assembly is from `de9a507`, which already includes precise per-cell lighting.
The shadow implementation and bounded map invalidation are in `450e6d5` and `31f2bb9`.
All measurements below use isolated game profiles.

## Visual and ground-light probes

`Tests/Visual/Run-Visual.ps1 -PreciseLighting -RimWorldPath $game` captures the same lane twice at night.
The camera, light, terrain and aim are fixed. For the second capture the fixture adds a single adult human to the center line, 36 cells from the lamp. The live ground-light grid and uploaded section-overlay mesh are both read back in each scene.

| Probe (1.6.4871 and 1.7.5021) | Empty lane, ground glow | Pawn present, ground glow |
|---|---:|---:|
| One cell before the pawn | 0.5000 | 0.5000 |
| Pawn's own cell | 0.5000 | 0.5000 |
| Four cells behind the pawn | 0.5000 | 0.3247 |
| One cell to the side of the shadow probe | 0.3529 | 0.3529 |

Behind the pawn the searchlight's raw RGB channel fell from 43 to 23, and the live ground-overlay center vertex from 33 to 23. The cap still reached 62 cells visually on a 60-cell aim. Both captures were taken with Harmony 3.0.4.0 and assembly MVID `24c1be5c-ed0f-4260-9342-964446010cb3`. [1.6 screenshot](pawn-shadow-1.6.png)

The 1.7.5021 run reproduced every sample above with Harmony 3.0.4.0 and assembly MVID `1fb6ee8b-2e74-4320-bed5-2e0ac8a11bdd`. The camera captures show the beam remaining bright ahead of the pawn and along either flank. [1.7 screenshot](pawn-shadow-1.7.png)
