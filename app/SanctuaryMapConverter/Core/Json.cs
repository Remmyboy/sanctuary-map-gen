using System.Globalization;
using System.Text;

namespace SanctuaryMapConverter.Core
{
    // A .sanmap is JSON with a strict edge: width, length, height,
    // heightmapResolution and Army.faction are int fields, and Newtonsoft
    // rejects "128.0" for them. The PowerShell pipeline got this right by
    // typing values before ConvertTo-Json; here the object model keeps the
    // distinction - int stays int, double stays double - and the writer
    // preserves insertion order the way the shipped maps read.
    public sealed class JObj
    {
        public readonly List<KeyValuePair<string, object>> Items = new();
        public JObj Add(string key, object value) { Items.Add(new(key, value)); return this; }
        public object this[string key]
        {
            set
            {
                for (int i = 0; i < Items.Count; i++)
                    if (Items[i].Key == key) { Items[i] = new(key, value); return; }
                Items.Add(new(key, value));
            }
        }
    }

    public static class Json
    {
        public static JObj Obj(params (string k, object v)[] pairs)
        {
            var o = new JObj();
            foreach (var (k, v) in pairs) o.Add(k, v);
            return o;
        }

        public static JObj Vec3(double x, double y, double z) => Obj(("x", x), ("y", y), ("z", z));
        public static JObj Quat(double x, double y, double z, double w) => Obj(("x", x), ("y", y), ("z", z), ("w", w));
        public static JObj Rgba(double r, double g, double b, double a) => Obj(("r", r), ("g", g), ("b", b), ("a", a));

        public static string Write(object root)
        {
            var sb = new StringBuilder(1 << 20);
            WriteValue(sb, root, 0);
            return sb.ToString();
        }

        static void WriteValue(StringBuilder sb, object v, int depth)
        {
            switch (v)
            {
                case null: sb.Append("null"); break;
                case bool b: sb.Append(b ? "true" : "false"); break;
                case int i: sb.Append(i.ToString(CultureInfo.InvariantCulture)); break;
                case long l: sb.Append(l.ToString(CultureInfo.InvariantCulture)); break;
                case float f: WriteDouble(sb, f); break;
                case double d: WriteDouble(sb, d); break;
                case string s: WriteString(sb, s); break;
                case JObj o: WriteObj(sb, o, depth); break;
                case System.Collections.IEnumerable e: WriteArr(sb, e, depth); break;
                default: WriteString(sb, v.ToString()); break;
            }
        }

        // Whole doubles print as "55.0", matching ConvertTo-Json - not needed
        // for correctness (the game's parsers accept either for float fields)
        // but it keeps exe and PowerShell output diffable line for line.
        static void WriteDouble(StringBuilder sb, double d)
        {
            string s = d.ToString("R", CultureInfo.InvariantCulture);
            sb.Append(s);
            if (!s.Contains('.') && !s.Contains('E') && !s.Contains("Inf") && !s.Contains("NaN"))
                sb.Append(".0");
        }

        static void WriteObj(StringBuilder sb, JObj o, int depth)
        {
            if (o.Items.Count == 0) { sb.Append("{}"); return; }
            sb.Append("{\n");
            for (int i = 0; i < o.Items.Count; i++)
            {
                Indent(sb, depth + 1);
                WriteString(sb, o.Items[i].Key);
                sb.Append(": ");
                WriteValue(sb, o.Items[i].Value, depth + 1);
                if (i < o.Items.Count - 1) sb.Append(',');
                sb.Append('\n');
            }
            Indent(sb, depth); sb.Append('}');
        }

        static void WriteArr(StringBuilder sb, System.Collections.IEnumerable e, int depth)
        {
            var items = new List<object>();
            foreach (var x in e) items.Add(x);
            if (items.Count == 0) { sb.Append("[]"); return; }
            sb.Append("[\n");
            for (int i = 0; i < items.Count; i++)
            {
                Indent(sb, depth + 1);
                WriteValue(sb, items[i], depth + 1);
                if (i < items.Count - 1) sb.Append(',');
                sb.Append('\n');
            }
            Indent(sb, depth); sb.Append(']');
        }

        static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char c in s)
            {
                if (c == '"' || c == '\\') { sb.Append('\\').Append(c); }
                else if (c == '\n') sb.Append("\\n");
                else if (c == '\r') sb.Append("\\r");
                else if (c == '\t') sb.Append("\\t");
                else if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                else sb.Append(c);
            }
            sb.Append('"');
        }

        static void Indent(StringBuilder sb, int depth) => sb.Append(' ', depth * 2);
    }
}
