using System.Linq;
using System.Reflection;

namespace SanctuaryMapConverter.Core
{
    // The PowerShell pipeline ran every script in a fresh process, so
    // MapGen's static fields always started at their compiled defaults. This
    // app runs many maps in one process - the deploy-all rebuild alone runs
    // eight builds back to back - and the named-map scripts deliberately rely
    // on defaults they never set (Serpent Crossing vouches for the hardcoded
    // BridgeX/BridgeZ, Broken Mesa never touches SymOrder). A field dirtied
    // by one run must not leak into the next.
    //
    // Rather than hand-maintain a reset list that goes stale the day MapGen
    // grows a field, the first Reset() snapshots every static field via
    // reflection and later calls restore the snapshot. Call it at the top of
    // anything that drives MapGen.
    public static class EngineState
    {
        static (FieldInfo Field, object Value)[] _defaults;

        public static void Reset()
        {
            if (_defaults == null)
            {
                _defaults = typeof(MapGen)
                    .GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                    .Where(f => !f.IsLiteral)
                    .Select(f => (f, Clone(f.GetValue(null))))
                    .ToArray();
                return;   // first caller sees the compiled defaults already
            }

            foreach (var (f, v) in _defaults)
            {
                if (f.IsInitOnly)
                {
                    // Cannot reassign, but a readonly array's contents can
                    // have been mutated in place - copy the snapshot back.
                    if (v is Array src && f.GetValue(null) is Array dst && dst.Length == src.Length)
                        Array.Copy(src, dst, src.Length);
                    continue;
                }
                f.SetValue(null, Clone(v));
            }
        }

        static object Clone(object v) => v is Array a ? a.Clone() : v;
    }
}
