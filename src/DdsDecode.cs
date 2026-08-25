// Full per-pixel decode of the DDS formats the corpus uses.
//
// The mean-colour reader in DdsMean.cs was enough to match tone, and tone
// matching turned out not to be enough: a substitute can land on the exact
// average colour and still read as a different ground, because character
// lives in the variance - grain size, contrast, hue spread. Judging that
// needs the actual pixels on the actual screen, which needs a real decoder.
//
// DXT1 and DXT3/5 colour blocks share one layout; DXT5 alpha is the two
// endpoint + 3-bit index scheme. Uncompressed 32/24-bit passes through.
// BC7 is not handled - none of the textures this is used on are BC7.
public static partial class MapGen
{
    /// Decode mip 0 to BGRA. Null when the format is not handled.
    public static byte[] DecodeDdsToBgra(byte[] b, out int w, out int h)
    {
        w = 0; h = 0;
        if (b == null || b.Length < 128) return null;
        if (b[0] != 0x44 || b[1] != 0x44 || b[2] != 0x53 || b[3] != 0x20) return null;
        h = BitConverter.ToInt32(b, 12);
        w = BitConverter.ToInt32(b, 16);
        if (w <= 0 || h <= 0 || w > 8192 || h > 8192) return null;
        int pfFlags = BitConverter.ToInt32(b, 80);
        string fourcc = System.Text.Encoding.ASCII.GetString(b, 84, 4);
        var outp = new byte[w * h * 4];

        if ((pfFlags & 0x4) == 0)
        {
            int bpp = BitConverter.ToInt32(b, 88);
            if (bpp != 32 && bpp != 24) return null;
            int step = bpp / 8;
            if (128 + w * h * step > b.Length) return null;
            for (int i = 0; i < w * h; i++)
            {
                int o = 128 + i * step;
                outp[i * 4] = b[o]; outp[i * 4 + 1] = b[o + 1]; outp[i * 4 + 2] = b[o + 2];
                outp[i * 4 + 3] = step == 4 ? b[o + 3] : (byte)255;
            }
            return outp;
        }

        bool dxt1 = fourcc == "DXT1";
        bool dxt45 = fourcc == "DXT5" || fourcc == "DXT4";
        bool dxt23 = fourcc == "DXT3" || fourcc == "DXT2";
        if (!dxt1 && !dxt45 && !dxt23) return null;

        int bx = (w + 3) / 4, by = (h + 3) / 4;
        int stride = dxt1 ? 8 : 16;
        if (128 + bx * by * stride > b.Length) return null;

        var pr = new int[4]; var pg = new int[4]; var pb = new int[4];
        for (int yb = 0; yb < by; yb++)
        {
            for (int xb = 0; xb < bx; xb++)
            {
                int p = 128 + (yb * bx + xb) * stride;
                int cp = dxt1 ? p : p + 8;

                int c0 = b[cp] | (b[cp + 1] << 8);
                int c1 = b[cp + 2] | (b[cp + 3] << 8);
                for (int i = 0; i < 2; i++)
                {
                    int c = i == 0 ? c0 : c1;
                    int r5 = (c >> 11) & 0x1f, g6 = (c >> 5) & 0x3f, b5 = c & 0x1f;
                    pr[i] = (r5 << 3) | (r5 >> 2); pg[i] = (g6 << 2) | (g6 >> 4); pb[i] = (b5 << 3) | (b5 >> 2);
                }
                bool four = !dxt1 || c0 > c1;
                if (four)
                {
                    pr[2] = (2 * pr[0] + pr[1]) / 3; pg[2] = (2 * pg[0] + pg[1]) / 3; pb[2] = (2 * pb[0] + pb[1]) / 3;
                    pr[3] = (pr[0] + 2 * pr[1]) / 3; pg[3] = (pg[0] + 2 * pg[1]) / 3; pb[3] = (pb[0] + 2 * pb[1]) / 3;
                }
                else
                {
                    pr[2] = (pr[0] + pr[1]) / 2; pg[2] = (pg[0] + pg[1]) / 2; pb[2] = (pb[0] + pb[1]) / 2;
                    pr[3] = 0; pg[3] = 0; pb[3] = 0;
                }
                uint idx = BitConverter.ToUInt32(b, cp + 4);

                // DXT5 alpha block, decoded per texel.
                ulong abits = 0; int a0 = 255, a1 = 255;
                if (dxt45)
                {
                    a0 = b[p]; a1 = b[p + 1];
                    for (int i = 0; i < 6; i++) abits |= (ulong)b[p + 2 + i] << (i * 8);
                }

                for (int t = 0; t < 16; t++)
                {
                    int x = xb * 4 + (t & 3), y = yb * 4 + (t >> 2);
                    if (x >= w || y >= h) continue;
                    int k = (int)((idx >> (t * 2)) & 3);
                    int a = 255;
                    if (dxt1 && !four && k == 3) a = 0;
                    if (dxt23) a = ((b[p + t / 2] >> ((t & 1) * 4)) & 0xf) * 17;
                    if (dxt45)
                    {
                        int ai = (int)((abits >> (t * 3)) & 7);
                        a = ai == 0 ? a0 : ai == 1 ? a1
                            : a0 > a1 ? ((8 - ai) * a0 + (ai - 1) * a1) / 7
                            : ai < 6 ? ((6 - ai) * a0 + (ai - 1) * a1) / 5
                            : ai == 6 ? 0 : 255;
                    }
                    int o = (y * w + x) * 4;
                    outp[o] = (byte)pb[k]; outp[o + 1] = (byte)pg[k]; outp[o + 2] = (byte)pr[k]; outp[o + 3] = (byte)a;
                }
            }
        }
        return outp;
    }
}
