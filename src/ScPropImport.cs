// Bringing the source map's props across.
//
// A converted map used to arrive with props we scattered ourselves - about 600
// of them, evenly spread, in place of the author's work. Measured across the
// whole corpus the source maps carry 1,685,924 prop instances between them,
// averaging 6,716 a map and reaching 31,042 on the largest. Hand-placed tree
// lines and rock fields are a large part of why a competitive map plays the
// way it does, and none of it was surviving.
//
// The mapping is easier than it looks because Supreme Commander's blueprint
// paths are regular. Classifying by path alone accounts for 92.6% of every
// instance in the corpus as tree, tree-group or rock, and most of the rest are
// rocks named outside the /rocks/ folder:
//
//     tree         92 blueprints    702,761 instances   41.7%
//     rock        106 blueprints    489,637 instances   29.0%
//     tree-group   44 blueprints    368,048 instances   21.8%
//     other       112 blueprints    125,478 instances    7.4%
//
// Sanctuary has 94 prop blueprints with opaque names (edbm0121 and the like)
// and no descriptive metadata - every one calls itself "Harvestable prop" - so
// which Sanctuary prop to use is a policy decision that lives in the caller,
// not a fact to derive here. This returns a kind and a transform; the caller
// picks the blueprint.
public static partial class MapGen
{
    public class ScPropOut
    {
        /// 0 tree, 1 tree-group, 2 rock, 3 unclassified.
        public int Kind;
        /// The source blueprint path, kept so the caller can match the
        /// environment family and size when choosing a Sanctuary prop.
        public string Blueprint = "";
        public float X, Y, Z;
        public float Yaw;                  // radians, already in Sanctuary's frame
        public float ScaleX, ScaleY, ScaleZ;
    }

    /// Classify a Supreme Commander prop blueprint path.
    public static int ScPropKind(string bp)
    {
        if (string.IsNullOrEmpty(bp)) return 3;
        string p = bp.ToLowerInvariant();
        if (p.Contains("/trees/groups/")) return 1;
        // Bushes and ferns are standing foliage of the same sort, just smaller,
        // and there are 9,000 of them across the corpus.
        if (p.Contains("/trees/") || p.Contains("/bush/") || p.Contains("fern")) return 0;
        // Rocks live under /rocks/ on most environments but not all: redrocks
        // names them rock_sm01, tundra has icerocksm02 and iceberg04, and all
        // read as stone. Logs are fallen trees but sit low and scattered, so
        // they behave like rocks rather than like standing timber.
        if (p.Contains("/rocks/") || p.Contains("boulder") || p.Contains("rock_") ||
            p.Contains("icerock") || p.Contains("iceberg") || p.Contains("/logs/") ||
            p.Contains("rubble") || p.Contains("stone")) return 2;
        return 3;
    }

    /// Convert source props into Sanctuary's frame.
    ///
    /// The z flip is the same one applied to the terrain and the markers -
    /// Supreme Commander's z grows south, Sanctuary's grows north - and it has
    /// to be applied to all three or the map is self-inconsistent. Yaw negates
    /// with it, because mirroring an axis reverses the sense of rotation about
    /// the vertical.
    public static List<ScPropOut> ConvertScProps(List<ScProp> props, ScMapInfo m, float verticalScale)
    {
        var outp = new List<ScPropOut>();
        if (props == null) return outp;

        foreach (var p in props)
        {
            // Supreme Commander stores rotation as three basis vectors rather
            // than a quaternion. The prop's local X axis in world space gives
            // the yaw directly; the other two carry tilt we do not use, since
            // Sanctuary seats props on the terrain normal itself.
            float yaw = (float)Math.Atan2(p.RotXz, p.RotXx);

            outp.Add(new ScPropOut
            {
                Kind = ScPropKind(p.Blueprint),
                Blueprint = p.Blueprint ?? "",
                X = p.X,
                Y = p.Y * verticalScale,
                Z = m.Size - p.Z,
                Yaw = -yaw,
                ScaleX = p.ScaleX <= 0f ? 1f : p.ScaleX,
                ScaleY = p.ScaleY <= 0f ? 1f : p.ScaleY,
                ScaleZ = p.ScaleZ <= 0f ? 1f : p.ScaleZ,
            });
        }
        return outp;
    }

    /// Thin a prop list to at most `limit`, keeping an even spread by taking
    /// every n-th entry rather than a prefix. Returns the input untouched when
    /// it already fits. The caller reports what was dropped - a silent cap
    /// reads as "everything came across" when it did not.
    public static List<ScPropOut> ThinProps(List<ScPropOut> props, int limit)
    {
        if (limit <= 0 || props == null || props.Count <= limit) return props;
        var outp = new List<ScPropOut>(limit);
        double step = props.Count / (double)limit;
        for (double i = 0; i < props.Count && outp.Count < limit; i += step)
            outp.Add(props[(int)i]);
        return outp;
    }
}
