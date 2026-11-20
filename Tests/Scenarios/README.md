# In-game scenario fixture

Run `Tests/Scenarios/Run-Scenarios.ps1` from any working directory. It uses the RimWorld 1.6 or 1.7 installation containing the repository by default. Pass `-RimWorldPath` or set `RIMWORLD_DIR` to use an alternate installation. The runner requires the .NET SDK and the mod's built assembly for that game version. It builds the fixture in a fresh `-quicktest` save-data profile, prints the profile and log path, and removes the temporary junctions it created.

The fixture sets each spawned searchlight to Independent and uses hostile pawns, walls, a door, and roof cells. It checks Fixed, Sweep, and Automatic behavior, the 60-cell acquisition limit, turret and tower visibility rules, door occlusion, and roof behavior.

The tracking checks sample aim range and angle on every tick as the beam extends from 15 to 60 cells and turns 90 degrees. They check both the endpoint glower's position and the target cell's ground glow. A single pawn walks through 58–61 cells using in-game movement, and the fixture checks the lamp resumes its configured sweep after losing the lock. Other checks cover lock retention, acquisition priority by visibility, distance, and ID, and manual targeting restrictions.

Most fixture phases control `CompPowerTrader.PowerOn` directly. The performance check records wall time for 180 game ticks with zero and ten powered lamps on the same map.

The final Night Only phase connects the searchlight to a `VanometricPowerCell` and saves during daytime standby. A second game process loads the save and checks the persisted setting, the shared power network, the 18:00 and 06:00 power transitions, and loss of illumination after the generator is removed.
