// Bringing a Supreme Commander map's own splat weights across.
//
// This turned out to be much less work than planned. The two texture masks are
// not compressed: pixel-format flags 0x41 mean RGB plus alpha, 32 bits per
// pixel, channel masks R=0x00ff0000 G=0x0000ff00 B=0x000000ff A=0xff000000 -
// which is BGRA in memory, exactly what a Sanctuary stratum TGA stores. Only
// the preview and the water map are DXT5, and we need neither. The plan
// budgeted a whole milestone for a DXT5 decoder that is not required.
//
// So the conversion is a resample and a reorientation:
//
//   * Supreme Commander mask row 0 is world z 0, and the import negates z, so
//     Sanctuary's row 0 - which is world z max - reads Supreme Commander row 0.
//   * mask resolution varies (mapSize/2 on most stock maps, full mapSize on
//     many community ones, 1024 on a 2048 map), so it has to be resampled to
//     heightmapResolution rather than copied.
//
// Channel meaning matches: low mask RGBA is layers 1-4, high mask RGBA is
// layers 5-8, and Sanctuary numbers its stratum layers the same way.
public static partial class MapGen
{
    /// Bilinear sample of one channel of an uncompressed BGRA DDS.
    /// `u` and `v` are in texels.
    static float SampleMask(byte[] b, int dataStart, int size, int channel, float u, float v)
    {
        if (u < 0) u = 0; if (v < 0) v = 0;
        float maxc = size - 1.0001f;
        if (u > maxc) u = maxc;
        if (v > maxc) v = maxc;

        int x0 = (int)u, y0 = (int)v;
        int x1 = Math.Min(x0 + 1, size - 1), y1 = Math.Min(y0 + 1, size - 1);
        float fx = u - x0, fy = v - y0;

        // BGRA: channel 0 is layer 1 (red), 1 is green, 2 is blue, 3 is alpha.
        // Red sits at byte offset 2, green 1, blue 0, alpha 3.
        int off = channel == 0 ? 2 : channel == 1 ? 1 : channel == 2 ? 0 : 3;

        int i00 = dataStart + (y0 * size + x0) * 4 + off;
        int i10 = dataStart + (y0 * size + x1) * 4 + off;
        int i01 = dataStart + (y1 * size + x0) * 4 + off;
        int i11 = dataStart + (y1 * size + x1) * 4 + off;
        if (i11 >= b.Length) return 0f;

        float top = b[i00] + (b[i10] - b[i00]) * fx;
        float bot = b[i01] + (b[i11] - b[i01]) * fx;
        return top + (bot - top) * fy;
    }

    /// Fill MapGen.Layers from the map's own splat, replacing whatever
    /// BuildLayers would have painted. Requires Configure/AdoptScMap to have
    /// set MapSize, HRes and SRes first.
    ///
    /// Returns false if the masks are not the uncompressed form this handles,
    /// so the caller can fall back to generated stratum weights.
    public static bool AdoptScSplat(byte[] b, ScTextureSet set)
    {
        if (set == null || set.MaskSize <= 0) return false;

        // Both masks must be uncompressed 32-bit, and long enough to hold a
        // full surface after the 128-byte header.
        foreach (int o in new[] { set.MaskLowOffset, set.MaskHighOffset })
        {
            int pfFlags = BitConverter.ToInt32(b, o + 80);
            int bpp = BitConverter.ToInt32(b, o + 88);
            if ((pfFlags & 0x40) == 0 || bpp != 32) return false;
        }
        int lowData = set.MaskLowOffset + 128;
        int highData = set.MaskHighOffset + 128;
        int need = set.MaskSize * set.MaskSize * 4;
        if (lowData + need > b.Length || highData + need > b.Length) return false;

        Layers = new byte[9][,];
        for (int i = 1; i <= 8; i++) Layers[i] = new byte[SRes, SRes];

        // One Sanctuary splat texel steps this many mask texels. SRes is
        // vertex-aligned to the heightmap, hence SRes - 1.
        float k = (float)set.MaskSize / (SRes - 1);

        for (int r = 0; r < SRes; r++)
        {
            // Sanctuary row r is world z = MapSize - r*step; the import negates
            // z, so that is Supreme Commander z = r*step, and the mask row
            // follows directly.
            float v = r * k;
            for (int c = 0; c < SRes; c++)
            {
                float u = c * k;
                for (int ch = 0; ch < 4; ch++)
                {
                    Layers[1 + ch][r, c] = (byte)Math.Round(SampleMask(b, lowData, set.MaskSize, ch, u, v));
                    Layers[5 + ch][r, c] = (byte)Math.Round(SampleMask(b, highData, set.MaskSize, ch, u, v));
                }
            }
        }

        // A mask channel only means anything when the matching layer has a
        // texture. Supreme Commander never reads the others, so an author is
        // free to leave them holding junk - SCMP_016 ships stratum2 as a
        // byte-for-byte copy of stratum1, which is harmless there because
        // layers 5-8 are unassigned.
        //
        // It is not harmless here. A Sanctuary slot with weight still gets
        // drawn, and an unassigned slot has no albedo but the shared
        // placeholder, so the copied weights painted a flat grey wash over
        // almost the whole map. Drop the weight where the source has no
        // texture for that layer.
        DroppedLayers = 0;
        for (int i = 1; i <= 8; i++)
        {
            bool assigned = set.Paths != null && i < set.Paths.Length &&
                            !string.IsNullOrWhiteSpace(set.Paths[i]);
            if (assigned) continue;
            Array.Clear(Layers[i], 0, Layers[i].Length);
            DroppedLayers++;
        }
        return true;
    }

    /// How many of the eight stratum layers were zeroed because the source map
    /// assigned them no texture. Reported by the converter so a mask that came
    /// across empty is visible rather than inferred from the picture.
    public static int DroppedLayers;

    /// Coverage of each layer after adoption, as a fraction of the map. Used to
    /// report what actually came across and to spot a mask read that produced
    /// nothing.
    public static float[] SplatCoverage()
    {
        var outp = new float[9];
        if (Layers == null) return outp;
        float total = SRes * (float)SRes;
        for (int i = 1; i <= 8; i++)
        {
            if (Layers[i] == null) continue;
            int n = 0;
            for (int r = 0; r < SRes; r++)
                for (int c = 0; c < SRes; c++)
                    if (Layers[i][r, c] > 32) n++;
            outp[i] = n / total;
        }
        return outp;
    }
}
