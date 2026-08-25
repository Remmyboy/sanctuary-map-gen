// DXT3 is a format Sanctuary cannot load.
//
// Unity has TextureFormat.DXT1 and TextureFormat.DXT5 and nothing for BC2, so
// a DXT3 texture arrives as no texture at all - which reads on screen as a
// clean white surface, not as an error. Seton's Clutch spent two rounds
// looking like snow because evgrass005_albedo.dds, carrying 80% of the ground
// across two layers, is DXT3.
//
// The tally says this is worth handling rather than skipping: Sanctuary ships
// zero DXT3 across 470 textures, Supreme Commander has 221 of them out of
// 2,462. Roughly one texture in eleven over there is unloadable over here.
//
// The transcode is close to free. Both formats are 16 bytes per 4x4 block and
// both put an 8-byte alpha block first and the same 8-byte colour block
// second, so the colour data - all of it, every mip - copies bit for bit and
// only the alpha block is rebuilt. Because the layout matches, the whole file
// walks as 16-byte strides from the end of the header without needing to know
// where one mip ends and the next begins.
public static partial class MapGen
{
    /// True if this DDS declares DXT3/BC2.
    public static bool IsDxt3(byte[] dds)
    {
        return dds != null && dds.Length >= 88 &&
               dds[0] == 0x44 && dds[1] == 0x44 && dds[2] == 0x53 && dds[3] == 0x20 &&
               (BitConverter.ToInt32(dds, 80) & 0x4) != 0 &&
               dds[84] == (byte)'D' && dds[85] == (byte)'X' && dds[86] == (byte)'T' && dds[87] == (byte)'3';
    }

    /// Rewrite a DXT3 DDS as DXT5 in place. Returns false and leaves the buffer
    /// untouched if it is not DXT3 or the block data is not a whole number of
    /// blocks, so a surprising file falls through rather than being corrupted.
    public static bool TranscodeDxt3ToDxt5(byte[] dds)
    {
        if (!IsDxt3(dds)) return false;
        int start = 128;
        int len = dds.Length - start;
        if (len <= 0 || len % 16 != 0) return false;

        var a8 = new int[16];
        for (int p = start; p < dds.Length; p += 16)
        {
            // DXT3 alpha: sixteen 4-bit values, two per byte, low nibble first.
            // Replicate the nibble rather than shifting, so 15 becomes 255 and
            // not 240 - an opaque texel has to stay opaque.
            int lo = 255, hi = 0;
            for (int i = 0; i < 8; i++)
            {
                int by = dds[p + i];
                int v0 = by & 0x0f, v1 = (by >> 4) & 0x0f;
                int e0 = (v0 << 4) | v0, e1 = (v1 << 4) | v1;
                a8[i * 2] = e0; a8[i * 2 + 1] = e1;
                if (e0 < lo) lo = e0; if (e0 > hi) hi = e0;
                if (e1 < lo) lo = e1; if (e1 > hi) hi = e1;
            }

            // DXT5 alpha: two endpoints then sixteen 3-bit indices. With
            // a0 > a1 the six interior values are evenly spaced between them,
            // so putting the endpoints at the block's own max and min spans
            // exactly the range present. The source has only sixteen distinct
            // levels to begin with, so eight well-placed ones lose very little.
            dds[p] = (byte)hi;
            dds[p + 1] = (byte)lo;
            for (int i = 2; i < 8; i++) dds[p + i] = 0;

            if (hi == lo) continue;                 // flat block: all index 0

            ulong bits = 0;
            for (int i = 0; i < 16; i++)
            {
                // Nearest of the eight representable values.
                int best = 0, bestErr = int.MaxValue;
                for (int k = 0; k < 8; k++)
                {
                    int val = k == 0 ? hi : k == 1 ? lo : ((8 - k) * hi + (k - 1) * lo) / 7;
                    int err = a8[i] - val; if (err < 0) err = -err;
                    if (err < bestErr) { bestErr = err; best = k; }
                }
                bits |= (ulong)best << (i * 3);
            }
            for (int i = 0; i < 6; i++) dds[p + 2 + i] = (byte)(bits >> (i * 8));
        }

        dds[87] = (byte)'5';
        return true;
    }
}
