// Reading the decal table without trusting the full sequential walk.
//
// The first plan anchored on the end of the texture set, on the theory that
// the decal count sits eight bytes past the normals. It does not: the texture
// block carries more entries than the eight-plus-four our scanner models (FA
// has upper and lower strata besides the masked eight), so the anchor landed
// in text. Rather than model the block exactly, anchor the way the prop
// scanner does - on the data we actually want.
//
// A decal record is: id(4) type(4) texCount(4) then texCount length-prefixed
// paths, then scale(12) position(12) rotation(12) cutoff(4) nearCutoff(4)
// ownerArmy(4). Decal texture paths live under a decals/ directory, so every
// plausible path start is a candidate first record; the count sits four bytes
// before the record. A candidate is accepted only if all count records parse
// with sane values and the table ends before the first splat image with a
// plausible group count next - strong enough that a wrong anchor cannot be
// accepted by accident.
public static partial class MapGen
{
    /// Read the decal table. Null when nothing parses; an empty list when the
    /// map has no decals to find.
    public static List<ScDecal> ScanScDecals(byte[] b, ScTextureSet set)
    {
        int lo = set != null && set.AfterNormalsOffset > 0 ? set.AfterNormalsOffset : 0;
        int hi = set != null && set.MaskLowOffset > 0 ? set.MaskLowOffset : b.Length;

        // Candidate anchors: length-prefixed paths mentioning a decals folder.
        var starts = new List<int>();
        for (int i = Math.Max(4, lo); i + 8 < hi; i++)
        {
            if (b[i] != (byte)'/') continue;
            int len = BitConverter.ToInt32(b, i - 4);
            if (len < 8 || len > 512 || i + len > hi) continue;
            string s = System.Text.Encoding.ASCII.GetString(b, i, Math.Min(len, 64)).ToLowerInvariant();
            if (s.Contains("decal")) starts.Add(i);
        }
        if (starts.Count == 0) return new List<ScDecal>();

        foreach (int pathStart in starts)
        {
            // Back out the record header: id, type, texCount, first length.
            int rec = pathStart - 16;
            int cntPos = rec - 4;
            if (cntPos < 0) continue;
            int count = BitConverter.ToInt32(b, cntPos);
            if (count < 1 || count > 200000) continue;

            var outp = TryDecalRun(b, rec, count, hi);
            if (outp != null) return outp;
        }
        return null;
    }

    static List<ScDecal> TryDecalRun(byte[] b, int p, int count, int limit)
    {
        var outp = new List<ScDecal>(Math.Min(count, 4096));
        for (int i = 0; i < count; i++)
        {
            if (p + 12 > limit) return null;
            p += 4;                                       // id
            int type = BitConverter.ToInt32(b, p); p += 4;
            int texCount = BitConverter.ToInt32(b, p); p += 4;
            // Albedo decals are type 1, normal-map decals type 2.
            if (type < 0 || type > 4 || texCount < 0 || texCount > 16) return null;

            var d = new ScDecal { Type = type };
            for (int t = 0; t < texCount; t++)
            {
                if (p + 4 > limit) return null;
                int len = BitConverter.ToInt32(b, p); p += 4;
                if (len < 0 || len > 512 || p + len > limit) return null;
                if (t == 0) d.Texture = System.Text.Encoding.ASCII.GetString(b, p, len);
                p += len;
            }

            if (p + 52 > limit) return null;
            d.ScaleX = BitConverter.ToSingle(b, p); p += 4;
            d.ScaleY = BitConverter.ToSingle(b, p); p += 4;
            d.ScaleZ = BitConverter.ToSingle(b, p); p += 4;
            d.X = BitConverter.ToSingle(b, p); p += 4;
            d.Y = BitConverter.ToSingle(b, p); p += 4;
            d.Z = BitConverter.ToSingle(b, p); p += 4;
            d.RotX = BitConverter.ToSingle(b, p); p += 4;
            d.RotY = BitConverter.ToSingle(b, p); p += 4;
            d.RotZ = BitConverter.ToSingle(b, p); p += 4;
            d.CutOffLod = BitConverter.ToSingle(b, p); p += 4;
            p += 8;                                       // nearCutOffLOD, ownerArmy

            if (float.IsNaN(d.X) || float.IsInfinity(d.X) ||
                float.IsNaN(d.ScaleX) || float.IsInfinity(d.ScaleX) ||
                Math.Abs(d.ScaleX) > 1e6 || Math.Abs(d.X) > 1e6) return null;
            outp.Add(d);
        }

        // The decal group count follows the table; a wrong anchor lands on
        // noise here.
        if (p + 4 > limit) return null;
        int groups = BitConverter.ToInt32(b, p);
        if (groups < 0 || groups > 200000) return null;
        return outp;
    }
}
