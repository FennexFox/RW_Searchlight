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

| Change | Check from the mod root |
| --- | --- |
| Beam math or grid traversal | `dotnet run --project Tests/Geometry/GeometryTests.csproj -c Release` |
| Tracking, standby, power or save/load | `Tests/Scenarios/Run-Scenarios.ps1` |
| Target sharing, reservations or alerts | `Tests/Coordination/Run-Coordination.ps1` |
| Rendered beam shape or clipping | `Tests/Visual/Run-Visual.ps1` |
| CE lighting or night vision | `Tests/Optics/Run-Optics.ps1` with a compatible CE installation |
| Game startup or optional-mod integration | `Tests/Runtime/Run-Runtime.ps1` with the relevant `-Case` |
| Packaged assembly, Defs or textures | `Tests/VerifyPackage.ps1 -GameVersion 1.6` and `-GameVersion 1.7` |

Use the existing harness runners and their isolated save-data profiles. Keep the player's saves, active mod list and configuration intact. Report the commands, game version, relevant mod versions and observed results. Identify any target that could only be built or could not be exercised.

## Working together

Keep changes scoped and preserve unrelated worktree edits. For independent investigations, delegate bounded questions such as comparing a game API across versions or running a separate compatibility fixture. Avoid simultaneous builds or harness runs that write to the same outputs or staging junction. Prefer a lightweight worker for lookup, an implementation worker for bounded code changes and coordinator review for conflicting evidence or cross-version design decisions.
