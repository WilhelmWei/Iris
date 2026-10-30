using System.Text.Json.Nodes;

namespace Pcdoctor.Core;

/// <summary>
/// 健康分（对应 judge/scoring.py）：由代码按规则命中计算，模型不参与打分。
/// 每个类别从 100 分起扣，总分按权重加权。同一条规则在一个类别里命中多次时合并扣分。
/// 被研判为"正常"且置信度足够的结果不扣分。
/// 注意 Python 里 int 和 float 的区别会影响输出（70 和 63.3、"−30" 和 "−45.0"），这里用 PyNum 跟踪。
/// </summary>
public static class Scoring
{
    /// <summary>WEIGHTS（顺序即输出顺序）</summary>
    public static readonly IReadOnlyList<(string name, int weight)> Weights =
        [("安全", 30), ("性能与后台", 25), ("存储", 20), ("启动", 15), ("驱动与系统设置", 10)];

    public static readonly IReadOnlyDictionary<string, string> SectionCategory = new Dictionary<string, string>
    {
        ["security"] = "安全",
        ["resources"] = "性能与后台", ["processes"] = "性能与后台", ["services"] = "性能与后台",
        ["scheduledTasks"] = "性能与后台", ["software"] = "性能与后台", ["browserNetwork"] = "性能与后台",
        ["storage"] = "存储",
        ["startup"] = "启动",
        ["drivers"] = "驱动与系统设置", ["versions"] = "驱动与系统设置", ["power"] = "驱动与系统设置", ["stability"] = "驱动与系统设置",
    };

    /// <summary>虽然落在其他节、但本质是安全问题的规则</summary>
    public static readonly IReadOnlySet<string> SecurityRules = new HashSet<string>
    {
        "system-name-outside-windows", "unsigned-user-folder", "script-engine",
        "proxy-remote", "proxy-pac", "hosts-custom", "svchost-third-party-dll",
    };

    /// <summary>研判为"正常"时可以免扣分的规则：只限识别问题；状态问题是客观事实，照样扣分</summary>
    public static readonly IReadOnlySet<string> ClearableRules = new HashSet<string>
    {
        "runs-from-user-folder", "unsigned", "heavy-memory", "unsigned-user-folder",
        "seed-bundle", "seed-anticheat-accel", "seed-bank-security", "svchost-third-party-dll",
        "server-service-running", "frequent", "duplicate-versions", "boot-start-third-party",
        "third-party-class-filter", "edge-preload", "duplicate-category",
        "edge-idle-background", "chrome-idle-background", "browser-unused",
    };

    /// <summary>用户确认后可以免扣分的规则：识别类规则，加上用户自己做的 Defender 设置</summary>
    public static readonly IReadOnlySet<string> UserClearableRules =
        new HashSet<string>(ClearableRules) { "defender-allowed-threat", "defender-exclusion" };

    public static readonly IReadOnlyDictionary<string, int> Penalty = new Dictionary<string, int>
        { ["high"] = 30, ["medium"] = 10, ["low"] = 3, ["info"] = 0 };

    public const double ExtraShare = 1.0 / 3; // 同一规则的第二条起，每条只扣自身分值的三分之一
    public const double GroupCap = 1.5;        // 同一规则合计最多扣最高单条分值的 1.5 倍

    /// <summary>Python 的数字：值 + 是否是 float。</summary>
    readonly record struct PyNum(double V, bool F)
    {
        public JsonNode Node => F ? Py.Float(V) : Py.Int((long)V);
        public static PyNum Int(long v) => new(v, false);
    }

    static string Category(JsonNode flag)
    {
        var rule = Py.S(Py.Get(flag, "rule"));
        if (rule is not null && SecurityRules.Contains(rule)) return "安全";
        var sec = Py.S(Py.Get(flag, "section"));
        return sec is not null && SectionCategory.TryGetValue(sec, out var c) ? c : "性能与后台";
    }

    static int PenaltyOf(JsonNode f) => Py.S(Py.Get(f, "severity")) is { } s && Penalty.TryGetValue(s, out var p) ? p : 0;

    static string Id(JsonNode f) => Py.ToStr(Py.Get(f, "id"));

    /// <summary>
    /// CPython 3.12+ 的 sum()：从整数 0 开始，遇到浮点数后用 Neumaier 补偿求和。
    /// 全是整数（或为空）时结果是整数。
    /// </summary>
    static PyNum PySum(IEnumerable<PyNum> xs)
    {
        double f = 0, c = 0;
        bool isFloat = false;
        foreach (var x in xs)
        {
            if (!isFloat)
            {
                if (!x.F) { f += x.V; continue; }
                f += x.V; // 第一个浮点数：0 + x（精确）
                isFloat = true;
                continue;
            }
            double t = f + x.V;
            if (Math.Abs(f) >= Math.Abs(x.V)) c += (f - t) + x.V;
            else c += (x.V - t) + f;
            f = t;
        }
        if (isFloat && c != 0 && double.IsFinite(c)) f += c;
        return new PyNum(f, isFloat);
    }

    /// <summary>浮点数列表的 sum()（测试用）。</summary>
    internal static double PyFloatSum(IEnumerable<double> xs) => PySum(xs.Select(x => new PyNum(x, true))).V;

    static PyNum Round1(PyNum x) => x.F ? new PyNum(Py.Round(x.V, 1), true) : x;

    /// <summary>
    /// health_score()。flags：报告里的预筛结果；clearedIds：研判为正常的 flag id；confirmedIds：用户确认过的 flag id。
    /// </summary>
    public static JsonObject HealthScore(JsonArray flags, IEnumerable<string>? clearedIds = null, IEnumerable<string>? confirmedIds = null)
    {
        var clearedSet = new HashSet<string>(clearedIds ?? []);
        var confirmedSet = new HashSet<string>(confirmedIds ?? []);
        var flagList = flags.Where(f => f is not null).Select(f => f!).ToList();
        var confirmed = flagList.Where(f => confirmedSet.Contains(Id(f)) && Py.S(Py.Get(f, "rule")) is { } r && UserClearableRules.Contains(r))
            .Select(Id).ToHashSet();
        var cleared = flagList.Where(f => clearedSet.Contains(Id(f)) && Py.S(Py.Get(f, "rule")) is { } r && ClearableRules.Contains(r))
            .Select(Id).ToHashSet();
        var notes = new Dictionary<string, string>();
        foreach (var i in confirmed) notes[i] = "你已确认，不扣分";
        foreach (var i in cleared.Except(confirmed)) notes[i] = "研判为正常，不扣分";
        cleared.UnionWith(confirmed);

        var groups = new List<((string cat, string rule) key, List<JsonNode> fs)>();
        foreach (var f in flagList)
        {
            if (PenaltyOf(f) == 0 || cleared.Contains(Id(f))) continue;
            var key = (Category(f), Py.Key(Py.Get(f, "rule")));
            var g = groups.FindIndex(x => x.key == key);
            if (g < 0) groups.Add((key, [f]));
            else groups[g].fs.Add(f);
        }

        var cats = new Dictionary<string, (int weight, PyNum score, JsonArray deductions)>();
        foreach (var (name, w) in Weights) cats[name] = (w, PyNum.Int(100), new JsonArray());
        foreach (var ((cat, _), fs) in groups)
        {
            var pts = fs.Select(PenaltyOf).OrderByDescending(p => p).ToList();
            var a = PySum(pts.Skip(1).Select(p => new PyNum(p * ExtraShare, true)));
            var sum = new PyNum(pts[0] + a.V, a.F);                 // pts[0] + sum(...)
            var cap = new PyNum(pts[0] * GroupCap, true);           // pts[0] * GROUP_CAP
            var points = Round1(cap.V < sum.V ? cap : sum);          // min() 相等时取第一个
            var objects = Py.Sorted(fs.Select(f => Py.ToStr(Py.Get(f, "object"))).Distinct());
            var c = cats[cat];
            c.deductions.Add(new JsonObject
            {
                ["rule"] = Py.Clone(Py.Get(fs[0], "rule")),
                ["flagIds"] = Py.List(fs.Select(f => Py.Clone(Py.Get(f, "id")))),
                ["objects"] = Py.StrList(objects),
                ["points"] = points.Node,
            });
            var diff = Round1(new PyNum(c.score.V - points.V, c.score.F || points.F));
            c.score = diff.V > 0 ? diff : PyNum.Int(0); // max(0, x)：x 不大于 0 时返回整数 0
            cats[cat] = c;
        }
        foreach (var f in flagList)
        {
            if (cleared.Contains(Id(f)) && PenaltyOf(f) != 0)
                cats[Category(f)].deductions.Add(new JsonObject
                {
                    ["rule"] = Py.Clone(Py.Get(f, "rule")),
                    ["flagIds"] = new JsonArray(Py.Clone(Py.Get(f, "id"))),
                    ["objects"] = new JsonArray(Py.ToStr(Py.Get(f, "object"))),
                    ["points"] = 0,
                    ["note"] = notes[Id(f)],
                });
        }
        var total = PySum(Weights.Select(w => new PyNum(cats[w.name].weight * cats[w.name].score.V / 100, true)));
        var categories = new JsonObject();
        foreach (var (name, _) in Weights)
        {
            var c = cats[name];
            categories[name] = new JsonObject { ["weight"] = c.weight, ["score"] = c.score.Node, ["deductions"] = c.deductions };
        }
        return new JsonObject { ["total"] = Py.RoundInt(total.V), ["categories"] = categories };
    }
}
