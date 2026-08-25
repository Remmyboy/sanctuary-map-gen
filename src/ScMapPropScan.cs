// Finding the prop table without walking the whole file.
//
// The straight walk from the water block to the props has to cross wave
// generators, the terrain texture set, decals, decal groups and eight
// length-prefixed images. It works on most maps and desynchronises on some -
// Seton's Clutch has four bytes after its wave textures that are not the wave
// generator count the format documents, and three sample maps were not enough
// to work out what they are.
//
// So do not walk. Props are the last section in the file, which makes them
// self-locating: a candidate offset is correct only if parsing count records
// from it lands exactly on the final byte. That check is strong enough that a
// wrong guess is essentially impossible to accept, and it does not care about
// anything upstream.
public static partial class MapGen
{
    /// Find and read the prop table by anchoring on the end of the file.
    /// Returns null if no candidate parses cleanly to EOF.
    public static List<ScProp> ScanScProps(byte[] b)
    {
        // Prop blueprint paths all live under these roots. Collect the offsets
        // of every plausible path start, cheaply, in one pass.
        var starts = new List<int>();
        for (int i = 0; i + 6 < b.Length; i++)
        {
            if (b[i] != '/') continue;
            if ((b[i + 1] == 'e' && b[i + 2] == 'n' && b[i + 3] == 'v' && b[i + 4] == '/') ||
                (b[i + 1] == 'p' && b[i + 2] == 'r' && b[i + 3] == 'o' && b[i + 4] == 'p'))
                starts.Add(i);
        }
        // A map with no props ends with a count of zero and has no records to
        // anchor on, which is a valid answer rather than a failure.
        if (b.Length >= 4 && BitConverter.ToInt32(b, b.Length - 4) == 0)
            return new List<ScProp>();
        if (starts.Count == 0) return null;

        // A prop record is a null-terminated path then fifteen floats: position,
        // three rotation basis vectors, and scale. Try each path start as the
        // first record, with the count in the four bytes before it.
        foreach (int first in starts)
        {
            if (first < 4) continue;
            int count = b[first - 4] | b[first - 3] << 8 | b[first - 2] << 16 | b[first - 1] << 24;
            if (count <= 0 || count > 500000) continue;

            var props = TryReadPropRun(b, first, count);
            if (props != null) return props;
        }
        return null;
    }

    /// Read `count` prop records from `p`. Succeeds only if the last one ends
    /// exactly at the end of the file.
    static List<ScProp> TryReadPropRun(byte[] b, int p, int count)
    {
        var outp = new List<ScProp>(count);
        for (int i = 0; i < count; i++)
        {
            int s = p;
            while (p < b.Length && b[p] != 0)
            {
                // Paths are printable ASCII; anything else means this was not a
                // prop record and the candidate is wrong.
                if (b[p] < 0x20 || b[p] > 0x7e) return null;
                p++;
            }
            if (p >= b.Length) return null;
            int len = p - s;
            if (len < 5 || len > 300) return null;
            string path = System.Text.Encoding.ASCII.GetString(b, s, len);
            p++;                                   // terminator

            if (p + 60 > b.Length) return null;
            var pr = new ScProp { Blueprint = path };
            pr.X = BitConverter.ToSingle(b, p); pr.Y = BitConverter.ToSingle(b, p + 4); pr.Z = BitConverter.ToSingle(b, p + 8);
            pr.RotXx = BitConverter.ToSingle(b, p + 12); pr.RotXy = BitConverter.ToSingle(b, p + 16); pr.RotXz = BitConverter.ToSingle(b, p + 20);
            // p+24..p+35 is the y basis, which a ground prop does not need.
            pr.RotZx = BitConverter.ToSingle(b, p + 36); pr.RotZy = BitConverter.ToSingle(b, p + 40); pr.RotZz = BitConverter.ToSingle(b, p + 44);
            pr.ScaleX = BitConverter.ToSingle(b, p + 48);
            pr.ScaleY = BitConverter.ToSingle(b, p + 52);
            pr.ScaleZ = BitConverter.ToSingle(b, p + 56);
            p += 60;   // position, three rotation basis vectors, scale

            outp.Add(pr);
        }
        // The prop table is the last thing in the file, so a candidate is only
        // right if it consumes exactly to the end.
        return p == b.Length ? outp : null;
    }

    /// Yaw in radians, from the x basis vector SupCom stores.
    public static float ScPropYaw(ScProp p)
    {
        return (float)Math.Atan2(p.RotXz, p.RotXx);
    }
}
