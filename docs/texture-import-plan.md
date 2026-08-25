# Importing Supreme Commander textures

## The goal

A converted map should look like the map its author made. Today we discard the
original's texture set and repaint it with one of five Sanctuary biomes, so
every conversion arrives looking like our Arid or Tropical preset with someone
else's hills. Supreme Commander ships **402 stratum textures across twelve
environments** — Evergreen, Tropical, Crystalline, Lava, Tundra, Desert,
Geothermal, Swamp, RedRocks, Red Barrens — and the map already tells us which
eight it uses.

## Why it is possible

Three facts, each verified:

1. **A map can carry its own assets.** `Data.PathToID` rewrites any path
   starting `map/` to `Maps/<current map>/…`, and `Data.InitMapFiles` registers
   everything in the map folder recursively. So a `.sanmap` can reference
   `map/Textures/foo_albedo.tga` and the game will find it.
2. **Texture lookup ignores the extension.** `Load.cs` strips it and probes
   `.dds` first. Supreme Commander's textures are already `.dds`, so they can be
   dropped in as-is under a `.tga` name — which is exactly how the shipped maps
   already work.
3. **The source material is readable.** `env.scd` is a 1.3 GB plain zip.

## Milestones

Each one ends with a check that runs over the whole corpus, because the format
walk has already desynchronised once and a silent partial success is the
failure mode that costs the most time.

### M1 — locate the texture set and the two masks

Read the eight texture paths and scales, and the two texture-mask images.

**Not by walking the file.** The walk from the water block crosses wave
generators, decals, decal groups and eight length-prefixed images, and it
already breaks on Seton's Clutch, where four bytes after the wave textures are
not the wave-generator count the format documents. Anchor instead:

- the texture set is a contiguous run of eight null-terminated paths under
  `env/`, each followed by a plausible float scale;
- the masks are DDS blobs, identifiable by their `DDS ` magic and by being
  `mapSize/2` square.

Both are self-validating: a wrong guess fails the structure check.

*Done when:* all 299 maps yield eight paths and two masks of the expected size.

### M2 — decode the masks

DXT5, which unlike BC7 is a fixed layout: an alpha block then two RGB565
endpoints and 2-bit indices. Full per-pixel decode this time, not endpoint
averaging — these are weights, not a tone estimate.

*Done when:* a decoded mask round-trips to a PNG that visibly matches the
original map's minimap.

### M3 — repack for Sanctuary

Eight channels into `stratums_1_4` and `stratums_5_8`, at
`heightmapResolution`, bottom-up, descriptor `0x28`, BGRA order
`[L3,L2,L1,L4]` / `[L7,L6,L5,L8]`. Resample from `mapSize/2` to
`heightmapResolution`.

*Done when:* `Show-Stratums` on a converted map reports each layer sitting on
the slope its Supreme Commander texture name implies — rock steep, grass flat.

### M4 — carry the textures

Extract the eight albedos and their normals from `env.scd` into the map's
`Textures/`, and point `stratumLayers` at `map/Textures/…`.

*Done when:* `Test-Sanmap -CheckTextures` resolves every reference, and the map
renders with them in the editor.

### M5 — the mask variant

Sanctuary expects `_albedo`, `_normal` and `_mask` per layer; Supreme Commander
has no `_mask`. Establish what Sanctuary's masks contain and either synthesise a
neutral one or omit it if the shader tolerates that.

### M6 — end to end

`SCMP_016` and Seton's Clutch converted with original textures, validated, and
looked at in the editor.

## Known unknowns

- **Tile scale.** Supreme Commander stores a `textureScale` per layer;
  Sanctuary has `tileSize`. The relationship is a guess until tested.
- **Size.** Each map becomes self-contained but carries a few MB of textures.
  Fine for a handful; worth a shared folder if hundreds are converted.
- **Layer count.** Supreme Commander has a base plus eight; Sanctuary has a base
  plus eight. They should map one to one, but the base may need care.
- **Licensing.** These are Gas Powered Games textures. Copying them into a map
  folder for local play is one thing; redistributing a map with them embedded is
  another, and worth being deliberate about.
