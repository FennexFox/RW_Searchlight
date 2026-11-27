# Phase 01: Transmission and lifecycle

## Goal

- Make partial transmission directional and consistent across the beam and gameplay light.

## Scope

- Map occupancy, finite-lens ray intersections, beam rendering and refresh.

## Non-goals

- Changing aim, endpoint mode, or save defaults.

## Affected files

- Source beam geometry, map state and lamp updates.

## Implementation steps

- Resolve the strongest occupant per cell, attenuate after exit once per object, update affected beams on occupancy changes.

## Acceptance criteria

- An adult casts 35% shadow, two in line transmit 42.25%. Solid walls retain their original clipping.

## Validation commands

- git diff --check

## Manual smoke tests

- Check both versioned builds and run the shadow fixtures in phase two.

## Rollback risks

- Restore source and both packaged assemblies together.

## Progress

- Not started.

## Decision log

- No decisions recorded yet.

## Outcomes / Retrospective

- Not completed yet.
