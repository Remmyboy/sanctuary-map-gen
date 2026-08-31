# Tuning notes

Dials whose current values are informed first guesses, not measured truths.
Each was calibrated against the shipped maps' numbers or the corpus, but none
has been iterated against the screen more than once. When a converted map
looks wrong in a way that is even, global and non-crashing, suspect this list
first. Values live where the **file** column says; change them there and both
pipelines pick them up (everything below `src/` is shared engine code).

## The one assumption, not a number

| what | current | file | how to falsify |
|---|---|---|---|
| Sun azimuth convention | light rotation assumed `Euler(sunDA, sunRA, 0)`, so direction-to-sun = `(-cos DA sin RA, sin DA, -cos DA cos RA)` | `src/ScMapEnvironment.cs` `ScSunAngles` | Loki (azimuth +70°) and Seton's (−48°) are deployed side by side: their shadows must fall on opposite sides. If ridges are lit from the wrong side vs the FA original, flip the sign of `sunRA` |

## Lighting (`src/ScMapEnvironment.cs`)

| dial | current | why this value | watch for |
|---|---|---|---|
| `sunDA` clamp | 15–30° | shipped band exactly | flat noon look = raise ceiling; everything in shadow = raise floor |
| Sun temperature | `6500 − 5000·log2(r/b)`, clamp 5000–9800 K | neutral white → 6500; shipped band as clamp | most FA maps are warm and land near 5000 — if everything reads orange, soften the 5000 multiplier |
| Intensity pivot `ScLightPivot` | 1.94, clamp 25000–60000 lux | corpus median of `lightingMultiplier × sun luminance`; median map keeps shipped 60000 | dark maps (White Fire: 34k lux) — if they read murky rather than moody, raise the 25000 floor |
| Fog attenuation | source fog band × 0.63, clamp 24–500 m | 0.63 ≈ HDRP's 1/e point vs SupCom's saturation distance | Canis River lands at 113 m — the mistiest deployed test. Too thick → raise the 0.63; fog invisible everywhere → lower it |

## Tint noise (`src/ScMapEnvironment.cs` tables, loop in `src/MapGen.cs` WriteTintColors)

| dial | current | watch for |
|---|---|---|
| `TintNoiseLum` per role | veg .09, dirt .07, mud .06, sand/gravel .05, snow .04, rock .03 | speckle at commander zoom = too high; ground still reads flat = too low |
| `TintNoiseWarm` per role | veg .05, dirt/sand .04, mud/gravel .02, rock/snow .01 | green/magenta cast on open ground = too high |
| Mottle scales | 28 m (3 oct) mixed 0.7 with 9 m (2 oct) at 0.3; warm noise 44 m | fixed metres by design — change the metres, not to map-relative |
| Pre-existing wash | broad 0.10 @ 0.55×map, fine 0.055 @ 0.11×map, height lift 0.07 | untouched by this work; listed because it stacks with the mottle |

## Wet shoreline (`src/MapGen.cs` WriteTintColors)

| dial | current | watch for |
|---|---|---|
| Max darkening | 13% | reads as a dirty ring rather than damp ground = too strong |
| Band height | 2.5 m above waterline | band ends mid-beach = raise; climbs hills = lower. Height-based by design (beach wide, cliff thin) — a horizontal distance transform is the upgrade path if height ever misbehaves |

## Macro overlay bake (`src/ScMapEnvironment.cs` SampleMacro / AdoptScMacro)

| dial | current | why |
|---|---|---|
| Albedo stand-in | 0.37 / 0.35 / 0.32 per channel | the default `diffuseRemap` mid tone; the true blend needs the albedo under each texel, which the bake cannot know |
| Factor clamp | 0.55–1.45 | keeps a near-black overlay pixel (lava sets) from deleting the ground |
| Invisible-skip | mean alpha < 2/255 | below this the overlay never showed in FA either |
| Scale sanity | 8–4096 m per repeat | corpus min is a degenerate 1.0 on a few maps |

## Mask smoothness per role (`src/ScMapEnvironment.cs` RoleSmoothness)

Banded around the shipped mean of 36.4 — the dial with the wet-plastic
history. Judge on Seton's mud flats vs its rock.

| role | value | | role | value |
|---|---|---|---|---|
| mud | 55 | | sand | 30 |
| snow | 50 | | dirt | 27 |
| rock | 45 | | veg | 24 |
| gravel | 38 | | | |

## Wreckage import (`src/ScWrecks.cs`)

| dial | current | watch for |
|---|---|---|
| `ScWreckMinMass` | 30 (walls cost 2, a T1 tank 56) | reclaim fields feeling empty = corpus maps lean on mid-value debris just under it |
| Size ladder | hitbox area ≤ 0.5 / 2.5 / 9 / 30 / else, aspect > 1.3 splits the two mid meshes | wrecks visually too big or small for what they were — judge on The_Dark_Heart's debris field |
| Economy | every SSS wreck blueprint is worth 100 alloys / 10 s (dev placeholder values) | positions and silhouettes are faithful, per-wreck value is not — revisit when the devs tune their wreck blueprints or ship a wider set |
| Harvesting itself | **not in the Playtest build** — harvest values and tags exist on every blueprint, but no command, system or Lua consumes them (verified by reflection: zero harvest/reclaim members across the gameplay assembly's 98 enums) | wrecks and props are visual and blocking only until the devs ship the mechanic; when they do, everything already placed lights up with no converter changes |

## Playable-area guards (`src/ScMapEnvironment.cs` ScPlayableArea)

Behavioral rather than cosmetic; less likely to need touching, but if a map's
border comes out wrong, these are the gates: ≥ 16 m a side, ≥ 25% of map
area, every spawn inside with 1 m slack.

## Pre-existing dials already known uncertain

Inherited, documented here so the list is in one place:

| dial | current | where | note |
|---|---|---|---|
| Stratum tile scale | carried 1:1 from SupCom `textureScale` | both stratum builders | comment says untested — "if the ground reads too coarse or too fine this is the number to change" |
| `-Cc0TileMult` | 2.5 | ConvertOptions / Convert-ScMap.ps1 | photo features are cm-scale vs FA's 4–10 m repeat |
| `-Cc0NormalScale` | 0.45 | same | photogrammetry normals are strong |
| `fogMaximumDistance` | 1800 (shipped: 1500) | converter JSON constants | flagged by Test-Environment on every converted map |
| `windDirection` | 160 (shipped: 100) | same | same |
| `waterDepth` clamp | 1–8 m | same | source deep-water elevation, clamped |
| Prop scale clamp | 0.5–2.0, tree groups ×1.35 | Converter props section | tree-group size is one number standing in for a whole mesh |
| `-MaxProps` | 20000 | ConvertOptions / Convert-ScMap.ps1 | every placed prop carries harvest values (5 alloys + 20 plasma, from the shipped blueprints), so once the devs ship the harvest mechanic, thinning the densest maps also trims their total reclaim, not just their looks |
