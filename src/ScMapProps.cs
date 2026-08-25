// The rest of the .scmap format: from the water block through to the props.
//
// The converter used to stop reading at the water settings, which meant
// discarding everything the map's author actually placed - their texture
// choices, their decals, and their prop layout - and substituting a generated
// biome and a random scatter. A converted map looked like our map with someone
// else's hills.
//
// Props are the biggest recoverable loss and the cheapest: they are plain
// records of a blueprint path and a transform, no image decoding involved. Tree
// lines along a ridge, a rock field guarding an expansion, a wrecked convoy on
// a road - all of it authored, all of it currently thrown away.
//
// Getting to them means walking everything in between, because the format is a
// stream with no offsets: wave generators, the terrain texture set, decals,
// decal groups, and a run of length-prefixed images. That walk is the awkward
// part, so it is verified against the whole corpus rather than one map.
public static partial class MapGen
{
    public class ScProp
    {
        public string Blueprint = "";
        public float X, Y, Z;
        public float ScaleX = 1f, ScaleY = 1f, ScaleZ = 1f;
        /// Rotation as SupCom stores it: three basis vectors, not a quaternion.
        public float RotXx, RotXy, RotXz;
        public float RotZx, RotZy, RotZz;
    }

    public class ScDecal
    {
        public string Texture = "";
        public int Type;
        public float X, Y, Z;
        public float ScaleX, ScaleY, ScaleZ;
        public float RotX, RotY, RotZ;
        public float CutOffLod;
    }

    /// What the map's author chose, recovered from the parts of the file the
    /// converter used to skip.
    public class ScContent
    {
        public string[] TexturePaths = new string[8];
        public float[] TextureScales = new float[8];
        public List<ScDecal> Decals = new List<ScDecal>();
        public List<ScProp> Props = new List<ScProp>();
        /// Byte offset and length of the two texture masks, so a caller that
        /// wants the splat can decode them without re-walking the file.
        public int TexMaskLowOffset, TexMaskLowLength;
        public int TexMaskHighOffset, TexMaskHighLength;
    }

    static void SkipStrZ(byte[] b, ref int p) { while (p < b.Length && b[p] != 0) p++; p++; }

    /// Continue from the end of the water block to the props.
    ///
    /// `p` must be positioned immediately after waterElevationAbyss. Returns
    /// null rather than throwing if the walk runs off the end, because a
    /// desynchronised read should degrade to "no content recovered" and let the
    /// heightmap conversion proceed.
    public static ScContent ReadScContent(byte[] b, int p, int versionMinor)
    {
        try
        {
            var c = new ScContent();

            // ---- the rest of the water block ----
            // Surface and depth colours, the fresnel and reflection terms, the
            // sun, then two texture paths and four wave textures. All fixed
            // size except the strings.
            p += 12;            // surfaceColor
            p += 8;             // colorLerp
            p += 4;             // refraction
            p += 4;             // fresnelBias
            p += 4;             // fresnelPower
            p += 4;             // unitReflection
            p += 4;             // skyReflection
            p += 4;             // sunShininess
            p += 4;             // sunStrength
            p += 12;            // sunDirection
            p += 12;            // sunColor
            p += 4;             // sunReflection
            p += 4;             // sunGlow
            SkipStrZ(b, ref p); // texPathCubemap
            SkipStrZ(b, ref p); // texPathWaterRamp
            for (int i = 0; i < 4; i++) p += 4;                 // wave normal repeats
            for (int i = 0; i < 4; i++)
            {
                p += 8;                                          // normalMovement
                SkipStrZ(b, ref p);                              // texPath
            }

            // ---- wave generators ----
            int waveGens = RdI32(b, ref p);
            if (waveGens < 0 || waveGens > 4096) return null;
            for (int i = 0; i < waveGens; i++)
            {
                SkipStrZ(b, ref p);   // texture
                SkipStrZ(b, ref p);   // ramp
                p += 12;              // position
                p += 4;               // rotation
                p += 12;              // velocity
                p += 4 * 10;          // lifetime, period, scale, frame count and rates, strip count
            }

            // ---- minimap colours and the terrain texture set ----
            p += 4 * 6;                                          // contour interval and five colours
            if (versionMinor > 56) p += 4;                       // unused float

            for (int i = 0; i < 8; i++)
            {
                c.TexturePaths[i] = RdStrZ(b, ref p);
                c.TextureScales[i] = RdF32(b, ref p);
            }
            for (int i = 0; i < 4; i++) { SkipStrZ(b, ref p); p += 4; }   // normals
            p += 8;                                              // two unused ints

            // ---- decals ----
            int decalCount = RdI32(b, ref p);
            if (decalCount < 0 || decalCount > 200000) return null;
            for (int i = 0; i < decalCount; i++)
            {
                var d = new ScDecal();
                RdI32(b, ref p);                                 // id
                d.Type = RdI32(b, ref p);
                int texCount = RdI32(b, ref p);
                if (texCount < 0 || texCount > 16) return null;
                for (int t = 0; t < texCount; t++)
                {
                    // Length-prefixed here, not null-terminated.
                    int len = RdI32(b, ref p);
                    if (len < 0 || p + len > b.Length) return null;
                    string s = System.Text.Encoding.ASCII.GetString(b, p, len);
                    if (t == 0) d.Texture = s;
                    p += len;
                }
                d.ScaleX = RdF32(b, ref p); d.ScaleY = RdF32(b, ref p); d.ScaleZ = RdF32(b, ref p);
                d.X = RdF32(b, ref p); d.Y = RdF32(b, ref p); d.Z = RdF32(b, ref p);
                d.RotX = RdF32(b, ref p); d.RotY = RdF32(b, ref p); d.RotZ = RdF32(b, ref p);
                d.CutOffLod = RdF32(b, ref p);
                p += 4;                                          // nearCutOffLOD
                p += 4;                                          // ownerArmy
                c.Decals.Add(d);
            }

            int groupCount = RdI32(b, ref p);
            if (groupCount < 0 || groupCount > 200000) return null;
            for (int i = 0; i < groupCount; i++)
            {
                RdI32(b, ref p);                                 // id
                SkipStrZ(b, ref p);                              // name
                int len = RdI32(b, ref p);
                if (len < 0 || len > 1000000) return null;
                p += len * 4;
            }

            // ---- the image block ----
            // Each image is a length-prefixed blob. The normal map is first and
            // is declared with a count that the format requires to be 1.
            p += 8;                                              // width, height
            int normalCount = RdI32(b, ref p);
            if (normalCount != 1) return null;
            p = SkipBlob(b, p); if (p < 0) return null;          // normal map

            c.TexMaskLowOffset = p + 4; c.TexMaskLowLength = PeekBlobLen(b, p);
            p = SkipBlob(b, p); if (p < 0) return null;          // texture mask low
            c.TexMaskHighOffset = p + 4; c.TexMaskHighLength = PeekBlobLen(b, p);
            p = SkipBlob(b, p); if (p < 0) return null;          // texture mask high

            p = SkipBlob(b, p); if (p < 0) return null;          // water map
            // Foam, flatness, depth bias and terrain type are plain byte arrays
            // at quarter resolution, each length-prefixed the same way.
            for (int i = 0; i < 4; i++) { p = SkipBlob(b, p); if (p < 0) return null; }

            // ---- skybox, version 60 and up ----
            if (versionMinor >= 60)
            {
                p += 4 * 3;                                      // position
                p += 4;                                          // horizonHeight
                p += 4;                                          // scale
                p += 4;                                          // subHeight
                p += 4;                                          // subDivAx
                p += 4;                                          // subDivHeight
                p += 4;                                          // zenithHeight
                p += 12;                                         // horizonColor
                p += 12;                                         // zenithColor
                p += 4;                                          // decalGlowMultiplier
                SkipStrZ(b, ref p);                              // albedo
                SkipStrZ(b, ref p);                              // glow
                int planetCount = RdI32(b, ref p);
                if (planetCount < 0 || planetCount > 4096) return null;
                for (int i = 0; i < planetCount; i++) { p += 12; p += 8; p += 8; p += 16; }
                p += 4;                                          // midRgbColor
                int cirrusCount = RdI32(b, ref p);
                if (cirrusCount < 0 || cirrusCount > 4096) return null;
                p += 4;                                          // cirrusMultiplier
                for (int i = 0; i < cirrusCount; i++) { p += 8; p += 4; SkipStrZ(b, ref p); }
            }

            // ---- props ----
            int propCount = RdI32(b, ref p);
            if (propCount < 0 || propCount > 1000000) return null;
            for (int i = 0; i < propCount; i++)
            {
                var pr = new ScProp();
                pr.Blueprint = RdStrZ(b, ref p);
                pr.X = RdF32(b, ref p); pr.Y = RdF32(b, ref p); pr.Z = RdF32(b, ref p);
                // Rotation is three basis vectors. Only the x and z axes carry
                // yaw, which is all a ground prop needs.
                pr.RotXx = RdF32(b, ref p); pr.RotXy = RdF32(b, ref p); pr.RotXz = RdF32(b, ref p);
                p += 12;                                         // y basis
                pr.RotZx = RdF32(b, ref p); pr.RotZy = RdF32(b, ref p); pr.RotZz = RdF32(b, ref p);
                pr.ScaleX = RdF32(b, ref p); pr.ScaleY = RdF32(b, ref p); pr.ScaleZ = RdF32(b, ref p);
                if (p > b.Length) return null;
                c.Props.Add(pr);
            }

            return c;
        }
        catch (IndexOutOfRangeException) { return null; }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    static int PeekBlobLen(byte[] b, int p)
    {
        if (p + 4 > b.Length) return -1;
        return b[p] | b[p + 1] << 8 | b[p + 2] << 16 | b[p + 3] << 24;
    }

    static int SkipBlob(byte[] b, int p)
    {
        int len = PeekBlobLen(b, p);
        if (len < 0 || p + 4 + len > b.Length) return -1;
        return p + 4 + len;
    }
}
