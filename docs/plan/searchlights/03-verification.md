# Phase 03: Game verification

## Goal

- Verify the mod in RimWorld.

## Scope

- Exercise compatibility, tracking, lifecycle and performance; provide operation and verification instructions.

## Non-goals

- Workshop publishing.

## Affected files

- Tests/Runtime and README.

## Implementation steps

- Run geometry and integration checks with isolated profiles and record observations.

## Acceptance criteria

- Clearly identify measurements and any unobserved scenario.

## Validation commands

- powershell -File build.ps1

## Manual smoke tests

- Inspect beam appearance in a real rendered night scene when available.

## Rollback risks

- Headless measurements do not establish GPU quality.

## Progress

- Completed game scenarios, compatibility fixtures and clean-archive binary verification.

## Decision log

- Use separate game profiles and test mods. Record the AO weather backend because its simulation gates game time.

## Outcomes / Retrospective

- See docs/verification/README.md for measurements, screenshot, commands and default AO simulator limitation.
- 2026-11-18 follow-up: rounded cap source and 1.7 binary built with zero warnings/errors; both version packages checked. Geometry and 1.7 game execution are blocked by Windows application control (0x800711C7), so new visual/runtime checks remain unverified. Tests/Visual preserves a separate-profile capture runner for retry. Existing 1.6 DLL remains the previously verified build.
