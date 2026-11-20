# Update 1: detection alerts and target sharing

## Goal

Make one searchlight useful as a sentry and multiple lights useful as a coordinated
night-defense system, without granting additional vision or combat bonuses.

## Scope

Map-level alert deduplication, a temporary target marker, per-building alert and
assignment controls, stable shared targeting and save/load migration.

## Non-goals

No exact tile lighting yet, no persistent enemy tracking through fog, no automated
weapon fire, no priority classes such as weapons/roles, and no power/heat changes.

## Behavior contract

### Alerts and markers

- Alert on a newly acquired valid hostile pawn, not every patrol scan. Fixed lamps
  do not acquire enemies and therefore do not emit detection alerts.
- Default alerts to visual-only. Offer Off / Visual / Visual + sound per building,
  with a mod-level default for newly built lamps. Avoid repeated major letters or
  automatic pauses; use one transient, selectable in-game notification.
- A tower's valid outdoor detection through a wall may trigger a detection alert,
  but must be labeled as detection, not successful illumination.
- Deduplicate by map and pawn ID across all lamps. Proposed cooldown: 2,500 game
  ticks per pawn; batch detections arriving within 60 ticks into one notification.
  A pawn must be absent from all valid detections for at least 120 ticks before
  becoming eligible for reacquisition notification after cooldown.
- A brief marker (proposed 180 ticks) may identify a currently valid detected pawn.
  Clear it on invalidation, despawn, death, fog or loss of all detectors. Store no
  persistent marker and never draw a live hidden position. A retained notification
  can point to its original detection cell, without continuously following a pawn.
- Do not reset cooldowns on power toggles or load. Save bounded cooldown/absence
  records, expire entries, and prune references to destroyed pawns/maps.
- Treat initial load as restoration, not a new detection burst. Resume persisted
  cooldown state; old saves without it establish a quiet initial detection set.

### Shared targeting

- Add Independent / Spread / Concentrate per building, separate from
  Fixed / Sweep / Automatic. Existing saves migrate to Independent; new buildings
  default to Spread. Concentrate initially means independently choosing the best
  eligible target without an occupancy penalty; it does not force everyone onto
  an invalid or unreachable shared target.
- Manual orders are hard reservations while valid. They can intentionally share
  a target; automatic coordination cannot replace them. Releasing the manual
  order resumes the chosen policy.
- Preserve a valid lock unless a Spread lamp shares a target while a comparable
  unassigned, directly reachable candidate exists. Proposed minimum hold: 120
  ticks; invalid targets are released immediately. Require the alternative to
  remain eligible across two scans before changing a valid shared lock.
- Acquisition ordering: valid manual order, stable retained lock subject to the
  redistribution rule, direct reachability, least reserved target in Spread,
  distance, pawn ID. Tower detection of blocked outdoor targets remains allowed
  but does not outrank reachable targets merely because the blocked pawn is free.
- At most one reservation per lamp. Count a manual reservation for coverage,
  but do not count powered-off, invalid or removed lamps. Distinguish reservation
  from actual illumination; a rotating beam has reserved but not yet lit a pawn.
- Use map-level deterministic arbitration. Reconcile invalid reservations before
  a batch, process lamps due for their staggered 30-tick scan in stable lamp-ID
  order, and update reservations atomically within that simulation batch.
- Keep the last known candidate/eligibility snapshot bounded by scan age. An
  invalidation must not wait for a long hold timer. Do not introduce a second
  full-map pawn scan every tick solely for alerts.

## Affected files

- Source/SearchlightMap.cs: reservation registry, arbitration, alert queue and
  cleanup; split into small TargetCoordinator/DetectionAlerts helpers if needed.
- Source/Building_Searchlight.cs: query shared policy, release reservations,
  publish acquisition events, gizmos and saved per-lamp options.
- Source/SearchlightSettings.cs and Languages/: defaults and localized controls.
- Tests/Scenarios or planned Tests/Coordination/: deterministic scenarios.
- Tests/Runtime/: load, power, removal and multi-map coverage; docs/verification/.

## Implementation steps

1. Extract a shared read-only target eligibility/candidate description while
   preserving existing behavior. Add an assignment-policy seam; rerun MVP tests.
2. Implement reservations and deterministic arbitration with no user alerts.
   Cover fewer/equal/more lamps than enemies, manual collisions and target loss.
3. Add stable redistribution and building controls. Serialize policy, manual
   intent and hold timestamps; rebuild reservations from actual valid lamps after
   map initialization rather than saving a duplicate authoritative registry.
4. Add acquisition events, cooldown persistence, batched messages and markers.
   Keep sound opt-in. Use only game UI/audio APIs verified for the target version.
5. Measure scan work and UI behavior with 1, 10 and 50 lamps; validate saves and
   disabled settings. Update README and retain fixtures/screenshots/results.

## Acceptance criteria

- One hostile acquired by ten lamps produces one notification per cooldown.
  Continuous tracking, load, and a power toggle do not produce repeated alerts.
- Three Spread lamps with three reachable enemies settle on distinct targets
  within two acquisition cycles after eligibility stabilizes. With more lamps
  than enemies, extra lights may share without oscillating every scan.
- A shared automatic lock redistributes after its hold/stability conditions;
  manual locks remain unchanged. Stable valid unique locks do not churn.
- Visibility precedes occupancy; invalid targets never receive reservations.
  Current Sweep arc restrictions apply equally to manual and automatic choices.
- Releasing/powering off/destroying a lamp frees its reservation. Save/load and
  switching maps do not leak reservations or reveal stale pawn positions.
- Independent mode preserves MVP target selection and illumination results.

## Validation commands

Run the master-plan commands, plus the planned new runner:
`powershell -File Tests/Coordination/Run-Coordination.ps1`.
It must validate assignments and notification event counts, not just screenshots.

## Manual smoke tests

A small night raid approaches three lamps: compare Spread and Concentrate,
manually pin one target, disable alerts, toggle power, then save/reload. Observe
notification wording, sound volume, marker lifetime and assignment stability.

## Rollback risks

Disable new alerts and switch policy to Independent to recover MVP behavior.
Additive saved fields need defaults; rebuild all transient reservations after
rollback/load. Avoid persisting UI objects or copying independent target ownership.

## Progress

The target eligibility extraction, map coordinator and alert delivery are
implemented. The version-aware coordination runner checks real locks, native
message delivery, opt-in sound dispatch, markers and save migration.

## Decision log

Use map-wide sharing rather than introducing configurable lamp groups in the
first release. Default migration preserves old-save behavior. Timer values are
playtest starting points; changes require updating tests and release notes.

Existing lamps migrate to Independent with visual alerts. Absence measures
whether any operating Sweep or Automatic lamp can detect the pawn, including
lamps with alerts off or a different lock. A completed absence is remembered
through cooldown. Confirmation scans for a free alternative may accrue during
the 120-tick lock hold.

## Outcomes / Retrospective

See the [coordination verification record](../../verification/coordination/README.md)
for the version-specific results, timing observations and limitations.
