# In-game scenario fixture

Run `Tests/Scenarios/Run-Scenarios.ps1` from any working directory. It locates RimWorld from this repository's location, builds the isolated test mod, starts a fresh `-quicktest` save-data profile, prints the profile and result log, and removes only its own verified `SearchlightScenarioHarness` junction. It requires the installed RimWorld 1.6 executable, .NET SDK, and the mod's built assembly.

The fixture uses actual spawned searchlights, hostile pawns, walls, a door, and roof cells. It asserts Fixed, Sweep, and Automatic behavior; acquisition at 60 cells and rejection at 61; release after leaving range; low versus tower wall detection; closed and open door occlusion; roofed-target and roofed-tower behavior. Test-only `CompPowerTrader.PowerOn` forcing keeps those scenarios independent of network layout.

The final phase measures elapsed wall time for 180 game ticks with zero and ten powered lamps in the same isolated game. These figures are diagnostic observations, not a strict performance threshold or a controlled benchmark.
