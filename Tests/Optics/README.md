# CE night optics probe

The probe also measures artificial ground glow seven cells along the centerline
of a 15-cell beam, first with the light on and again 60 ticks after power-off.
The endpoint-mode baseline on RimWorld 1.6.4871 with Combat Extended 16.7.4.0
was **0 on and 0 off** at that cell, with sky glow 0. The lit endpoint measured
0.5 in the same run. Use the paired mid-beam readings for comparison when
testing precise illumination.

Run `Tests/Optics/Run-Optics.ps1` with a RimWorld installation that has a compatible version of Combat Extended and Searchlight. The launcher detects RimWorld 1.6 or 1.7. Pass `-RimWorldDir` to use a separate installation where those mods are available. The probe clears nearby roofs and light sources, places exactly one powered fixed searchlight, sets local time to hour 1, and samples the spot plus cells one to three tiles beyond it along the axis and diagonal. It records RimWorld ground glow, CE `CombatGlowAtFor`, and CE `GetLightingShift` first with an unaided shooter and then with `CE_Apparel_AdvancedNightVision` equipped. The script prints the result and retains its temporary profile and logs. The sampled values serve as diagnostics.
