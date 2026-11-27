# Partial shadows in precise searchlight mode

## Issue Target And Scope Summary

- Issue target: Body-size and cover shadows
- Title: Partial shadows in precise searchlight mode
- Source plan: accepted cover-shadow plan with the subsequent pawn-centered lighting revision.
- Scope: Directional visual shadows across the beam and actual light at all reached pawns on 1.6 and 1.7. Vanilla publishes occupied pawn cells only. CE additionally samples their clipped 3×3 neighborhoods.

## Strategy

- Extend the finite-lens sampler and mesh on de9a507. Reuse existing gameplay aggregation and target visibility.

## Phase Order

1. [Transmission and lifecycle](01-optics.md)
2. [Gameplay and performance verification](02-verification.md)

## Phase Dependencies

- Phase 1 has no phase dependency beyond resolved issue context.
- Phase 2 depends on completion and validation of phase 1.

## Source Of Truth Decisions

- `00-master-plan.md` is the phased implementation plan source of truth.
- Phase files in this directory define phase-local scope and validation.
- Earlier monolithic plans are input material only unless explicitly retained.

## Global Validation Expectations

- git diff --check

## Known Risks And Assumptions

- Moving pawns invalidate fields frequently. Compare ten-lamp scenarios against the original assembly.
