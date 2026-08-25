// Writing DDS that Sanctuary can load.
//
// Bringing in outside textures means producing the format the game already
// reads rather than hoping it copes: DXT1 with a full mip chain, which is what
// 209 of its 470 shipped textures are. Uncompressed would be 16 MB a texture
// and PNG decodes to RGBA32 in memory, so neither survives nine layers.
//
// Mips are not optional. Ground tiles at four to ten metres a repeat, so a
// 512 m map shows a texture upwards of sixty times across; with no mip chain
// that is pure aliasing.
//
// The BC1 encoder is a bounding-box fit with least-squares refinement. Cluster
// fit would be better and much slower, and the source is photographed ground -
// noisy and low-contrast within any one block - which is the case bounding box
// already handles well.
public static partial class MapGen
{
    /// Box-filter one BGRA level down.
    static byte[] HalveRgba(byte[] src, int w, int h, out int nw, out int nh)
    {
        nw = Math.Max(1, w / 2); nh = Math.Max(1, h / 2);
        var dst = new byte[nw * nh * 4];
        for (int y = 0; y < nh; y++)
        {
            int y0 = Math.Min(y * 2, h - 1), y1 = Math.Min(y * 2 + 1, h - 1);
            for (int x = 0; x < nw; x++)
            {
                int x0 = Math.Min(x * 2, w - 1), x1 = Math.Min(x * 2 + 1, w - 1);
                for (int c = 0; c < 4; c++)
                    dst[(y * nw + x) * 4 + c] = (byte)((src[(y0 * w + x0) * 4 + c] + src[(y0 * w + x1) * 4 + c]
                                                      + src[(y1 * w + x0) * 4 + c] + src[(y1 * w + x1) * 4 + c]) / 4);
            }
        }
        return dst;
    }

    static int To565(int r, int g, int b) { return ((r >> 3) << 11) | ((g >> 2) << 5) | (b >> 3); }

    static void From565(int c, out int r, out int g, out int b)
    {
        int r5 = (c >> 11) & 0x1f, g6 = (c >> 5) & 0x3f, b5 = c & 0x1f;
        r = (r5 << 3) | (r5 >> 2); g = (g6 << 2) | (g6 >> 4); b = (b5 << 3) | (b5 >> 2);
    }

    static int Clamp255(double v) { return v < 0 ? 0 : v > 255 ? 255 : (int)Math.Round(v); }

    // Palette weight of each index: entry 0 is endpoint A, entry 1 is endpoint
    // B, and 2 and 3 are two thirds and one third of the way between.
    static readonly double[] Bc1Weight = { 1.0, 0.0, 2.0 / 3.0, 1.0 / 3.0 };

    static void EncodeBc1Block(byte[] px, int w, int h, int bx, int by, byte[] outp, int o)
    {
        var r = new int[16]; var g = new int[16]; var b = new int[16];
        for (int y = 0, n = 0; y < 4; y++)
        {
            int sy = Math.Min(by * 4 + y, h - 1);
            for (int x = 0; x < 4; x++, n++)
            {
                int i = (sy * w + Math.Min(bx * 4 + x, w - 1)) * 4;
                r[n] = px[i + 2]; g[n] = px[i + 1]; b[n] = px[i];        // BGRA in
            }
        }

        int rlo = 255, glo = 255, blo = 255, rhi = 0, ghi = 0, bhi = 0;
        for (int i = 0; i < 16; i++)
        {
            if (r[i] < rlo) rlo = r[i]; if (r[i] > rhi) rhi = r[i];
            if (g[i] < glo) glo = g[i]; if (g[i] > ghi) ghi = g[i];
            if (b[i] < blo) blo = b[i]; if (b[i] > bhi) bhi = b[i];
        }
        // Inset by a sixteenth: the interior palette entries sit at thirds, so
        // endpoints pulled slightly in cover the middle better than the literal
        // extremes do.
        int ri = (rhi - rlo) >> 4, gi = (ghi - glo) >> 4, bi = (bhi - blo) >> 4;
        double ar = rhi - ri, ag = ghi - gi, ab = bhi - bi;
        double br = rlo + ri, bg = glo + gi, bb = blo + bi;

        var idx = new int[16];
        int c0 = 0, c1 = 0;

        for (int pass = 0; ; pass++)
        {
            c0 = To565(Clamp255(ar), Clamp255(ag), Clamp255(ab));
            c1 = To565(Clamp255(br), Clamp255(bg), Clamp255(bb));
            // c0 must exceed c1, or the block decodes in the three-colour
            // punch-through mode and a quarter of the palette becomes
            // transparent black.
            if (c0 < c1) { int t = c0; c0 = c1; c1 = t; }

            int p0r, p0g, p0b, p1r, p1g, p1b;
            From565(c0, out p0r, out p0g, out p0b);
            From565(c1, out p1r, out p1g, out p1b);
            var pr = new[] { p0r, p1r, (2 * p0r + p1r) / 3, (p0r + 2 * p1r) / 3 };
            var pg = new[] { p0g, p1g, (2 * p0g + p1g) / 3, (p0g + 2 * p1g) / 3 };
            var pb = new[] { p0b, p1b, (2 * p0b + p1b) / 3, (p0b + 2 * p1b) / 3 };

            for (int i = 0; i < 16; i++)
            {
                int best = 0, bestE = int.MaxValue;
                for (int k = 0; k < 4; k++)
                {
                    int dr = r[i] - pr[k], dg = g[i] - pg[k], db = b[i] - pb[k];
                    int e = dr * dr + dg * dg + db * db;
                    if (e < bestE) { bestE = e; best = k; }
                }
                idx[i] = best;
            }
            if (pass == 1) break;

            // Refit. A palette colour at weight a is a*A + (1-a)*B, so the two
            // endpoints fall out of a 2x2 normal-equation solve:
            //     [saa sab][A]   [pa]
            //     [sab sbb][B] = [pb]
            double saa = 0, sab = 0, sbb = 0;
            double par = 0, pag = 0, pab = 0, pbr = 0, pbg = 0, pbb = 0;
            for (int i = 0; i < 16; i++)
            {
                double a = Bc1Weight[idx[i]], bw = 1.0 - a;
                saa += a * a; sab += a * bw; sbb += bw * bw;
                par += a * r[i]; pag += a * g[i]; pab += a * b[i];
                pbr += bw * r[i]; pbg += bw * g[i]; pbb += bw * b[i];
            }
            double det = saa * sbb - sab * sab;
            if (Math.Abs(det) < 1e-9) break;        // every texel on one entry
            double inv = 1.0 / det;
            ar = (sbb * par - sab * pbr) * inv; br = (saa * pbr - sab * par) * inv;
            ag = (sbb * pag - sab * pbg) * inv; bg = (saa * pbg - sab * pag) * inv;
            ab = (sbb * pab - sab * pbb) * inv; bb = (saa * pbb - sab * pab) * inv;
        }

        outp[o] = (byte)c0; outp[o + 1] = (byte)(c0 >> 8);
        outp[o + 2] = (byte)c1; outp[o + 3] = (byte)(c1 >> 8);
        uint bits = 0;
        for (int i = 0; i < 16; i++) bits |= (uint)idx[i] << (i * 2);
        outp[o + 4] = (byte)bits; outp[o + 5] = (byte)(bits >> 8);
        outp[o + 6] = (byte)(bits >> 16); outp[o + 7] = (byte)(bits >> 24);
    }

    /// Encode BGRA pixels as a DXT1 DDS with a full mip chain.
    public static byte[] WriteDxt1Dds(byte[] bgra, int w, int h)
    {
        var levels = new List<byte[]>();
        byte[] cur = bgra; int cw = w, ch = h;
        while (true)
        {
            int bx = (cw + 3) / 4, by = (ch + 3) / 4;
            var enc = new byte[bx * by * 8];
            for (int y = 0; y < by; y++)
                for (int x = 0; x < bx; x++)
                    EncodeBc1Block(cur, cw, ch, x, y, enc, (y * bx + x) * 8);
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
        dds[84] = (byte)'D'; dds[85] = (byte)'X'; dds[86] = (byte)'T'; dds[87] = (byte)'1';
        PutI(dds, 108, 0x1000 | 0x8 | 0x400000);
        int p = 128;
        foreach (var l in levels) { Buffer.BlockCopy(l, 0, dds, p, l.Length); p += l.Length; }
        return dds;
    }

    static void PutI(byte[] a, int o, int v)
    {
        a[o] = (byte)v; a[o + 1] = (byte)(v >> 8); a[o + 2] = (byte)(v >> 16); a[o + 3] = (byte)(v >> 24);
    }
}
