// Enough BC7 to read a texture's average colour.
//
// The stratum albedos are half DXT1 and half BC7 (DDS with a DX10 header and
// dxgiFormat 98). DXT1 stores two RGB565 endpoints at a fixed offset, so
// averaging them is trivial and that is what the tone table used. BC7 has eight
// block modes with different subset counts, endpoint precisions and p-bit
// arrangements, none of it at a fixed offset - so the same trick read garbage
// and every BC7 texture came back at roughly the same wrong number, near 122.
//
// That mattered: the tone table decides each layer's diffuseRemap, and five of
// the textures in the biome tables were falling back to a guess.
//
// This decodes endpoints only. Per-pixel decode would need the partition
// tables, the index bits and the interpolation weights; the mean of a block's
// endpoints is a good estimate of the mean of its pixels, and a texture-wide
// average of those is all a tone comparison needs.
public static class Bc7
{
    // Per mode: subsets, partition bits, rotation bits, index-selection bits,
    // colour bits, alpha bits, per-endpoint p-bits, shared p-bits.
    static readonly int[] NS  = { 3, 2, 3, 2, 1, 1, 1, 2 };
    static readonly int[] PB  = { 4, 6, 6, 6, 0, 0, 0, 6 };
    static readonly int[] RB  = { 0, 0, 0, 0, 2, 2, 0, 0 };
    static readonly int[] ISB = { 0, 0, 0, 0, 1, 0, 0, 0 };
    static readonly int[] CB  = { 4, 6, 5, 7, 5, 7, 7, 5 };
    static readonly int[] AB  = { 0, 0, 0, 0, 6, 8, 7, 5 };
    static readonly int[] EPB = { 1, 0, 0, 1, 0, 0, 1, 1 };
    static readonly int[] SPB = { 0, 1, 0, 0, 0, 0, 0, 0 };

    /// Reads a bit stream LSB-first across a 16-byte block.
    class Bits
    {
        readonly byte[] b; readonly int off; int p;
        public Bits(byte[] data, int offset) { b = data; off = offset; p = 0; }
        public int Read(int n)
        {
            int v = 0;
            for (int i = 0; i < n; i++)
            {
                int bit = (b[off + (p >> 3)] >> (p & 7)) & 1;
                v |= bit << i;
                p++;
            }
            return v;
        }
    }

    /// Mean R, G, B of one block's endpoints, or false if the block is invalid.
    public static bool BlockMean(byte[] data, int offset, out float r, out float g, out float bl)
    {
        r = g = bl = 0f;

        // The mode is a unary prefix: the number of zero bits before the first
        // one. All-zero in the low byte is not a valid block.
        int mode = -1;
        for (int i = 0; i < 8; i++)
            if (((data[offset] >> i) & 1) != 0) { mode = i; break; }
        if (mode < 0) return false;

        var bits = new Bits(data, offset);
        bits.Read(mode + 1);                       // consume the mode prefix

        if (PB[mode] > 0)  bits.Read(PB[mode]);    // partition
        if (RB[mode] > 0)  bits.Read(RB[mode]);    // rotation
        if (ISB[mode] > 0) bits.Read(ISB[mode]);   // index selection

        int ep = NS[mode] * 2;
        var comp = new int[3][];
        for (int c = 0; c < 3; c++)
        {
            comp[c] = new int[ep];
            for (int e = 0; e < ep; e++) comp[c][e] = bits.Read(CB[mode]);
        }
        if (AB[mode] > 0)
            for (int e = 0; e < ep; e++) bits.Read(AB[mode]);   // alpha, unused here

        // p-bits extend each endpoint by one low bit: one per endpoint, or one
        // per subset shared by its pair.
        var pbit = new int[ep];
        bool hasP = false;
        if (EPB[mode] > 0)
        {
            hasP = true;
            for (int e = 0; e < ep; e++) pbit[e] = bits.Read(1);
        }
        else if (SPB[mode] > 0)
        {
            hasP = true;
            for (int s = 0; s < NS[mode]; s++)
            {
                int v = bits.Read(1);
                pbit[s * 2] = v; pbit[s * 2 + 1] = v;
            }
        }

        int prec = CB[mode] + (hasP ? 1 : 0);
        float sr = 0f, sg = 0f, sb = 0f;
        for (int e = 0; e < ep; e++)
        {
            sr += Expand(comp[0][e], pbit[e], hasP, prec);
            sg += Expand(comp[1][e], pbit[e], hasP, prec);
            sb += Expand(comp[2][e], pbit[e], hasP, prec);
        }
        r = sr / ep; g = sg / ep; bl = sb / ep;
        return true;
    }

    /// Endpoint component to 0..255: append the p-bit, then replicate the high
    /// bits into the low ones, which is how BC7 defines the expansion.
    static float Expand(int v, int p, bool hasP, int prec)
    {
        if (hasP) v = (v << 1) | p;
        if (prec >= 8) return v & 0xff;
        int shifted = v << (8 - prec);
        return shifted | (shifted >> prec);
    }

    /// Mean colour of a whole BC7 surface. `start` is the first byte of pixel
    /// data - 148 for a DDS with a DX10 header.
    public static bool SurfaceMean(byte[] dds, int start, int maxBlocks,
                                   out float r, out float g, out float b)
    {
        double sr = 0, sg = 0, sb = 0; int n = 0;
        for (int i = start; i + 16 <= dds.Length && n < maxBlocks; i += 16)
        {
            float br, bg, bb;
            if (!BlockMean(dds, i, out br, out bg, out bb)) continue;
            sr += br; sg += bg; sb += bb; n++;
        }
        r = g = b = 0f;
        if (n == 0) return false;
        r = (float)(sr / n); g = (float)(sg / n); b = (float)(sb / n);
        return true;
    }
}
