using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Pcdoctor.Core;

/// <summary>
/// 让 JsonNode 的写法和 Python 的 dict / list 一一对应的小工具：真值判断、str()、repr()、==、
/// round()、浮点数格式化等。研判逻辑原样移植自 judge/*.py，细节（尤其是数字和字符串的格式）以 Python 为准。
/// </summary>
public static class Py
{
    // ---- 取值 ----

    /// <summary>d.get(key)：d 不是对象时返回 null（Python 会报错，这里放宽）。</summary>
    public static JsonNode? Get(JsonNode? d, string key) => d is JsonObject o && o.TryGetPropertyValue(key, out var v) ? v : null;

    /// <summary>d.get(key) 且只要对象（默认空对象，不修改原数据）。</summary>
    public static JsonObject Obj(JsonNode? d, string key) => Get(d, key) as JsonObject ?? new JsonObject();

    /// <summary>d.get(key) 且只要数组（默认空数组）。</summary>
    public static JsonArray Arr(JsonNode? d, string key) => Get(d, key) as JsonArray ?? new JsonArray();

    /// <summary>字符串值；不是字符串时返回 null。</summary>
    public static string? S(JsonNode? n) => n is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : null;

    public static bool Has(JsonNode? d, string key) => d is JsonObject o && o.ContainsKey(key);

    public static JsonNode? Clone(JsonNode? n) => n?.DeepClone();

    /// <summary>Python 的 a or b or c：返回第一个为真的值，都不为真时返回最后一个。</summary>
    public static JsonNode? Or(params JsonNode?[] xs)
    {
        for (int i = 0; i < xs.Length - 1; i++)
            if (Truthy(xs[i])) return xs[i];
        return xs.Length > 0 ? xs[^1] : null;
    }

    // ---- 构造 ----

    public static JsonNode? Str(string? s) => s is null ? null : JsonValue.Create(s);
    public static JsonNode Int(long v) => JsonValue.Create(v);
    public static JsonNode Float(double v) => JsonValue.Create(v);
    public static JsonNode Bool(bool v) => JsonValue.Create(v);

    public static JsonArray List(IEnumerable<JsonNode?> xs)
    {
        var a = new JsonArray();
        foreach (var x in xs) a.Add(x?.Parent is null ? x : x.DeepClone()); // 已经挂在别处的节点复制一份
        return a;
    }

    public static JsonArray StrList(IEnumerable<string> xs) => List(xs.Select(x => (JsonNode?)JsonValue.Create(x)));

    // ---- 类型 ----

    public enum Kind { None, Bool, Int, Float, Str, List, Dict }

    public static Kind KindOf(JsonNode? n)
    {
        switch (n)
        {
            case null: return Kind.None;
            case JsonObject: return Kind.Dict;
            case JsonArray: return Kind.List;
        }
        var v = (JsonValue)n;
        switch (v.GetValueKind())
        {
            case JsonValueKind.String: return Kind.Str;
            case JsonValueKind.True:
            case JsonValueKind.False: return Kind.Bool;
            case JsonValueKind.Null: return Kind.None;
            case JsonValueKind.Number:
                var o = v.GetValue<object>();
                if (o is JsonElement el)
                {
                    var raw = el.GetRawText();
                    return raw.IndexOfAny(['.', 'e', 'E']) >= 0 ? Kind.Float : Kind.Int;
                }
                return o is double or float or decimal or Half ? Kind.Float : Kind.Int;
            default: return Kind.None;
        }
    }

    /// <summary>数字的值（不是数字时为 0）。</summary>
    public static double Num(JsonNode? n)
    {
        if (n is not JsonValue v) return 0;
        var o = v.GetValue<object>();
        return o switch
        {
            JsonElement el => el.ValueKind == JsonValueKind.Number ? el.GetDouble() : 0,
            double d => d, float f => f, decimal m => (double)m, Half h => (double)h,
            int i => i, long l => l, short s => s, byte b => b, sbyte sb => sb, uint ui => ui, ulong ul => ul, ushort us => us,
            BigInteger bi => (double)bi,
            _ => 0,
        };
    }

    static BigInteger BigInt(JsonValue v)
    {
        var o = v.GetValue<object>();
        if (o is JsonElement el) return BigInteger.Parse(el.GetRawText(), CultureInfo.InvariantCulture);
        return o switch
        {
            int i => i, long l => l, short s => s, byte b => b, sbyte sb => sb, uint ui => ui, ulong ul => ul, ushort us => us,
            BigInteger bi => bi,
            _ => new BigInteger(Convert.ToDouble(o, CultureInfo.InvariantCulture)),
        };
    }

    /// <summary>bool(x)</summary>
    public static bool Truthy(JsonNode? n) => KindOf(n) switch
    {
        Kind.None => false,
        Kind.Bool => n!.GetValue<bool>(),
        Kind.Int => !BigInt((JsonValue)n!).IsZero,
        Kind.Float => Num(n) != 0,
        Kind.Str => S(n)!.Length > 0,
        Kind.List => ((JsonArray)n!).Count > 0,
        Kind.Dict => ((JsonObject)n!).Count > 0,
        _ => false,
    };

    /// <summary>Python 的 ==（数字按值比较，True == 1，dict 不看键的顺序）。</summary>
    public static bool Eq(JsonNode? a, JsonNode? b)
    {
        var ka = KindOf(a);
        var kb = KindOf(b);
        bool numA = ka is Kind.Int or Kind.Float or Kind.Bool, numB = kb is Kind.Int or Kind.Float or Kind.Bool;
        if (numA && numB) return NumValue(a) == NumValue(b);
        if (ka != kb) return false;
        switch (ka)
        {
            case Kind.None: return true;
            case Kind.Str: return S(a) == S(b);
            case Kind.List:
                var la = (JsonArray)a!; var lb = (JsonArray)b!;
                if (la.Count != lb.Count) return false;
                for (int i = 0; i < la.Count; i++) if (!Eq(la[i], lb[i])) return false;
                return true;
            case Kind.Dict:
                var da = (JsonObject)a!; var db = (JsonObject)b!;
                if (da.Count != db.Count) return false;
                foreach (var (k, v) in da)
                    if (!db.TryGetPropertyValue(k, out var w) || !Eq(v, w)) return false;
                return true;
        }
        return false;
    }

    static double NumValue(JsonNode? n) => KindOf(n) == Kind.Bool ? (n!.GetValue<bool>() ? 1 : 0) : Num(n);

    /// <summary>
    /// 当作 dict / set 键时用的规范字符串：Python 里相等的值得到同一个键（字符串和数字不会混淆）。
    /// </summary>
    public static string Key(JsonNode? n) => KindOf(n) switch
    {
        Kind.Str => "s:" + S(n),
        Kind.None => "n:",
        Kind.Bool or Kind.Int or Kind.Float => "d:" + NumValue(n).ToString("R", CultureInfo.InvariantCulture),
        _ => "j:" + PyJson.Dumps(n, sortKeys: true),
    };

    /// <summary>for x in v：字符串逐字符、dict 取键、list 取元素；其他类型当作空（Python 会报错）。</summary>
    public static IEnumerable<JsonNode?> Iter(JsonNode? v)
    {
        switch (KindOf(v))
        {
            case Kind.List:
                foreach (var x in (JsonArray)v!) yield return x;
                break;
            case Kind.Dict:
                foreach (var kv in (JsonObject)v!) yield return JsonValue.Create(kv.Key);
                break;
            case Kind.Str:
                foreach (var r in S(v)!.EnumerateRunes()) yield return JsonValue.Create(r.ToString());
                break;
        }
    }

    // ---- 字符串 ----

    /// <summary>len(s)：按码点计数（Python 的长度），不是 UTF-16 单元。</summary>
    public static int Len(string s)
    {
        int n = 0;
        for (int i = 0; i < s.Length; i++)
        {
            if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) i++;
            n++;
        }
        return n;
    }

    /// <summary>s[:n]，按码点。</summary>
    public static string Head(string s, int n)
    {
        int count = 0, i = 0;
        while (i < s.Length && count < n)
        {
            if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) i += 2;
            else i++;
            count++;
        }
        return s[..i];
    }

    /// <summary>Python 的字符串排序：按码点比较。</summary>
    public static readonly IComparer<string> StrOrder = Comparer<string>.Create(CompareCodePoints);

    public static int CompareCodePoints(string? a, string? b)
    {
        if (a is null || b is null) return a is null ? (b is null ? 0 : -1) : 1;
        int n = Math.Min(a.Length, b.Length);
        for (int i = 0; i < n; i++)
        {
            char x = a[i], y = b[i];
            if (x == y) continue;
            // UTF-16 的代理项（U+D800–DFFF）按码点应排在 U+E000–FFFF 之后
            return Fix(x).CompareTo(Fix(y));
        }
        return a.Length.CompareTo(b.Length);

        static int Fix(char c) => c >= 0xD800 ? (c >= 0xE000 ? c - 0x800 : c + 0x2000) : c;
    }

    public static List<string> Sorted(IEnumerable<string> xs)
    {
        var l = xs.ToList();
        l.Sort(StrOrder);
        return l;
    }

    /// <summary>str(x)</summary>
    public static string ToStr(JsonNode? n) => KindOf(n) == Kind.Str ? S(n)! : Repr(n);

    /// <summary>str(x or '')：只在 x 为真时取 str。</summary>
    public static string StrOr(JsonNode? n) => Truthy(n) ? ToStr(n) : "";

    /// <summary>repr(x)</summary>
    public static string Repr(JsonNode? n)
    {
        switch (KindOf(n))
        {
            case Kind.None: return "None";
            case Kind.Bool: return n!.GetValue<bool>() ? "True" : "False";
            case Kind.Int: return BigInt((JsonValue)n!).ToString(CultureInfo.InvariantCulture);
            case Kind.Float: return FloatRepr(Num(n));
            case Kind.Str: return ReprStr(S(n)!);
            case Kind.List: return "[" + string.Join(", ", ((JsonArray)n!).Select(Repr)) + "]";
            case Kind.Dict: return "{" + string.Join(", ", ((JsonObject)n!).Select(kv => ReprStr(kv.Key) + ": " + Repr(kv.Value))) + "}";
        }
        return "None";
    }

    public static string ReprList(IEnumerable<JsonNode?> xs) => "[" + string.Join(", ", xs.Select(Repr)) + "]";
    public static string ReprList(IEnumerable<string> xs) => "[" + string.Join(", ", xs.Select(ReprStr)) + "]";

    public static string ReprStr(string s)
    {
        char q = s.Contains('\'') && !s.Contains('"') ? '"' : '\'';
        var sb = new StringBuilder();
        sb.Append(q);
        foreach (var r in s.EnumerateRunes())
        {
            int c = r.Value;
            if (c == q || c == '\\') sb.Append('\\').Append((char)c);
            else if (c == '\t') sb.Append("\\t");
            else if (c == '\n') sb.Append("\\n");
            else if (c == '\r') sb.Append("\\r");
            else if (c < 0x20 || c == 0x7F) sb.Append("\\x").Append(c.ToString("x2"));
            else if (c < 0x7F) sb.Append((char)c);
            else if (!IsPrintable(r))
            {
                if (c <= 0xFF) sb.Append("\\x").Append(c.ToString("x2"));
                else if (c <= 0xFFFF) sb.Append("\\u").Append(c.ToString("x4"));
                else sb.Append("\\U").Append(c.ToString("x8"));
            }
            else sb.Append(r.ToString());
        }
        sb.Append(q);
        return sb.ToString();
    }

    static bool IsPrintable(Rune r)
    {
        var cat = Rune.GetUnicodeCategory(r);
        return cat switch
        {
            UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.Surrogate or UnicodeCategory.PrivateUse
                or UnicodeCategory.OtherNotAssigned or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator => false,
            UnicodeCategory.SpaceSeparator => r.Value == ' ',
            _ => true,
        };
    }

    // ---- 浮点数 ----

    /// <summary>repr(float)：最短可往返的十进制，-4 &lt; 指数 ≤ 16 时用定点写法并保留 .0。</summary>
    public static string FloatRepr(double x)
    {
        if (double.IsNaN(x)) return "nan";
        if (double.IsInfinity(x)) return x > 0 ? "inf" : "-inf";
        if (x == 0) return double.IsNegative(x) ? "-0.0" : "0.0";
        var (neg, digits, decpt) = ShortestDigits(x);
        string body;
        if (decpt > -4 && decpt <= 16)
        {
            if (decpt <= 0) body = "0." + new string('0', -decpt) + digits;
            else if (decpt >= digits.Length) body = digits + new string('0', decpt - digits.Length) + ".0";
            else body = digits[..decpt] + "." + digits[decpt..];
        }
        else
        {
            int e = decpt - 1;
            body = digits[..1] + (digits.Length > 1 ? "." + digits[1..] : "") + "e" + (e < 0 ? "-" : "+") + Math.Abs(e).ToString("00");
        }
        return neg ? "-" + body : body;
    }

    /// <summary>最短可往返数字串和小数点位置（值 = 0.digits × 10^decpt）。</summary>
    static (bool neg, string digits, int decpt) ShortestDigits(double x)
    {
        // 先取 .NET 的最短往返表示（"R"），再统一成 digits + 指数
        var r = x.ToString("R", CultureInfo.InvariantCulture);
        bool neg = r.StartsWith('-');
        if (neg) r = r[1..];
        int exp = 0;
        int ei = r.IndexOfAny(['E', 'e']);
        if (ei >= 0)
        {
            exp = int.Parse(r[(ei + 1)..], CultureInfo.InvariantCulture);
            r = r[..ei];
        }
        int dot = r.IndexOf('.');
        string intPart = dot >= 0 ? r[..dot] : r, frac = dot >= 0 ? r[(dot + 1)..] : "";
        string all = intPart + frac;
        int decpt = intPart.Length + exp;
        int lead = 0;
        while (lead < all.Length - 1 && all[lead] == '0') { lead++; decpt--; }
        all = all[lead..].TrimEnd('0');
        if (all.Length == 0) all = "0";
        return (neg, all, decpt);
    }

    /// <summary>精确的十进制展开后按"四舍六入五成双"保留 n 位小数，返回整数部分 q（值 ≈ q / 10^n）和符号。</summary>
    static (bool neg, BigInteger q) RoundScaled(double x, int n)
    {
        long bits = BitConverter.DoubleToInt64Bits(x);
        bool neg = bits < 0;
        int exp = (int)((bits >> 52) & 0x7FF);
        long man = bits & 0xFFFFFFFFFFFFFL;
        if (exp == 0) exp++; else man |= 1L << 52;
        exp -= 1075; // x = man × 2^exp
        BigInteger num = man, den = 1;
        if (exp >= 0) num <<= exp; else den <<= -exp;
        if (n >= 0) num *= BigInteger.Pow(10, n); else den *= BigInteger.Pow(10, -n);
        var q = BigInteger.DivRem(num, den, out var rem);
        int c = (rem * 2).CompareTo(den);
        if (c > 0 || (c == 0 && !q.IsEven)) q += 1;
        return (neg, q);
    }

    /// <summary>round(x, n)：和 Python 一样按精确值做银行家舍入（2.675 → 2.67）。</summary>
    public static double Round(double x, int n)
    {
        if (double.IsNaN(x) || double.IsInfinity(x) || x == 0) return x;
        var (neg, q) = RoundScaled(x, n);
        var s = q.ToString(CultureInfo.InvariantCulture) + "E" + (-n).ToString(CultureInfo.InvariantCulture);
        var v = double.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);
        return neg ? -v : v;
    }

    /// <summary>round(x)：返回整数，银行家舍入。</summary>
    public static long RoundInt(double x) => (long)Math.Round(x, MidpointRounding.ToEven);

    /// <summary>f'{x:.nf}'</summary>
    public static string Fixed(double x, int n)
    {
        if (double.IsNaN(x)) return "nan";
        if (double.IsInfinity(x)) return x > 0 ? "inf" : "-inf";
        var (neg, q) = RoundScaled(x, n);
        var s = q.ToString(CultureInfo.InvariantCulture).PadLeft(n + 1, '0');
        if (n > 0) s = s[..^n] + "." + s[^n..];
        return (neg ? "-" : "") + s;
    }

    /// <summary>float(v)：数字、布尔、数字字符串；其他抛 FormatException。</summary>
    public static double ToFloat(JsonNode? v)
    {
        switch (KindOf(v))
        {
            case Kind.Bool: return v!.GetValue<bool>() ? 1 : 0;
            case Kind.Int:
            case Kind.Float: return Num(v);
            case Kind.Str:
                var s = S(v)!.Trim();
                var low = s.ToLowerInvariant();
                var sign = 1.0;
                var body = low;
                if (body.StartsWith('+') || body.StartsWith('-')) { sign = body[0] == '-' ? -1 : 1; body = body[1..]; }
                if (body is "inf" or "infinity") return sign * double.PositiveInfinity;
                if (body == "nan") return double.NaN;
                if (body.Length > 0 && (char.IsAsciiDigit(body[0]) || body[0] == '.')
                    && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return d;
                throw new FormatException(s);
        }
        throw new FormatException("not a number");
    }

    /// <summary>str(数字)：JsonNode 里的 int 或 float，按 Python 的写法。</summary>
    public static string NumStr(JsonNode? n) => ToStr(n);
}
