# Phase 02: Gameplay and performance verification

## Goal

- Exercise light measurements, visual output, moving cover and simulation cost.

## Scope

- Existing isolated lighting and performance harnesses on both versions, with CE on 1.6.

## Non-goals

- Using player saves or changing global game settings.

## Affected files

- Tests/Lighting, Tests/LightingPerformance and verification records.

## Implementation steps

- Compare baseline and candidate using identical crossing, rotation and tracking workloads. Capture measured results.

## Acceptance criteria

- Match gameplay samples within 0.015, clear shadows after movement, publish observed timing and rebuild counts.

## Validation commands

- git diff --check

## Manual smoke tests

- Check captured shadows and measure isolated fixtures.

## Rollback risks

- Keep reports tied to the exact tested assembly.

## Progress

- Verifying the pawn-centered provider in isolated 1.6, 1.6+CE and 1.7 profiles.

## Decision log

- Keep visual geometry tests across the full beam. Measure gameplay shadow transmission at an occupied receiver cell.

## Outcomes / Retrospective

- Not completed yet.
