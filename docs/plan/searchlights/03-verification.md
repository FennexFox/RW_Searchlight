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

- 2026-11-18 final 1.6 acceptance completed: live range/rotation/boundary tracking, target ranking/manual rules, power loss/destruction, ten-lamp vanilla/CE lifecycle, non-finite saved-heading recovery, CE optics and rendered attenuation all verified. See `docs/verification/mvp-1.6` for retained evidence. Final bundled 1.6 DLL matches a separate source build.

- See docs/verification/README.md for measurements, screenshot, commands and default AO simulator limitation.
- 2026-11-18 follow-up: rounded cap source and 1.7 binary built with zero warnings/errors; both version packages checked. Geometry and 1.7 game execution are blocked by Windows application control (0x800711C7), so new visual/runtime checks remain unverified. Tests/Visual preserves a separate-profile capture runner for retry. Existing 1.6 DLL remains the previously verified build.
- 2026-11-18 retry after the user disabled Smart App Control: Geometry passed and the 1.7 visual harness passed. Inspected rendered rounded tip and wall clipping; stored screenshots and measurements in docs/verification. This supersedes the execution blocker above for these tests.
