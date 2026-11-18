# Post-MVP: detection coordination and exact beam illumination

## Issue Target And Scope Summary

Requested on 2026-11-19: write implementation plans for the two proposed updates:
1. Detection alerts and target sharing between searchlights.
2. Actual game illumination restricted to the cells reached by the beam.

Status: **planned, not implemented**. Defaults below are design proposals for
implementation and playtesting, not measured balance claims. Existing MVP
acceptance remains recorded in `../searchlights/` and `../../verification/`.

## Strategy

Release update 1 independently, retaining the endpoint CompGlower lighting model.
Then build update 2 behind an explicit lighting-mode setting, preserving endpoint
lighting as a migration and rollback option. Keep detection, assignment, visual
rendering and actual illumination separate: detecting or reserving a target must
never illuminate it before the physical beam arrives.

The current baseline has map-level lamp registration/drawing in SearchlightMap,
staggered acquisition in Building_Searchlight, shared traversal in BeamGrid,
cached meshes in BeamField, one endpoint glower per active lamp and CE tests.
Current Sweep validity includes the configured arc, including retained/manual
targets. The renderer now starts at a finite-width lens while some reach checks
still use the building center. Resolve this distinction explicitly in update 2;
do not implement a second unrelated cone calculation.

## Phase Order

1. [Detection alerts and target sharing](01-coordination.md)
2. [Exact per-cell beam illumination](02-illumination.md)

Each document describes small implementation checkpoints. Commit each tested
checkpoint separately; finish an update's acceptance before enabling it by default.

## Phase Dependencies

- Update 1 requires only the existing MVP. It must work with endpoint lighting.
- Update 2 follows update 1 for release sequencing but must not depend on alerts
  being enabled or on a particular targeting policy.
- The illumination integration proof is a release gate: no unsupported hook may
  silently fall back to approximate lighting while presenting itself as exact.

## Source Of Truth Decisions

- These three files describe future work; they do not replace MVP test evidence.
- Source files inspected for this plan: SearchlightMap.cs, Building_Searchlight.cs,
  BeamField.cs, BeamGrid.cs and SearchlightSettings.cs under Source/.
- Preserve the currently implemented target-validity rules, including Sweep arc,
  roofs, fog, hostility, range and visibility. Sharing changes preference only.
- RimWorld 1.6 is the first acceptance target. Support for 1.7 requires its own
  build and runtime evidence; source compatibility is not proof of correctness.
- AO testing is not reopened. A directly observed regression is documented;
  a comprehensive weather-engine matrix is outside these updates.

## Global Validation Expectations

Run existing geometry, scenario, vanilla runtime, CE optics and visual checks.
Add deterministic tests for each new behavior and record actual game results.
Use isolated profiles and generated fixtures; do not alter normal saves/configs.
Keep ordinary play and main-thread rendering usable after each checkpoint.
New test projects/scripts named in phase documents are planned artifacts.

```powershell
.\build.ps1
.\Tests\VerifyPackage.ps1 -GameVersion 1.6
dotnet run --project .\Tests\Geometry\GeometryTests.csproj -c Release
.\Tests\Scenarios\Run-Scenarios.ps1
.\Tests\Runtime\Run-Runtime.ps1 -Case vanilla
.\Tests\Runtime\Run-Runtime.ps1 -Case ce
.\Tests\Optics\Run-Optics.ps1
.\Tests\Visual\Run-Visual.ps1
```

## Known Risks And Assumptions

- Alerts can reveal enemies or spam the player unless deduplication and existing
  visibility restrictions are preserved.
- Local greedy sharing can cause churn or poor coverage; evaluate the map-level
  result rather than merely showing that lamps choose different IDs.
- Exact lighting changes gameplay, including intermediate cells and CE's spatial
  sampling. It is not just a visual improvement.
- Game light caches and other light sources must remain correct on removal,
  door changes, saves and map switches. Runtime hook details are unverified.
- Performance targets in update 2 are proposed gates, not existing measurements.

## Completion Criteria

Update 1 is done when alerts are useful without repeating, assignment is stable
and respects manual orders, and save/power/map lifecycle tests pass. Update 2 is
done when measured vanilla and CE lighting follows the reached-cell field,
obstacles and overlap remain correct, and a repeatable performance gate passes.
Neither update adds blindness, direct accuracy buffs, thermal mechanics, raid-AI
changes, or a new power balance.
