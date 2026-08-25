// Mean surface colour of a DDS, whatever it is compressed with.
//
// Needed to replace Supreme Commander's textures with our own: a substitute
// only reads as the same ground if it lands on the same tone, and the biome
// path already has the machinery for that - Get-DiffuseRemap is
// targetTone / measuredLuminance. What was missing is the measurement, for
// formats other than BC7.
//
// The three DXT variants share one colour block: two RGB565 endpoints and
// sixteen 2-bit indices. BC1 puts it first and switches to a 3-colour mode
// when the endpoints are ordered the other way; BC2 and BC3 put an 8-byte
// alpha block in front of it and always use 4 colours. So one decoder covers
// all three, given the right offset and mode rule.
public static partial class MapGen
{
    public struct DdsInfo
    {
        public bool Ok;
        public string Format;
        public int Width, Height, Mips;
        public double R, G, B;      // 0-255 mean of mip 0
        public double Luma;         // Rec. 601
    }

    static void Bc1Block(byte[] b, int p, bool opaqueOnly, double[] acc, ref int n)
    {
        int c0 = b[p] | (b[p + 1] << 8);
        int c1 = b[p + 2] | (b[p + 3] << 8);
        var r = new int[4]; var g = new int[4]; var bl = new int[4];
        for (int i = 0; i < 2; i++)
        {
            int c = i == 0 ? c0 : c1;
            // 5-6-5 to 8-8-8 by bit replication, so 31 reaches 255.
            int r5 = (c >> 11) & 0x1f, g6 = (c >> 5) & 0x3f, b5 = c & 0x1f;
            r[i] = (r5 << 3) | (r5 >> 2);
            g[i] = (g6 << 2) | (g6 >> 4);
            bl[i] = (b5 << 3) | (b5 >> 2);
        }
        bool four = opaqueOnly || c0 > c1;
        if (four)
        {
            for (int k = 0; k < 3; k++)
            {
                r[2] = (2 * r[0] + r[1]) / 3; g[2] = (2 * g[0] + g[1]) / 3; bl[2] = (2 * bl[0] + bl[1]) / 3;
                r[3] = (r[0] + 2 * r[1]) / 3; g[3] = (g[0] + 2 * g[1]) / 3; bl[3] = (bl[0] + 2 * bl[1]) / 3;
            }
        }
        else
        {
            r[2] = (r[0] + r[1]) / 2; g[2] = (g[0] + g[1]) / 2; bl[2] = (bl[0] + bl[1]) / 2;
            r[3] = 0; g[3] = 0; bl[3] = 0;
        }
        uint idx = BitConverter.ToUInt32(b, p + 4);
        for (int i = 0; i < 16; i++)
        {
            int k = (int)((idx >> (i * 2)) & 3);
            if (!four && k == 3) continue;          // punch-through texel
            acc[0] += r[k]; acc[1] += g[k]; acc[2] += bl[k]; n++;
        }
    }

    /// Header fields plus the mean colour of mip 0.
    public static DdsInfo ReadDdsInfo(byte[] b)
    {
        var d = new DdsInfo();
        if (b == null || b.Length < 148) return d;
        if (b[0] != 0x44 || b[1] != 0x44 || b[2] != 0x53 || b[3] != 0x20) return d;

        d.Height = BitConverter.ToInt32(b, 12);
        d.Width = BitConverter.ToInt32(b, 16);
        d.Mips = BitConverter.ToInt32(b, 28);
        int pfFlags = BitConverter.ToInt32(b, 80);
        string fourcc = System.Text.Encoding.ASCII.GetString(b, 84, 4);
        bool compressed = (pfFlags & 0x4) != 0;
        d.Format = compressed ? fourcc : "uncompressed";
        if (d.Width <= 0 || d.Height <= 0) return d;

        int bx = (d.Width + 3) / 4, by = (d.Height + 3) / 4;
        int blocks = bx * by;
        var acc = new double[3];
        int n = 0;
        int start = 128;

        if (!compressed)
        {
            int bpp = BitConverter.ToInt32(b, 88);
            if (bpp != 32 && bpp != 24) return d;
            int step = bpp / 8;
            int need = d.Width * d.Height * step;
            if (start + need > b.Length) return d;
            for (int i = 0; i < d.Width * d.Height; i++)
            {
                int o = start + i * step;
                acc[0] += b[o + 2]; acc[1] += b[o + 1]; acc[2] += b[o]; n++;   // BGRA
            }
        }
        else if (fourcc == "DXT1")
        {
            if (start + blocks * 8 > b.Length) return d;
            for (int i = 0; i < blocks; i++) Bc1Block(b, start + i * 8, false, acc, ref n);
        }
        else if (fourcc == "DXT3" || fourcc == "DXT5" || fourcc == "DXT2" || fourcc == "DXT4")
        {
            if (start + blocks * 16 > b.Length) return d;
            // Colour block sits after the 8-byte alpha block, always 4-colour.
            for (int i = 0; i < blocks; i++) Bc1Block(b, start + i * 16 + 8, true, acc, ref n);
        }
        else if (fourcc == "DX10")
        {
            float fr, fg, fb;
            if (!Bc7.SurfaceMean(b, 148, blocks, out fr, out fg, out fb)) return d;
            acc[0] = fr; acc[1] = fg; acc[2] = fb; n = 1;
        }
        else return d;

        if (n == 0) return d;
        d.R = acc[0] / n; d.G = acc[1] / n; d.B = acc[2] / n;
        d.Luma = 0.299 * d.R + 0.587 * d.G + 0.114 * d.B;
        d.Ok = true;
        return d;
    }
}
