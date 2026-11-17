# Directed Searchlights for RimWorld 1.6

## Issue Target And Scope Summary

- Issue target: Directed Searchlights MVP
- Title: Directed Searchlights for RimWorld 1.6
- Source plan: None
- Scope: build and verify two 60-tile tracking lights with range-dependent zoom.

## Strategy

- Use one clipped beam model for rendering and aim, and a vanilla radius-six light for ground illumination.

## Phase Order

1. [Build and settings](01-build.md)
2. [Searchlights and beam geometry](02-runtime.md)
3. [Game verification](03-verification.md)

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
