# In-game scenario fixture

Run `Tests/Scenarios/Run-Scenarios.ps1` from any working directory. It locates RimWorld from this repository's location, builds the isolated test mod, starts a fresh `-quicktest` save-data profile, prints the profile and result log, and removes only its own verified `SearchlightScenarioHarness` junction. It requires the installed RimWorld 1.6 executable, .NET SDK, and the mod's built assembly.

The fixture uses spawned searchlights, hostile pawns, walls, a door, and roof cells. It checks Fixed, Sweep, and Automatic behavior, the 60-cell acquisition limit, turret and tower visibility rules, door occlusion, and roof behavior.

The tracking checks sample aim range and angle on every tick as the beam extends from 15 to 60 cells and turns 90 degrees. They check both the endpoint glower's position and the target cell's ground glow. A single pawn walks through 58–61 cells using in-game movement, and the fixture checks the lamp resumes its configured sweep after losing the lock. Other checks cover lock retention, acquisition priority by visibility, distance, and ID, and manual targeting restrictions.

The fixture controls `CompPowerTrader.PowerOn` directly to keep it independent of power-network layout. It verifies that a loss of power clears illumination with the switch still on, that restoring power restores illumination, and that destroying the powered lamp removes the glower and its ground light.

The final phase reports diagnostic wall time for 180 game ticks with zero and ten powered lamps in the same isolated game.
