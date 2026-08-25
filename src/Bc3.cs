// DXT5 output, for textures that need a real alpha channel.
//
// Sanctuary's stratum _mask is Unity HDRP's mask map. The engine binary names
// the channels outright - _MaskmapMetal, _MaskmapAO, _MaskmapSmoothness,
// alongside _MetallicScale, _AORemapMin/Max and _SmoothnessRemapMin/Max - so
// the layout is the documented HDRP one:
//
//     R = metallic     G = ambient occlusion     B = detail mask     A = smoothness
//
// Smoothness living in alpha is why a mask has to be DXT5 and not DXT1. It is
// also why every converted map looked wet: the shared placeholder was opaque,
// and alpha 255 is a mirror.
//
// BC3 is BC1's colour block with an 8-byte alpha block in front, so the colour
// half is the encoder already written for albedo and only the alpha half is new.
public static partial class MapGen
{
    /// Eight bytes: two endpoints then sixteen 3-bit indices. Endpoints go at
    /// the block's own max and min so the eight representable values span
    /// exactly the range present.
    static void EncodeBc3AlphaBlock(byte[] px, int w, int h, int bx, int by, byte[] outp, int o)
    {
        var a = new int[16];
        int lo = 255, hi = 0;
        for (int y = 0, n = 0; y < 4; y++)
        {
            int sy = Math.Min(by * 4 + y, h - 1);
            for (int x = 0; x < 4; x++, n++)
            {
                int v = px[(sy * w + Math.Min(bx * 4 + x, w - 1)) * 4 + 3];
                a[n] = v;
                if (v < lo) lo = v;
                if (v > hi) hi = v;
            }
        }

        outp[o] = (byte)hi; outp[o + 1] = (byte)lo;
        for (int i = 2; i < 8; i++) outp[o + i] = 0;
        if (hi == lo) return;                       // flat block: every index 0

        ulong bits = 0;
        for (int i = 0; i < 16; i++)
        {
            int best = 0, bestErr = int.MaxValue;
            for (int k = 0; k < 8; k++)
            {
                int val = k == 0 ? hi : k == 1 ? lo : ((8 - k) * hi + (k - 1) * lo) / 7;
                int err = a[i] - val; if (err < 0) err = -err;
                if (err < bestErr) { bestErr = err; best = k; }
            }
            bits |= (ulong)best << (i * 3);
        }
        for (int i = 0; i < 6; i++) outp[o + 2 + i] = (byte)(bits >> (i * 8));
    }

    /// Encode BGRA pixels as a DXT5 DDS with a full mip chain.
    public static byte[] WriteDxt5Dds(byte[] bgra, int w, int h)
    {
        var levels = new List<byte[]>();
        byte[] cur = bgra; int cw = w, ch = h;
        while (true)
        {
            int bx = (cw + 3) / 4, by = (ch + 3) / 4;
            var enc = new byte[bx * by * 16];
            for (int y = 0; y < by; y++)
                for (int x = 0; x < bx; x++)
                {
                    int o = (y * bx + x) * 16;
                    EncodeBc3AlphaBlock(cur, cw, ch, x, y, enc, o);
                    EncodeBc1Block(cur, cw, ch, x, y, enc, o + 8);
                }
            levels.Add(enc);
            if (cw == 1 && ch == 1) break;
            int nw, nh;
            cur = HalveRgba(cur, cw, ch, out nw, out nh);
            cw = nw; ch = nh;
        }

        int total = 128; foreach (var l in levels) total += l.Length;
        var dds = new byte[total];
        dds[0] = 0x44; dds[1] = 0x44; dds[2] = 0x53; dds[3] = 0x20;
        PutI(dds, 4, 124);
        PutI(dds, 8, 0x1 | 0x2 | 0x4 | 0x1000 | 0x20000 | 0x80000);
        PutI(dds, 12, h); PutI(dds, 16, w);
        PutI(dds, 20, levels[0].Length);
        PutI(dds, 28, levels.Count);
        PutI(dds, 76, 32);
        PutI(dds, 80, 0x4);
        dds[84] = (byte)'D'; dds[85] = (byte)'X'; dds[86] = (byte)'T'; dds[87] = (byte)'5';
        PutI(dds, 108, 0x1000 | 0x8 | 0x400000);
        int p = 128;
        foreach (var l in levels) { Buffer.BlockCopy(l, 0, dds, p, l.Length); p += l.Length; }
        return dds;
    }
}
