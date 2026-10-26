using System.Text.Json.Nodes;

namespace Pcdoctor.Core;

/// <summary>
/// 校验模型输出（对应 judge/validate.py，设计文档第 6 章"输出校验"）。
/// 模型说什么都不直接算数：操作编号不在操作库里、不适用于该报告节、碰到红线的，一律改成"先问用户"；
/// 风险等级由操作库决定；拿不准的结论必须带一个问题。所有改动记进 issues。
/// </summary>
public static class Validation
{
    public static readonly IReadOnlySet<string> Verdicts = new HashSet<string> { "normal", "problem", "leftover", "bloat", "suspicious", "uncertain" };
    public const double MinConfidence = 0.6;

    /// <summary>_context()</summary>
    static JsonNode? Context(JsonObject item, JsonNode evidence)
    {
        if (item.ContainsKey("context")) return Py.Or(item["context"], new JsonObject());
        var refKey = Py.S(item["contextRef"]);
        var v = refKey is null ? null : Py.Get(Py.Get(evidence, "sharedContext"), refKey);
        return Py.Or(v, new JsonObject());
    }

    /// <summary>_red_line()：返回违反的红线说明；没有违反返回 null。</summary>
    static string? RedLine(string op, List<JsonObject> items, JsonNode evidence)
    {
        foreach (var it in items)
        {
            var ctx = Context(it, evidence);
            if (Oplib.DriverOps.Contains(op) && Py.S(it["section"]) == "drivers")
            {
                var signer = Py.StrOr(Py.Get(ctx, "signer"));
                var company = Py.StrOr(Py.Get(ctx, "company"));
                if (signer.StartsWith("Microsoft", StringComparison.Ordinal) || company.StartsWith("Microsoft", StringComparison.Ordinal))
                    return "红线 3：不动微软签名或 Windows 自带的驱动";
                var startMode = Py.S(Py.Get(ctx, "startMode"));
                if (op == "driver.remove_after_reboot" && Py.S(Py.Get(ctx, "state")) == "Running" && startMode is "Boot" or "System")
                    return "红线 4：不动系统启动早期加载且正在运行的第三方驱动";
                if (Py.Truthy(Py.Get(ctx, "classFilters")) && op != "driver.remove_after_reboot")
                    return "红线 2：挂在设备过滤器上的驱动只能先摘除过滤器、重启后再处理";
            }
        }
        return null;
    }

    /// <summary>_as_float()：转成 0–1 之间的浮点数，转不了算 0。</summary>
    static double AsFloat(JsonNode? v)
    {
        try
        {
            var x = Py.ToFloat(v);
            // Python 的 max(0.0, min(1.0, x))：NaN 比较都为假，min 返回 1.0
            var m = double.IsNaN(x) ? 1.0 : Math.Min(1.0, x);
            return Math.Max(0.0, m);
        }
        catch (FormatException)
        {
            return 0.0;
        }
    }

    /// <summary>validate()：返回 (clean, issues)。</summary>
    public static (JsonObject clean, List<string> issues) Validate(JsonNode? output, JsonNode evidence)
    {
        var issues = new List<string>();
        var itemsById = new Dictionary<string, JsonObject>();
        var idOrder = new List<JsonNode?>();
        foreach (var it in Py.Iter(Py.Get(evidence, "items")).OfType<JsonObject>())
        {
            var k = Py.Key(it["flagId"]);
            if (!itemsById.ContainsKey(k)) idOrder.Add(it["flagId"]);
            itemsById[k] = it;
        }
        if (output is not JsonObject outObj)
            return (new JsonObject { ["findings"] = new JsonArray() }, ["模型输出不是 JSON 对象"]);

        var findings = new JsonArray();
        int i = 0;
        foreach (var fNode in Py.Iter(Py.Or(outObj["findings"], new JsonArray())))
        {
            i++;
            if (fNode is not JsonObject f)
            {
                issues.Add($"第 {i} 条 finding 不是对象，已丢弃");
                continue;
            }
            var objStr = Py.ToStr(f["object"]);
            var rawIds = Py.Iter(Py.Or(f["flagIds"], new JsonArray())).ToList();
            var ids = rawIds.Where(x => itemsById.ContainsKey(Py.Key(x))).ToList();
            var unknown = rawIds.Where(x => !itemsById.ContainsKey(Py.Key(x))).ToList();
            if (unknown.Count > 0) issues.Add($"{objStr}：引用了不存在的 flagId {Py.ReprList(unknown)}，已忽略");
            if (ids.Count == 0)
            {
                issues.Add($"{objStr}：没有有效的 flagId，整条丢弃");
                continue;
            }
            var items = ids.Select(x => itemsById[Py.Key(x)]).ToList();

            var verdictNode = f["verdict"];
            var verdict = Py.S(verdictNode);
            if (verdict is null || !Verdicts.Contains(verdict))
            {
                issues.Add($"{objStr}：结论 {Py.Repr(verdictNode)} 不合法，改为 uncertain");
                verdict = "uncertain";
            }
            var confidence = AsFloat(f["confidence"]);

            var opNode = Py.Or(f["proposedAction"], JsonValue.Create("none"));
            var op = Py.S(opNode);
            if (op is null || !Oplib.Operations.ContainsKey(op))
            {
                issues.Add($"{objStr}：操作 {Py.Repr(opNode)} 不在操作库里，改为先问用户");
                op = "ask_user";
            }
            else
            {
                var operation = Oplib.Operations[op];
                var bad = operation.Sections.Contains("*") ? []
                    : items.Select(it => it["section"]).Where(s => !(Py.S(s) is { } ss && operation.Sections.Contains(ss)))
                        .Select(Py.ToStr).Distinct().ToList();
                bad = Py.Sorted(bad);
                var onlyRules = operation.Rules;
                if (bad.Count > 0)
                {
                    issues.Add($"{objStr}：操作 {op} 不适用于 {Py.ReprList(bad)}，改为先问用户");
                    op = "ask_user";
                }
                else if (onlyRules is { Count: > 0 } && !items.Any(it => Py.S(it["rule"]) is { } r && onlyRules.Contains(r)))
                {
                    issues.Add($"{objStr}：操作 {op} 只能用于 {Py.ReprList(Py.Sorted(onlyRules))} 规则命中的对象，改为先问用户");
                    op = "ask_user";
                }
            }
            var line = RedLine(op, items, evidence);
            if (line is not null)
            {
                issues.Add($"{objStr}：建议的 {op} 触犯{line}，改为先问用户");
                op = "ask_user";
            }

            JsonNode? question = Py.Truthy(f["questionForUser"]) ? f["questionForUser"]!.DeepClone() : null;
            // 提示注入的保险：规则判为高风险、或数据里有像是写给 AI 的指令的项，模型判"正常"不算数（用户确认过的除外）
            var risky = items.Where(it => (Py.S(it["severity"]) == "high" || Py.Truthy(it["injectionSuspect"])) && !it.ContainsKey("userConfirmed")).ToList();
            if (verdict == "normal" && risky.Count > 0)
            {
                var why = risky.Any(it => Py.Truthy(it["injectionSuspect"])) ? "数据里有像是写给 AI 的指令" : "规则判为高风险";
                issues.Add($"{objStr}：{why}，模型却判为正常，改为拿不准并请用户确认");
                verdict = "uncertain";
                question = $"规则认为这一项风险较高（{why}），但 AI 判断为正常。这是你认识、自己安装的东西吗？";
            }
            var needsUser = verdict == "uncertain" || confidence < MinConfidence || op == "ask_user";
            if (needsUser && !Py.Truthy(question))
            {
                question = $"这一项需要你确认：{objStr}（{(Py.Truthy(f["userExplanation"]) ? Py.ToStr(f["userExplanation"]) : "原因见证据")}）";
                issues.Add($"{objStr}：需要问用户但没给问题，已补默认问题");
            }

            var operationFinal = Oplib.Operations[op];
            var clean = new JsonObject
            {
                ["flagIds"] = Py.List(ids.Select(Py.Clone)),
                ["object"] = Py.Clone(f["object"]),
                ["verdict"] = verdict,
                ["confidence"] = confidence,
                ["evidence"] = Py.Clone(f["evidence"]),
                ["proposedAction"] = op,
                ["actionName"] = operationFinal.Name,
                ["risk"] = operationFinal.Risk, // 风险由操作库决定
                ["userExplanation"] = Py.Clone(f["userExplanation"]),
                ["questionForUser"] = needsUser ? question : null,
                ["needsUser"] = needsUser,
            };
            if (Py.Truthy(f["kbId"])) clean["kbId"] = Py.Clone(f["kbId"]); // 知识库直接给出的结论（Judge 保证模型伪造不了）
            findings.Add(clean);
        }

        if (findings.Any(f => Py.S(f!["verdict"]) == "suspicious") && !Py.Truthy(outObj["malwareSuspected"]))
            issues.Add("结论里有\"可疑\"，但整体判断为\"没有中毒迹象\"，前后不一致（未修改，留给人工复核）");

        var covered = findings.SelectMany(f => Py.Iter(f!["flagIds"])).Select(Py.Key).ToHashSet();
        var missing = idOrder.Where(x => !covered.Contains(Py.Key(x))).ToList();
        if (missing.Count > 0) issues.Add($"有 {missing.Count} 条预筛结果没有被研判：{Py.ReprList(missing)}");

        var tips = new JsonArray();
        foreach (var t in Py.Iter(Py.Or(outObj["tips"], new JsonArray())).Take(3))
        {
            if (t is JsonObject to && Py.Truthy(to["title"]) && Py.Truthy(to["body"]))
                tips.Add(new JsonObject
                {
                    ["title"] = Py.ToStr(to["title"]),
                    ["body"] = Py.ToStr(to["body"]),
                    ["relatedFlagIds"] = Py.List(Py.Iter(Py.Or(to["relatedFlagIds"], new JsonArray()))
                        .Where(x => itemsById.ContainsKey(Py.Key(x))).Select(Py.Clone)),
                });
        }

        var result = new JsonObject
        {
            ["headline"] = Py.StrOr(outObj["headline"]),
            ["malwareSuspected"] = Py.Truthy(outObj["malwareSuspected"]),
            ["malwareReasoning"] = Py.StrOr(outObj["malwareReasoning"]),
            ["top3"] = Py.StrList(Py.Iter(Py.Or(outObj["top3"], new JsonArray())).Select(Py.ToStr).Take(3)),
            ["findings"] = findings,
            ["tips"] = tips,
            ["notCollected"] = Py.StrList(Py.Iter(Py.Or(outObj["notCollected"], new JsonArray())).Select(Py.ToStr)),
            ["unjudgedFlagIds"] = Py.List(missing.Select(Py.Clone)),
        };
        return (result, issues);
    }

    /// <summary>cleared_flag_ids()：研判为正常且置信度足够的 flag 编号，不再扣健康分。</summary>
    public static HashSet<string> ClearedFlagIds(JsonNode clean)
    {
        var s = new HashSet<string>();
        foreach (var f in Py.Iter(Py.Get(clean, "findings")))
            if (Py.S(Py.Get(f, "verdict")) == "normal" && Py.Num(Py.Get(f, "confidence")) >= MinConfidence)
                foreach (var x in Py.Iter(Py.Get(f, "flagIds")))
                    s.Add(Py.ToStr(x));
        return s;
    }
}
