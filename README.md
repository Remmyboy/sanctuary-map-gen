# sanctuary-map-gen

Map tooling for **Sanctuary: Shattered Sun** — a converter that rebuilds
**Supreme Commander: Forged Alliance** maps as native Sanctuary maps, a
procedural map generator, and a validation stack that checks a map is actually
playable before it ships.

Everything is C# compiled at run time by PowerShell drivers. There is no build
step: run a script and it works.

**289 of the 299 Supreme Commander maps on this machine convert (97%).** Seven
of the rest are campaign maps with no skirmish spawns, refused correctly; one
is non-square (also a campaign map); two are undiagnosed.

---

## Quick start

Convert a Supreme Commander map with its own textures (local play only — the
result contains GPG/Square Enix art):

```
pwsh -File Convert-ScMap.ps1 -Source "C:\...\My Games\...\Maps\loki_-_faf_version.v0004" -Force
```

The same map with CC0 substitutes (shareable — no third-party art):

```
pwsh -File Convert-ScMap.ps1 -Source "...\Maps\loki_-_faf_version.v0004" -Cc0Textures -Name "~SC-Loki_CC0" -Force
```

Generate random maps, deploy everything, audit what is deployed:

```
pwsh -File New-RandomMap.ps1 -Count 6 -Size 512 -Players 2 -Force
pwsh -File Deploy-All.ps1
pwsh -File tools\Show-Sanmap.ps1 -MapDir "F:\...\Sanctuary_Data\Maps\Riverbreak"
```

**Restart the game after deploying.** The engine snapshots map files at load
and will not notice changes underneath it.

---

## What transfers

Everything the source map is, short of decals:

|  |  |
|---|---|
| heightmap | copied byte-exact, zero-error round trip asserted |
| water | level copied; depth from the source's own deep-water elevation |
| Mass / Hydrocarbon markers | alloy spots |
| `ARMY_n` markers | spawns, one army each |
| stratum textures | carried (default) or substituted with CC0 (option) |
| splat weights | the author's own masks, resampled to `heightmapResolution` |
| normal maps | the author's true normal **per layer** |
| props | the author's placements — trees, groups, rocks — onto a biome-matched Sanctuary palette |
| decals | parked; see below |

**The heightmap is byte-exact.** SupCom stores uint16 at a height scale of
1/128 on every map ever shipped; Sanctuary stores uint16 scaled by
`height/65535`. Set `height` to 512 and `65535/512 = 128` — the same
fixed-point encoding. The converter asserts a zero-error round trip.

**z runs the opposite way.** SupCom draws heightmap row 0 at the top, so its z
grows southward; Sanctuary's grows northward. The import negates z on the
terrain, the markers, and the props together. Doing it to only one mirrors the
spawns off the terrain they were authored on, which is why `ScMarkerFit`
asserts terrain and markers agree (tolerance 6 m — community maps carry stale
marker heights; a genuine mirror measures 12 m and up).

---

## Two texture modes

**Default: the source map's own textures.** Extracted from `env.scd` (or the
map's own folder — about one community map in ten ships its own art), carried
in the map folder and referenced as `map/Textures/...`. DXT3 textures — one in
eleven of SupCom's, a format Unity cannot load — are transcoded to DXT5 with
the colour block copied bit-exact. The result looks closest to the original
and is **local-play only**: the folder contains someone else's art.

**`-Cc0Textures`: substitutes from a CC0 library** (ambientCG; ~30 materials,
built by `tools\Build-TexturePack.ps1`). The result is yours to share. Each of
the 312 corpus textures maps onto a same-role material via measured statistics:

- **chroma** (mean colour direction),
- **contrast** (luma standard deviation),
- **feature size** (contrast after 8× downsample — what separates fine
  confetti gravel from bold pebbles when their plain contrast is identical).

A per-channel `diffuseRemap` is then solved so the substitute renders the
exact average colour the original renders (mean error 0.01/255). Sand, gravel
and dirt share one candidate pool because **FA's texture names lie** — its
desert "gravels" are sand in all but name. A small `$eyeOverrides` table in
`Match-Textures.ps1` holds the few calls no metric can make ("soft", "mossy"),
each backed by an in-game comparison.

Two dials tame the photographic sources: `-Cc0TileMult` (default 2.5 — photo
features are centimetre-scale where FA paints for a 4–10 m repeat) and
`-Cc0NormalScale` (default 0.45 — photogrammetry normals are strong). CC0
layers also get real per-material mask maps built from the sources' AO and
roughness. `tools\Compare-MapTextures.ps1` renders any two deployed maps'
layers side by side, as configured, for auditing pairs.

---

## Props

The corpus's 299 maps place **1,685,924 props** between them — 6,716 per map
on average. All of it transfers: position, yaw (rebuilt from SupCom's basis
vectors, negated with the z flip), and bounded scale. Classification by
blueprint path covers **98.7% of every instance** as tree, tree-group or rock.

The Sanctuary side was catalogued by rendering **every shipped prop model as a
silhouette** — `.sanmodel` is name + vertex count + packed xyz floats — and
classifying the pictures, since the blueprints are opaquely named and all call
themselves "Harvestable prop". The naming decodes as `edb*` broadleaf / `edm*`
mineral with `s/m/l` sizes. The converter maps source environment families
onto matched palettes: tundra→Baikal conifers, desert/redrocks→dead snag and
gnarly trees with dark rocks, tropical→olive rocks, temperate→an 18-tree mix.
63 of 94 shipped props are usable (present in both game builds); the
WhiteDesert set (real desert trees, chalk hoodoos) is engine-only and waits on
the devs.

A SupCom "tree group" is one object whose mesh holds several trees; it becomes
one tree at 1.35× rather than inventing positions. `-MaxProps` (default
20,000) thins evenly and reports what it dropped.

---

## Decals — parked at 90%

Everything works except the last step. The scanner reads 290 of 299 maps
(284,626 instances); transforms land in bounds with the projector rotation
decoded from shipped data (`Ry(yaw)·Rx(90°)`); map-local `.sandecal` /
`.sanmaterial` blueprints are authored per source texture and load in-game.
But `RTS/Decals/Default` renders the result invisibly, with no errors and no
shader source to iterate against. `-Decals` keeps the machinery available;
revisit when the developers ship a map that carries its own decals.

Two engine facts from that work matter beyond it:

- **Lua's `Engine.GetFileContent` cannot see map folders.** It reads
  `EM.Lua.FilesCache`, a startup-time dictionary (LJ/lua, the `.sanmap`s,
  `Environment.sanpack`). The `map/` path rewriting belongs to the asset
  pipeline only. The SanctuaryHud mod carries a Harmony fallback that serves
  `map/` paths from the loaded map's folder on a cache miss — decal-carrying
  maps **hard-require the mod on every machine**.
- A missing blueprint reaches Lua as an **empty string, not nil**, so loader
  error branches don't fire and `json.decode("")` aborts `RunMapSetup`
  downstream instead.

---

## Validation

Every check exists because something shipped looking plausible and wrong. The
recurring fault in this project is not code that crashes — it is a value that
renders: a placeholder with splat weight, a texture in an unloadable format, a
lighting field an order of magnitude out. None fail, none log, all look like
terrain.

| tool | what it catches |
|---|---|
| `Test-Sanmap.ps1` | the game's own Newtonsoft parse; asset resolution per build tree; splat weight on placeholder textures; DXT3 in the map folder |
| `Test-LuaJson.ps1` | the game's own `json.lua` (stricter than Newtonsoft) |
| `Test-Deployed.ps1` | all of the above against both deployed trees; runs from `Deploy-All.ps1` |
| `Test-Environment.ps1` | the ~30 lighting/fog fields against the range the shipped maps use |
| `Test-BiomeTextures.ps1` | every biome-table texture has albedo, normal and mask |

Playability is measured, not assumed, and reported in terms that distinguish a
naval map from a broken one:

```
land 74% of map;  over the slope limit 42% of land;  open ground 1%
archipelago: 6 spawns across 6 landmasses (9%, 9%, 9%, 9%, 9%, 9%)
```

(That map spent weeks looking broken at "9% reachable". It is Crossfire
Canal.)

Reachability reimplements the game's Land nav layer: `maxSlope = 30°`, a cell
blocked if any neighbour in its 3×3 exceeds it.

---

## Layout

```
Convert-ScMap.ps1       SupCom map -> Sanctuary map (the main event)
Convert-ScMapGui.ps1    the same, with a WinForms window
New-RandomMap.ps1       random maps by style and biome
New-*Map.ps1            four hand-tuned named maps
Deploy-All.ps1          build + mirror to both game trees + validate

src/    MapGen.cs         heightfield, stratum weights, file writers
        Generator.cs      symmetry, spawn placement, scoring
        PathedMesas.cs    mask library and the plateau pipeline, after Neroxis
        Resources.cs      alloy budget, base rings, expansion clusters
        Terrain.cs        route finding, clearance, chokepoints, overlook
        ScMap.cs          .scmap and _save.lua reader
        ScMapTextures.cs  anchored texture-block scanner (10 albedos + 9 normals)
        ScMapSplat.cs     splat adoption, incl. DXT5-compressed masks
        ScMapPropScan.cs  self-locating prop-table scanner
        ScPropImport.cs   prop classification and frame conversion
        ScMapDecalScan.cs self-locating decal-table scanner
        Bc7.cs / Dxt.cs / Bc3.cs / DdsMean.cs / DdsDecode.cs / DdsWrite.cs
                          DDS decode (BC1/2/3/7 means, full BC1/2/3 pixels)
                          and encode (DXT1/DXT5 with mip chains)
        Biomes.ps1        the biome table (single source of truth)
        Import-MapGen.ps1 compiles the C#; dot-source it

tools/  Measure-ScTextures.ps1   measure + classify every corpus texture
        Build-TexturePack.ps1    fetch CC0 materials, encode DDS + mask maps
        Match-Textures.ps1       solve the substitution table
        Export-ScTextures.ps1    extract source textures into a map folder
        Export-Cc0Textures.ps1   the CC0 equivalent
        Export-ScDecals.ps1      author map-local decal blueprints (parked)
        Compare-MapTextures.ps1  two deployed maps, layer by layer, as configured
        Write-NeutralMask.ps1    the shared mask, from measured shipped means
        Show-Sanmap.ps1          render and audit a deployed map from its bytes
        Test-*.ps1               the validation stack (see above)
        Measure-*.ps1            corpus statistics (markers, terrain, lanes)
        Grab-Editor.ps1          screenshot the map editor window
        New-MaskProbe.ps1        test maps that sweep one mask channel each

texturepack/   generated: CC0 DDS library + manifest (rebuild with
               Build-TexturePack.ps1; downloads cache locally)
docs/          generated measurement CSVs + the substitution table
```

---

## Format notes

Facts that cost real debugging time, recorded so they only cost it once.

**`.sanmap` is JSON** with strict int fields: `width`, `length`, `height`,
`heightmapResolution`, `Army.faction` — Newtonsoft rejects `128.0`, and a map
editor stuck at 0% is usually that. `heightmapResolution` must be a power of
two plus one: Unity rounds invalid values up and `SetHeights` writes into the
corner, leaving up to 44% of the terrain silently at height zero.

**The scmap texture block is 10 + 9**, not the 8 + 4 an obvious reading
suggests: LowerStratum + Strata 1–8 + UpperStratum albedos, then nine normals
that pair with layers 0–8. Modelling it as 8+4 *appears* to work on most maps
while silently dropping stratum 8 and mistaking the upper macrotexture for a
normal map. Ten fully-populated community maps refused to parse at all, which
is what gave it away. Two pre-FA maps use an older interleaved layout and are
not handled.

**Sanctuary's stratum `_mask` is Unity HDRP's mask map** — the engine binary
names the channels (`_MaskmapMetal`, `_MaskmapAO`, `_MaskmapSmoothness`):

| channel | meaning | shipped mean |
|---|---|---|
| R | metallic | 7.5 |
| G | ambient occlusion | 218.5 |
| B | detail mask | 149.5 |
| **A** | **smoothness** | **36.4** |

A flat mid-grey placeholder — the obvious "safe middle" — is wrong in every
channel and put a wet-plastic sheen over every converted map. So did
`skylightIntensity` 6000, where every shipped map uses exactly 0.
`Test-Environment.ps1` exists so the next out-of-range value is caught by a
tool instead of an eye.

**Stratum TGAs** are 18-byte header, type 2, 32bpp BGRA, rows bottom-up;
`stratums_1_4` holds `[L3, L2, L1, L4]`, `stratums_5_8` holds `[L7, L6, L5,
L8]`. Splat resolution equals `heightmapResolution` — vertex-aligned, not a
power-of-two at texel centres.

**Texture lookups are extension-agnostic** (`Load.cs` strips the extension and
probes `.dds` first) — which also means files sharing a stem collide, so a
map-local blueprint must not share its stem with a texture. **Blueprints are
not** extension-agnostic: `.santp` in the engine build, `.sanprop` in the map
editor, hence `-PropExtension` and the double deploy.

**A smoothstep's steepest gradient is 1.5×H/d, not H/d.** Every impassable
ramp in this project traced back to that. Height profiles that must stay under
the nav limit are linear, not eased.

---

## The generator, and what 300 real maps say

The generator's numbers come from measuring 291 SupCom maps and 47 shipped
Sanctuary maps (`Measure-ScCorpus.ps1`, `Measure-Sanmaps.ps1`), not from
invention. The short version:

- **Every spawn gets a ring of alloys** — 3–5 within 6–16 m; three
  independent sources (Neroxis, the SupCom corpus, the shipped maps) agree.
  Base rings are placed first, and `MinAlloysNearSpawn` gates on it.
- **Resource count follows player count, not area** — `AlloyBudget` uses
  Neroxis's formula, which lands on the corpus medians.
- **Resources come in clusters** of 3–4, because an expansion should be
  somewhere you go and hold.
- **Spawn separation depends on player count** — measured closest-pair
  fractions: 2P 0.84, 4P 0.39, 6P 0.24, 8P 0.15.

Structure is measured the same way (`Measure-ScTerrain.ps1`,
`Measure-SanTerrain.ps1`): the route between the two furthest spawns is found
and judged — clearance, sustained pinches, overlook by high ground. The
generator once produced lanes 2–4× narrower than SupCom's and routes hemmed in
by high ground three times as often; lane clearance, directness and overlook
are now gated directly, and a candidate re-rolls on any failure:

| check | threshold |
|---|---|
| spawns and resources reachable | all |
| walkable ground connected | ≥ 92% |
| open ground (contiguous, buildable, < 6°) | ≥ 14% |
| alloys within 20 m of the barest spawn | ≥ 3 |
| lane clearance between spawns | ≥ 2.2% of map size |
| route directness | ≥ 0.82 |
| route overlooked by high ground | ≤ 55% |

---

## Deploying

Maps go in `<install>\engine\Sanctuary_Data\Maps\` for play and
`<install>\map-editor\SanctuaryMapEditor_Data\Maps\` for the editor — same
content, different prop extension. `Deploy-All.ps1` does both and validates
everything it deployed. Restart the game afterwards; it caches map files at
load.

---

## Attribution and licensing

Terrain generation follows the approach of
[Neroxis](https://github.com/FAForever/Neroxis-Map-Generator), the Forged
Alliance Forever map generator.

The CC0 texture library is built from [ambientCG](https://ambientcg.com)
materials (CC0 — no attribution required, included here with thanks anyway).

Converted maps remain the work of their original authors. A map converted with
its source textures contains Gas Powered Games / Square Enix art and is for
**local play only**; the `-Cc0Textures` mode exists so a conversion can be
shared. Either way, credit the mapper.
