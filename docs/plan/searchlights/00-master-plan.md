# Directed Searchlights for RimWorld 1.6

## Issue Target And Scope Summary

- Issue target: Directed Searchlights MVP
- Title: Directed Searchlights for RimWorld 1.6
- Source plan: None
- Scope: build and verify two 60-tile tracking lights with range-dependent zoom.

## Strategy

- Use one clipped beam model for rendering and aim, and a vanilla endpoint light whose physical radius follows the beam width (supersedes the original radius-six MVP after user feedback). The rounded visual falloff may extend beyond the focal plane without extending gameplay reach.

## Phase Order

1. [Build and settings](01-build.md)
2. [Searchlights and beam geometry](02-runtime.md)
3. [Game verification](03-verification.md)
4. Follow-up: retain 150W/200W after installed-Core and manufacturer comparison; round the visual endpoint while preserving gameplay reach, then build, test and capture an isolated game scene. See `docs/verification/power-balance.md` and the verification log.
   User authorized continuing on the installed 1.7.5011 after Steam removed 1.6. Build the same source against its .NET 10 compatibility assemblies into a separate 1.7 folder; retain the previously verified 1.6 binary until a 1.6 installation is available.

## Phase Dependencies

- Phase 1 has no phase dependency beyond resolved issue context.
- Phase 2 depends on completion and validation of phase 1.
- Phase 3 depends on completion and validation of phase 2.

## Source Of Truth Decisions

- `00-master-plan.md` is the phased implementation plan source of truth.
- Phase files in this directory define phase-local scope and validation.
- Earlier monolithic plans are input material only unless explicitly retained.

## Global Validation Expectations

- powershell -File build.ps1

## Known Risks And Assumptions

- Validate installed RimWorld 1.6.4871 and optional CE/AO using isolated game data.
