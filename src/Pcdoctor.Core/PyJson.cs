using System.Text;
using System.Text.Json.Nodes;

namespace Pcdoctor.Core;

/// <summary>
/// 与 Python 的 json.dumps(obj, ensure_ascii=False, indent=..., sort_keys=...) 输出逐字节相同的序列化。
/// 发给模型的证据包、pcdoctor-known.json、-judge.json 都用它，和 Python 版写出的文件一致。
/// </summary>
public static class PyJson
{
    /// <param name="indent">null：单行（分隔符 ", " 和 ": "）；数字：换行缩进（分隔符 "," 和 ": "）。</param>
    public static string Dumps(JsonNode? node, int? indent = null, bool sortKeys = false)
    {
        var sb = new StringBuilder();
        Write(sb, node, indent, sortKeys, 0);
        return sb.ToString();
    }

    static void Write(StringBuilder sb, JsonNode? n, int? indent, bool sortKeys, int level)
    {
        switch (Py.KindOf(n))
        {
            case Py.Kind.None: sb.Append("null"); return;
            case Py.Kind.Bool: sb.Append(n!.GetValue<bool>() ? "true" : "false"); return;
            case Py.Kind.Int: sb.Append(Py.Repr(n)); return;
            case Py.Kind.Float:
                var d = Py.Num(n);
                sb.Append(double.IsNaN(d) ? "NaN" : double.IsPositiveInfinity(d) ? "Infinity" : double.IsNegativeInfinity(d) ? "-Infinity" : Py.FloatRepr(d));
                return;
            case Py.Kind.Str: WriteString(sb, Py.S(n)!); return;
            case Py.Kind.List:
                var a = (JsonArray)n!;
                if (a.Count == 0) { sb.Append("[]"); return; }
                sb.Append('[');
                for (int i = 0; i < a.Count; i++)
                {
                    if (i > 0) sb.Append(indent is null ? ", " : ",");
                    NewLine(sb, indent, level + 1);
                    Write(sb, a[i], indent, sortKeys, level + 1);
                }
                NewLine(sb, indent, level);
                sb.Append(']');
                return;
            case Py.Kind.Dict:
                var o = (JsonObject)n!;
                if (o.Count == 0) { sb.Append("{}"); return; }
                IEnumerable<KeyValuePair<string, JsonNode?>> items = o;
                if (sortKeys) items = o.OrderBy(kv => kv.Key, Py.StrOrder);
                sb.Append('{');
                bool first = true;
                foreach (var (k, v) in items)
                {
                    if (!first) sb.Append(indent is null ? ", " : ",");
                    first = false;
                    NewLine(sb, indent, level + 1);
                    WriteString(sb, k);
                    sb.Append(": ");
                    Write(sb, v, indent, sortKeys, level + 1);
                }
                NewLine(sb, indent, level);
                sb.Append('}');
                return;
        }
    }

    static void NewLine(StringBuilder sb, int? indent, int level)
    {
        if (indent is null) return;
        sb.Append('\n').Append(' ', indent.Value * level);
    }

    public static void WriteString(StringBuilder sb, string s)
    {
        sb.Append('"');
        foreach (char c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                default:
                    if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                    else sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
    }

    /// <summary>json.loads：解析失败抛 System.Text.Json.JsonException。</summary>
    public static JsonNode? Loads(string text)
    {
        return JsonNode.Parse(text, null, new System.Text.Json.JsonDocumentOptions { MaxDepth = 256 });
    }
}
