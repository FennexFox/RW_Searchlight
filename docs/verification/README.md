# Directed Searchlights verification record

This page records observed results, test limitations, and reproduction commands.
Measurements are labeled as observations; they are not claims of controlled
performance or gameplay-balance testing.

## Pawn lighting and cover shadows

The [pawn lighting report](partial-shadows/README.md) shows the same beam before
and after an adult enters it, with measured light at the receiving pawn on
1.6 and 1.7. A separate game capture shows which cells Combat Extended samples.

## Earlier per-cell beam illumination

The [lighting verification record](illumination/README.md) covers vanilla
ground glow, the rendered mesh, CE, overlapping lamps, old saves, and tick
time for both lighting providers.

## Target sharing and detection alerts

The [coordination verification record](coordination/README.md) covers the
post-MVP packages, including save migration and the night-raid fixture.

## MVP results recorded on 2026-11-18

**Core MVP acceptance is complete for RimWorld 1.6.** The latest scenario
run passed the original mode/range/door/roof checks and the additional live
tracking tests: 15-to-60 range transition (68 intermediate dark ticks),
90-degree rotation (41 intermediate dark ticks), the same pawn walking
60→59→58→59→60→61, resuming the configured 15-cell sweep, retained-target
priority, visible/distance/ID acquisition ordering, manual validity, power
loss while the switch remains on, recovery and destruction cleanup.
See [scenario results](mvp-1.6/scenarios.txt).

The tests prohibit teleporting the endpoint glower to an unreached target.
They intentionally allow a nearby endpoint's radial glow to touch a pawn
before perfect alignment, as documented by the MVP's vanilla light model.
Ground glow is checked after the grid has updated, with bounded deadlines.

The restored installation is RimWorld 1.6.4871 (`Version.txt`: rev590; the
game log reports rev591). The bundled 1.6 DLL has now been rebuilt from current
source, including rounded caps, width-based visual attenuation and the saved
heading fallback. The build has zero warnings/errors, geometry tests pass,
and a second build to a temporary output directory matches the distributed
DLL byte-for-byte: SHA-256
`4E182AAAF70F20CED167F5185D4D462293B6E6AFC83B364D4348467E9AD491BF`.

Fresh isolated vanilla and CE runs passed moving-target tracking, ten lamps,
switch-off/on and save/reload. The final CE run also saved NaN/Infinity fixed
and patrol headings on one lamp and verified they restored to building
rotation. CE combat glow/penalty remained 0.5/0.5 ON and 0/1 OFF. A separate
optics run confirmed the advanced goggles reduce the center penalty to 0.2.
See [vanilla results](mvp-1.6/runtime-vanilla.txt),
[final CE results](mvp-1.6/runtime-ce.txt) and [optics results](mvp-1.6/optics.txt).
Vanilla and optics were run before the visual-only attenuation change; the
final CE and visual runs used the final DLL above.

The version-aware visual harness passed on 1.6 at 18 and 60 cells and with a
wall at cell 30. The endpoint remained at cell 29 before that wall, and all
reached endpoint cells measured 0.5 ground glow at sky glow 0. The [captured
contact sheet](mvp-1.6/visual.jpg) was inspected for the rounded fade and clean
wall clipping; [measurements](mvp-1.6/visual.txt) are retained. These figures
are not photometric or controlled performance measurements.

The 1.7 DLL remains the earlier rounded-cap build: it was not rebuilt because
the matching game installation is no longer present. Its earlier visual
checks below are historical and do not establish full 1.7 integration
acceptance for current source. AO review is closed at the user's request;
the historical backend-specific limitations below remain documented.

### RimWorld 1.7 rounded-cap visual test

After an initial attempt was blocked by Windows application control
(`0x800711C7`), the visual harness was rerun successfully on RimWorld 1.7.5011:

- `Tests/Geometry/GeometryTests.csproj` completed with `Geometry checks passed.`
- `Tests/Visual/Run-Visual.ps1` completed with `PASS`.
- At night (`skyGlow=0`), the 18-cell and 60-cell aim tests reported
  `GroundGlowAt(ignoreSky: true) = 0.5` at the endpoint.
- With a wall midway through a 60-cell aim, the light moved to cell 29. The
  capture showed the beam stopping at the wall.
- A close-up confirmed the rounded visual tip and its fade. See the [rounded
  tip](rounded-tip-1.7.jpg), [wall clipping](rounded-wall-1.7.jpg), and
  [recorded measurements](visual-results-1.7.txt).

This was a visual test of fixed aim, endpoint glow, and beam clipping. It did
not re-verify the full gameplay scenarios, moving-target tracking, save/load,
power-network behavior, CE, or Atmosphere Overhaul on 1.7. The earlier failed
launch is retained here as history; it is superseded by this successful retry.

The 1.7 source build and both package checks passed at the time of this test.
The visual harness forces power for its fixture, so this is not a power-network
integration test.

### RimWorld 1.6 gameplay scenarios

The recorded in-game run used RimWorld 1.6.4871 rev590 and isolated save-data
profiles. The runtime integration fixture used its generated power sources;
the scenario fixture forced `CompPowerTrader.PowerOn` to isolate target and
occlusion behavior.

| Scenario | Observed result |
|---|---|
| Fixed, Sweep, and Automatic | Fixed performed no target acquisition. Sweep moved with no target; Automatic continued to scan. |
| Range boundary | A target exactly 60 cells away was acquired and illuminated. A target at 61 cells was rejected; a locked target was released after moving out of range. |
| Walls and doors | The turret lost a target behind a wall; the tower retained detection of an outdoor target but its beam stopped at the wall. Opening a closed door allowed the turret to acquire the target again. |
| Roofs | The tower rejected a roofed target and stopped operating when its own cell was roofed. The turret continued to operate under a roof. |
| Illumination | At night, `GroundGlowAt(ignoreSky: true)` returned 0.5 at the target and light-spot cells. |
| Lifecycle and save/load | Flicking off removed the endpoint light; restoring power recreated it. Removing the building removed its light. Ten powered lights, their power state, target lock, and endpoint lights were checked after a save/load in the same process. |

One vanilla run with ten active lights recorded 998 ms for 360 game ticks.
A separate 180-tick run recorded 487 ms with no lights and 595 ms with ten
fixed lights. These are wall-clock observations affected by game speed and
frame scheduling; they are not per-light CPU costs or large-colony frame-rate
benchmarks.

### Combat Extended and Atmosphere Overhaul

On RimWorld 1.6.4871, the recorded compatibility runs passed with vanilla,
Combat Extended (CE) 16.7.4.0, Atmosphere Overhaul (AO) alone, and CE + AO.
The AO pass used its VanillaDerivation weather backend. With AO's default
Simulator backend, the recorded test advanced the clock to night and then
stalled; full behavior with that backend remains unverified.

In an earlier radius-six endpoint-light build, CE-only and CE + AO runs held
the shooter and target at fixed cells and measured these nighttime values:

| CE query | Light on | Light off | Restored |
|---|---:|---:|---:|
| `CombatGlowAtFor` | 0.5 | 0 | 0.5 |
| `GetLightingShift` | 0.5 | 1.0 | 0.5 |

These are CE lighting-penalty inputs, not measured projectile hit rates. A
clean Git export of that earlier radius-six build produced a rebuilt assembly
with SHA-256
`24756D313EAF64E8926B2C7A44BFC8072B53396310C4B8F35A0561FAD12143C5`.
Those measurements should not be read as a test of the current endpoint
radius.

### Current endpoint-light measurements

The width-derived endpoint-light behavior was measured with one fixed
searchlight aimed 15 cells away, at local hour 1 with sky glow 0. The shooter
was measured first without night-vision apparel and then with
`CE_Apparel_AdvancedNightVision` actually equipped. A diagonal cell is offset
by `(1, 1)`.

| Position relative to light spot | Ground glow | CE combat glow | Unaided penalty | Advanced-goggle penalty |
|---|---:|---:|---:|---:|
| Center | 0.5 | 0.500 | 0.500 | 0.200 |
| One cell along axis | 0.5 | 0.355 | 0.645 | 0.258 |
| One cell diagonal | 0.5 | 0.250 | 0.750 | 0.300 |
| Two cells along axis | 0 | 0.145 | 0.855 | 0.342 |
| Three cells along axis | 0 | 0 | 1.000 | 0.400 |

For CE 16.7.4.0, the notes from this run state that the target cell and its
eight neighbors are combined with distance weights and nighttime combat glow
is capped at 0.5. If the shooter's night-vision efficiency is `N` and combat
glow is `G`, the darkness penalty is `max((1 - G) * (1 - N), 0)`. The default
monocular efficiency is 0.4 and the advanced-goggle efficiency is 0.6. These
are lighting-penalty calculations, not actual hit probabilities.

### AO default-backend limitation

A natural-time AO Simulator retry reached map generation but did not begin the
lighting test within 420 seconds. AO's initial forecast setup can itself take
up to 600 seconds, so this result is inconclusive, not evidence of
incompatibility. To retry with a longer limit:

```powershell
.\Tests\Runtime\Run-Runtime.ps1 -Case ao -TimeoutSeconds 1200
```

## Reproduction commands

Run these commands from the mod root in Windows PowerShell. The build script
detects the installed RimWorld version. The package check defaults to 1.6; pass
`-GameVersion 1.7` to check the 1.7 package. Game tests require the relevant
installed game executable and, for modded cases, the named mods.

```powershell
.\build.ps1
.\Tests\VerifyPackage.ps1 -GameVersion 1.6
.\Tests\VerifyPackage.ps1 -GameVersion 1.7
dotnet run --project .\Tests\Geometry\GeometryTests.csproj -c Release
.\Tests\Scenarios\Run-Scenarios.ps1
.\Tests\Optics\Run-Optics.ps1
.\Tests\Runtime\Run-Runtime.ps1 -Case vanilla
.\Tests\Runtime\Run-Runtime.ps1 -Case ce
.\Tests\Runtime\Run-Runtime.ps1 -Case ao -UseVanillaWeather
.\Tests\Runtime\Run-Runtime.ps1 -Case both -UseVanillaWeather
.\Tests\Visual\Run-Visual.ps1
```

Scenario and runtime harnesses retain result logs in their isolated temporary
profiles. The visual harness stores its captures and result log in its
temporary profile. For ordinary play, set a Fixed aim 60 cells away at night
and place a door midway along the beam: closing the door should stop the beam
before it, and opening it should restore the endpoint to the target. For CE
comparisons, keep shooter, target, distance, and weather fixed and exclude
other lights and muzzle flashes.
