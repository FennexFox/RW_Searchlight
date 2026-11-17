# Searchlight power balance (2026-11-18)

**Decision:** retain 150 W for the turret and 200 W for the tower. These
outputs are plausible for real LED searchlights, while in the game these
buildings are better understood as defensive equipment that tracks distant
targets than as cheap area lights. This is a reasoned balance choice, not a
value established by controlled gameplay balance testing.

## Comparison with RimWorld 1.7.5011 rev24

The vanilla building Defs were checked in the installed Core files. The
installed game had changed from the earlier development target, RimWorld
1.6.4871, to 1.7.5011. These are observations from the installed 1.7 version;
they are not a fresh recheck of the 1.6 Core files. Light radii below are Def
values, not claims about the number of cells illuminated at equal brightness.

| Building | Power | Light radius / role |
|---|---:|---|
| Standing lamp | 30 W, reduced to 15 W by ColoredLights research | Radius 12 |
| Wall light | 30 W, reduced to 15 W by research | Radius 11 |
| Floodlight | 100 W base, reduced to 50 W by research | Radius 24; ColoredLights research is required to build it |
| Sun lamp | 2,900 W while active | Radius 14; high-intensity crop lighting, disabled at night |
| Searchlight turret | 150 W | Tracks targets up to 60 cells away; default endpoint-light radius is about 2 cells |
| Searchlight tower | 200 W | Same illumination, plus detection of outdoor enemies through walls; stops under a roof |

Searchlights do not receive the ColoredLights power discount. After that
research, the turret uses three times the floodlight's power and the tower
uses four times as much. A floodlight is far more efficient for illuminating a
fixed area; the searchlight premium pays for long-range illumination and
automatic tracking. Ten active lights consume 1,500 W or 2,000 W.

The tower's additional 50 W is a gameplay cost for its detection ability, not
a physical requirement of its height. Considering that it must remain
outdoors, the current cost is conservative, but there is no clear evidence
that it should be reduced. The tower is not described as having a brighter
light simply because it uses more power.

## Real-world comparison

Green Inova lists a [150 W / 200 W LED searchlight](https://www.greeninova.com/product/150w-high-power-searchlight)
at 16,209 lm and 21,613 lm, respectively, with a 3.6-degree beam angle for
both models. This is close to the mod's approximately 3.8-degree full beam
angle at maximum range with the default diameter. It demonstrates that a
narrow beam at these power levels is plausible; it does not verify in-game
brightness or balance.

Do not convert cells to a fixed number of meters or the game's 0.5 combat-glow
value to lux. Real optical efficiency, motors, sensors, and the game's
simplified lighting model have no direct one-to-one relationship.
