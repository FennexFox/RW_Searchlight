# Directed Searchlights for RimWorld 1.6

Future work is planned separately in the [post-MVP update plans](../post-mvp/00-master-plan.md): detection alerts with target sharing, followed by exact per-cell beam illumination. Those plans do not change this MVP's completed acceptance scope.

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

## MVP completion follow-up (2026-11-18)

- The user restored RimWorld 1.6.4871 and requested completing the remaining core MVP work. Rebuild its DLL from current source and use 1.6 as the release acceptance target.
- Add actual-game tracking regressions for distance transitions, pre-arrival lighting, moving range boundary, target ordering/manual validity and patrol return. Sweep limits initial acquisition, while a valid acquired target may be followed outside the patrol arc.
- Repeat vanilla/CE lifecycle, ten-light and optics verification. AO review is closed by user instruction.
- Add the agreed visual expansion attenuation without changing ground illumination or CE mechanics; validate geometry and rendered screenshots.
- Update completion evidence and package notes. Full 1.7 integration acceptance remains separate from core 1.6 MVP acceptance because that installation has been replaced.

### Outcome

- Complete for the restored 1.6 acceptance target: current binary matches a separate source build, geometry/package checks pass, live tracking scenarios pass, vanilla/CE lifecycle and CE optics pass, and final visual captures were inspected.
- The additional visual expansion attenuation is implemented without changing the ground-light model. Scenario logs and captures are retained under `docs/verification/mvp-1.6`.
- Remaining follow-ups are outside this acceptance: updating/revalidating the 1.7 binary, exact per-cell cone illumination, and controlled large-colony performance tuning. AO is not reopened.

## Known Risks And Assumptions

- Validate installed RimWorld 1.6.4871 and optional CE/AO using isolated game data.
