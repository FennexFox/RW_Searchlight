# Directed Searchlights

These instructions apply throughout the RimWorld mod unless a nearer `AGENTS.md` overrides them. Start with [README.md](README.md) for player-facing behavior and [the verification record](docs/verification/README.md) for scenarios and measurements.

## Where to make changes

- `Source/Building_Searchlight.cs` owns the lamp's operating state, aim, power draw, endpoint light and saved settings. Preserve existing Scribe keys and check defaults against both new buildings and older saves.
- `Source/SearchlightMap.cs` owns per-map registration and drawing. `Source/TargetCoordinator.cs` manages target assignment and reservations, and `Source/DetectionAlerts.cs` manages notifications. Check despawn, power loss and map removal when changing map-owned state.
- `Source/BeamGrid.cs` contains geometry and grid traversal. `Source/BeamField.cs` builds and draws the clipped mesh. Keep the simulation and main-thread mesh upload in their respective paths.
- Edit shared building Defs in `1.6/Defs`. The 1.7 build copies them into `1.7/Defs`. Keep player-facing strings synchronized in `Languages/English` and `Languages/Korean`.

## Behavior to preserve

- A tower can detect an unroofed enemy behind cover. The beam still stops at walls and closed doors. Test target acquisition, beam clipping and actual ground illumination separately.
- Tracking uses the pawn's progress between cells. Keep the existing pan and tilt limits when changing how aim is updated.
- A powered light with **Activate on detection** can scan while its beam is off and its draw is zero. **Night only** also gates scanning. With Atmosphere Overhaul active, the schedule follows the local solar horizon.
- The shipped lighting model uses one vanilla glower at the reached endpoint. For ground illumination, sample `GroundGlowAt` with sky light excluded and allow the glow grid to refresh. Combat Extended samples neighboring cells for combat glow. The [per-cell illumination plan](docs/plan/post-mvp/02-illumination.md) describes future work and its acceptance criteria.

## Build and package

Run PowerShell from the mod root. `build.ps1` reads the selected installation's `Version.txt`. RimWorld 1.6 targets .NET Framework 4.7.2 and 1.7 targets .NET 10. Both use [the shared C# project](Source/DirectedSearchlights.csproj).

```powershell
.\build.ps1
.\build.ps1 -RimWorldPath 'D:\Games\RimWorld-1.6'
```

The second command illustrates an alternate installation. For changes to shared runtime code, rebuild both shipped DLLs against their respective game installations when available. Each build writes to its own version's `Assemblies` directory. Game and optional-mod assemblies are references only.

## Choose verification by change

Set `$game` to the same RimWorld installation used for the build.

| Change | Check from the mod root |
| --- | --- |
| Beam math or grid traversal | `dotnet run --project Tests/Geometry/GeometryTests.csproj -c Release` |
| Tracking, standby, power or save/load | `Tests/Scenarios/Run-Scenarios.ps1 -RimWorldPath $game` |
| Target sharing, reservations or alerts | `Tests/Coordination/Run-Coordination.ps1 -RimWorldPath $game` |
| Rendered beam shape or clipping | `Tests/Visual/Run-Visual.ps1 -RimWorldPath $game` |
| CE lighting or night vision | `Tests/Optics/Run-Optics.ps1 -RimWorldDir $game` with a compatible CE installation |
| Game startup or optional-mod integration | `Tests/Runtime/Run-Runtime.ps1 -RimWorldDir $game` with the relevant `-Case` |
| Packaged assembly, Defs or textures | `Tests/VerifyPackage.ps1 -GameVersion 1.6` and `-GameVersion 1.7` |

For the coordination check, install or link this rebuilt checkout in the selected game's Mods folder before launching. The coordination runner stages its test harness only and does not verify the loaded mod DLL. Check the installation path and matching versioned DLL before relying on a pass.

Use the existing harness runners and their isolated save-data profiles. Keep the player's saves, active mod list and configuration intact. Report the commands, game version, relevant mod versions and observed results. Identify any target that could only be built or could not be exercised.

## Working together

Keep changes scoped and preserve unrelated worktree edits. For independent investigations, delegate bounded questions such as comparing a game API across versions or running a separate compatibility fixture. Avoid simultaneous builds or harness runs that write to the same outputs or staging junction. Prefer a lightweight worker for lookup, an implementation worker for bounded code changes and coordinator review for conflicting evidence or cross-version design decisions.

### Model selection

When spawning a worker, explicitly select both the model and reasoning effort when the runtime supports it.

- **gpt-6.2-astra, low**: preferred root coordinator. Use for task decomposition,
  deciding what evidence is needed, assigning workers, integrating worker
  results, and final checks. Keep the root focused on orchestration and global
  state rather than routine implementation.
- **gpt-6-luna, low**: repository search, symbol/usages lookup, extraction,
  inventory work, repetitive read-heavy inspection, and simple log/test-output
  classification.
- **gpt-6-luna, medium**: bounded analysis that needs modest reasoning but has
  a clear question and narrow evidence set. Also suitable for very small,
  mechanically specified code changes where implementation judgment is minimal.
- **gpt-6-luna, xhigh**: preferred default implementation worker. Use for
  ordinary implementation, refactoring, test writing, and debugging when the
  task is reasonably well specified and the relevant code surface is bounded.
  Prefer this over Luna max for routine software-engineering work.
- **gpt-6-luna, max**: bounded reasoning escalation. Use when Luna xhigh has
  produced an incomplete result, several plausible implementations or failure
  causes must be explored and checked, or a difficult but still well-scoped task
  benefits from deeper search, verification, and revision. Do not use max by
  default merely because a task involves coding.
- **gpt-6.2-sol, medium**: capability and context-integration escalation. Use
  when the relevant scope is broad, several subsystems must be integrated,
  abstraction or API choices have significant downstream effects, or Luna
  repeatedly misses relevant context or produces structurally weak solutions.
  Prefer Sol over simply increasing Luna effort when the likely limitation is
  model capability or context breadth rather than insufficient deliberation.
- **gpt-6.2-sol, high**: difficult implementation and engineering escalation.
  Use for stubborn cross-cutting debugging, complex refactors or migrations,
  subtle stateful/concurrent behavior, or other bounded technical work where Sol
  medium is insufficient. Keep final cross-source synthesis and repository-level
  semantic decisions with the Astra coordinator.
- **gpt-6.2-astra, medium**: root-level escalation for conflicting worker
  evidence, invalidated plans, parser/game-semantics disagreements, repeated
  failures that suggest the problem framing itself may be wrong, or
  architecture/simulation-semantics decisions requiring stronger global
  reasoning.
- **gpt-6.2-astra, high**: exceptional root escalation only for unresolved,
  high-impact, structurally difficult problems where Astra medium has not been
  sufficient.

Model names are routing preferences, not repository invariants. If a requested
model or effort is unavailable, preserve the role separation and use the
cheapest available worker that can reliably perform the bounded task.
