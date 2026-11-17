# Phase 01: Build and settings

## Goal

- Build the net472 library against the installed game.

## Scope

- Project, build script, persisted zoom diameter.

## Non-goals

- Runtime geometry and buildings come in phase 2.

## Affected files

- Source project, settings, build.ps1.

## Implementation steps

- Resolve game references, build in Release and expose a 2-6 tile slider.

## Acceptance criteria

- No shipped game assemblies and a reproducible build.

## Validation commands

- powershell -File build.ps1

## Manual smoke tests

- Open mod settings in phase 3.

## Rollback risks

- Older game builds may have different signatures.

## Progress

- Release build succeeded against RimWorld 1.6.4871 rev590 with zero warnings.

## Decision log

- The distribution includes only our own net472 assembly.

## Outcomes / Retrospective

- Verified that the output directory contains only DirectedSearchlights.dll.

