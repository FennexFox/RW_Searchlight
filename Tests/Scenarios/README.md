# In-game scenario fixture

Run `Tests/Scenarios/Run-Scenarios.ps1` from any working directory. It uses the RimWorld 1.6 or 1.7 installation containing the repository by default. Pass `-RimWorldPath` or set `RIMWORLD_DIR` to use an alternate installation. The runner requires the .NET SDK and the mod's built assembly for that game version. It builds the fixture in a fresh `-quicktest` save-data profile, prints the profile and log path, and removes the temporary junctions it created.

The fixture sets each spawned searchlight to Independent and uses hostile pawns, walls, a door, and roof cells. It checks Fixed, Sweep, and Automatic behavior, the 60-cell acquisition limit, turret and tower visibility rules, door occlusion, and roof behavior.

The tracking checks sample aim range and angle on every tick as the beam extends from 15 to 60 cells and turns 90 degrees. They check both the endpoint glower's position and the target cell's ground glow. A single pawn walks through 58–61 cells using in-game movement, and the fixture checks the lamp resumes its configured sweep after losing the lock. Other checks cover lock retention, acquisition priority by visibility, distance, and ID, and manual targeting restrictions.

Most fixture phases control `CompPowerTrader.PowerOn` directly. The performance check records wall time for 180 game ticks with zero and ten powered lamps on the same map.

The final Night Only phase connects the searchlight to a `VanometricPowerCell` and saves during daytime standby. A second game process loads the save and checks the persisted Night only and Activate on detection modes on separate lamps, the shared power network, the 18:00 and 06:00 power transitions, and loss of illumination after the generator is removed.

Before that save phase, the Enemy Only fixture checks fixed-mode detection without a target lock, Automatic-mode wake and tracking, and standby after a target disappears. It verifies dark 0 W waiting and cycles the same power-mode command through Always on, Night only, Activate on detection, and back to Always on.

Run `Tests/Scenarios/Run-Scenarios.ps1 -AtmosphereOverhaul` with Harmony and Atmosphere Overhaul installed to exercise the legacy Night only solar schedule. This separate quicktest locates sunset and sunrise from AO's solar elevation on the generated map. A powered Automatic turret tracks a stationary hostile as the map ticks across each horizon. The fixture checks scan counts, lock, power draw, endpoint position and artificial ground glow on both sides, including a sample where the fixed 06:00/18:00 schedule would produce a different result.

Run `Tests/Scenarios/Run-Scenarios.ps1 -PreciseLighting` (or combine it with `-AtmosphereOverhaul`) with Harmony and Atmosphere Overhaul installed for the precise-field variant. It first verifies the actual accumulated field includes intermediate cells, then removes the detected hostile and checks powered 0 W standby restores the entire sampled probe region to its pre-beam values. The Night only check keeps a hostile in range across AO sunrise and verifies the first update after the horizon stops scanning, draws 0 W and clears the previous field cells.
