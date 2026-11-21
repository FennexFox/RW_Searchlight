# Update 2: exact per-cell beam illumination

## Goal

Make actual vanilla illumination follow the reached beam footprint, including
intermediate cells, so visual coverage and lighting-based gameplay agree.
Preserve CE's own lighting/vision calculations rather than adding an accuracy buff.

## Scope

Shared beam geometry, reached-cell intensity fields, vanilla light integration,
overlap and cache lifecycle, an explicit legacy/precise mode, migration and
performance acceptance. Exact means exact to the documented cell-sampling rule,
not a continuous photometric simulation.

## Non-goals

No per-cell Thing/CompGlower spawning, blindness, thermal simulation, bounced
light, transparent-window support, custom CE hit formulas or new weather matrix.
No modification of the user's normal save/config during testing.

## Lighting contract

- Detection range remains 60 cells measured from the building center, preserving
  existing target validity. Define a shared BeamPose containing center, actual
  lens origin/width, direction, aim distance, zoom and clipped geometry.
- Preserve the finite-width lens introduced in the current renderer. Export the
  authoritative footprint/coverage sampler from the same calculation used by
  BeamField. Do not independently reconstruct a point cone from angle alone.
- A cell is covered if its center is inside the physical beam, within aim range
  and unobstructed by the shared supercover rule. Boundary tolerance must be
  consistent across targeting and rendering. Always sample the central ray.
  Document that partly touched cells whose centers are outside are not lit.
- Walls and closed doors stop illumination at their near face. Blocker-cell
  centers and cells behind them receive no searchlight contribution. Open doors
  transmit light. Include diagonal grazing, map edges, lens offsets and nearby
  blockers in tests. Decorative rounded falloff beyond aim range remains visual
  only; it cannot extend real illumination past 60 cells.
- In precise mode remove the endpoint CompGlower; do not leave its circular glow
  active alongside the field. An illuminated intermediate pawn gets normal
  lighting effects even when it is not the reserved target.
- Initial intensity proposal: multiply a calibrated baseline by lateral edge
  falloff and a softened width-based attenuation, retaining a nonzero center at
  60 cells. Use a separate gameplay profile from material alpha: alpha is not lux
  or GroundGlowAt. Calibrate the default four-cell center to the MVP's measured
  0.5 artificial ground glow at night before varying the profile.
- Narrower beams should not be darker than wider beams in the same conditions.
  Preserve the standard game/CE brightness caps. Explicitly record sampled edge
  intensities and CE neighborhood averaging; CE can report positive combat glow
  for a dark cell next to lit cells, which is not an occlusion leak.
- Combine searchlight contributions by maximum, not unbounded addition. Overlap
  cannot create a super-bright stacking exploit. The adapter must also preserve
  natural sky light and all non-searchlight light sources; do not overwrite or
  normalize vanilla light to the searchlight profile.
- Changing mode is a transaction: unregister the old provider, publish the new
  one and invalidate affected cells. Never double-light for a tick or leave a
  stale endpoint. Legacy saves default to endpoint mode; precise mode becomes
  the default for new games only after the integration/performance gates pass.

## Affected files

- Source/BeamGrid.cs and BeamField.cs: shared lens-aware pose, coverage and clipped
  sample data; preserve cache-driven main-thread rendering.
- Planned Source/BeamLightField.cs: sparse per-lamp intensity snapshots.
- Planned Source/BeamLightingMap.cs: per-map overlap aggregation and dirty cells.
- Planned Source/LightingIntegration.cs: version-scoped vanilla adapter and
  optional compatibility patches proven by installed-assembly evidence.
- Building_Searchlight.cs, SearchlightMap.cs, SearchlightSettings.cs: provider
  lifecycle, settings, rebuilds and migration. About/LoadFolders only if a verified
  patch dependency is necessary; never bundle game assemblies.
- Tests/Geometry, Runtime, Optics, Visual and planned Tests/Lighting/; docs.

## Implementation steps

1. **Integration proof before committing to an architecture.** Inspect installed
   1.6 assemblies and CE readers for GroundGlowAt, RGB/visual light caches,
   invalidation and lighting consumers. Prototype one cell: add/remove a field
   contribution, combine it with a vanilla lamp, save/reload, then sample CE.
   Record exact types/methods/version and whether Harmony is required. A postfix
   on a single getter is not sufficient unless all relevant consumers are proven
   to use it. If the proof fails, keep legacy mode and report a blocked precise
   release; do not claim exact lighting through endpoint lamps.
2. **Shared geometry without gameplay changes.** Produce deterministic coverage
   and intensity snapshots from BeamPose. First compare the debug cell overlay
   to the rendered beam and existing reach tests. Preserve acquisition geometry
   and make any unavoidable near-lens change explicit in fixtures/release notes.
3. **Map aggregation.** Store each lamp's previous sparse field, maintain source
   contributions for affected cells, and recompute maxima only for the union of
   old/new footprints. Removing the strongest lamp must reveal the next source,
   not erase overlapping light. Fast off/despawn/map disposal paths clear all
   contributions and dirty the actual vanilla integration caches.
4. **Precise lighting adapter behind a setting.** Enable verified vanilla and CE
   consumers, replace endpoint lights in precise mode, and test mode toggling.
   Serialize mode/configuration but rebuild fields from saved aiming state.
   Restore simulation caches after load; allocate Unity resources only in Draw.
5. **Bounded updates and optimization.** Stage pose changes and publish fields
   atomically so intensity never uses a different pose from reach/rendering.
   Start with exact simulation-tick updates for changed poses and the existing
   staggered 15-tick obstruction refresh. Enumerate a tight beam bounding box;
   cache ray results/cell indices and avoid scanning the whole map each tick.
   Dirty queries should be constant-time lookups, not LOS computations.
6. **Acceptance and migration.** Run the correctness matrix and controlled cost
   measurements below, then make a deliberate default-mode decision. Retain
   legacy mode. Validate the adapter first on RimWorld 1.6, then build and run
   the same vanilla lighting checks on the installed 1.7. Record results for
   each version. Update 1 coordination must behave identically in both modes.

## Acceptance criteria

- At 5/10/30/60 cells and diameters 2/4/6, sampled inside/outside cells match the
  shared coverage rule, including the center line of the narrow 60-cell beam.
- At night in a cleared fixture, cells outside coverage have zero *searchlight*
  contribution. Mid-beam cells gain light; the old circular endpoint halo is gone.
- Closed/open doors, a diagonal wall, demolition and a wall at 30 cells during
  60-cell aim produce correct fields. Static obstruction changes are reflected
  within the documented 15-tick refresh; power-off and removal clear next update.
- A rotating beam does not illuminate the reserved pawn before physical coverage
  reaches it. A walking pawn crossing the cone boundary receives changing ground
  light. No field reaches past the gameplay range even if the visual cap does.
- Track a pawn walking diagonally. Sample while it is between tile centers and
  check that the rendered beam and illuminated cell centers follow the same
  fractional aim pose through the tile transition.
- In Activate on detection mode, remove the last detected enemy. Switch to
  Night only and test across local sunrise in Atmosphere Overhaul while an
  enemy remains in range. On the next update each lamp must draw zero power
  and withdraw its contribution from every previously lit cell. Compare with
  the lamp-off baseline using artificial ground glow so daylight cannot mask
  stale light.
- Two lamps overlap, one turns off, then the other is removed: each step produces
  the expected maximum without stale light. Existing vanilla lamps and daylight
  are preserved. Repeat across save/load and map switching.
- Same shooter/target CE OFF/ON tests still respond to actual ground lighting;
  repeat with night-vision equipment and at edges. Confirm no double application
  of darkness compensation. Measure outcomes rather than pinning every sample
  to old endpoint-light values, since this update intentionally changes coverage.
- Existing legacy-mode geometry, coordination, lifecycle and optics tests pass.

## Performance gate

Proposed acceptance target: compare legacy and precise modes in the same seeded
map, release build and fixed camera, with 0/10/50 lamps; test static, rotating,
moving-target and frequently toggled-door cases. Warm up 600 ticks, measure at
least 3,600 ticks, repeat three times, and record machine/runtime/mod configuration.
Instrument generation, aggregation and invalidation separately. Ignore startup
and shader warmup; do not infer CPU cost from accelerated wall time alone.

For 10 active lamps, target less than 10% additional median simulation-tick time
and less than 20% additional p95 time versus legacy in the same scene. Treat
these as proposed relative release gates, not universal hardware guarantees.
Require bounded memory after repeated map unloads and no recurring full-map
allocations. Fifty lamps is a stress characterization, not a promised capacity.
If gates fail, optimize before changing defaults; do not silently drop blocked
cells or loosen occlusion. Any multi-tick update cadence must explicitly bound
latency and keep pose, visible beam and lighting snapshots synchronized.

## Validation commands

Run the master-plan commands in legacy and precise modes, plus planned
`powershell -File Tests/Lighting/Run-Lighting.ps1 -Mode precise` and its legacy
comparison. Add a performance runner with seed, lamp count and output metrics.
These new runners must be created during implementation; they do not exist yet.

## Manual smoke tests

Watch a pawn cross the beam halfway to the focus; inspect ground glow and CE
lighting at the lit/dark boundary. Close a diagonal door, remove one overlapping
lamp, toggle lighting modes, save/load and change maps. Compare the debug cell
mask to the visible mesh; verify the decorative cap does not grant extra range.

## Rollback risks

Retain the endpoint mode and additive save settings. On disabling precise mode,
remove all custom contributions and invalidate their cells before restoring the
endpoint light. Unpatch integration at the supported lifecycle point and clear
map caches. A partial hook failure must disable precise mode with an explicit
status; never leave a mixture of registered old and new lighting providers.

## Progress

Plan written; no adapter API, performance budget or precise-light implementation
has yet been validated.

## Decision log

Use a sparse map-owned field rather than one Thing per cell. Keep gameplay range
at 60 and treat the rounded extension as cosmetic. Prefer max overlap to stacking.
Do not assert any particular private game field/hook until the integration proof
has checked the installed assemblies. Keep current AO scope closed.

## Outcomes / Retrospective

Pending implementation. Record the selected adapter and rejected alternatives,
measured light samples, update latency and performance traces before acceptance.
