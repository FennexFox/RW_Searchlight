# Phase 02: Searchlights and beam geometry

## Goal

- Deliver both working lights.

## Scope

- Definitions, art, geometry, tracking, rendering and the temporary spotlight.

## Non-goals

- Full directional ground glow is deferred.

## Affected files

- About, Defs, Textures, Source and Tests/Geometry.

## Implementation steps

- Share opacity tests; clip the cone mesh and update a single vanilla glower per light.

## Acceptance criteria

- Build passes; geometry cases cover zoom, range and obstructions.

## Validation commands

- powershell -File build.ps1

## Manual smoke tests

- Use the isolated integration profile in phase 3.

## Rollback risks

- Changing load/save identifiers can invalidate existing lights.

## Progress

- Not started.

## Decision log

- No decisions recorded yet.

## Outcomes / Retrospective

- Not completed yet.
