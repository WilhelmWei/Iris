using System.Text.Json.Nodes;

namespace Pcdoctor.Core;

/// <summary>
/// 用户画像（judge/userprofile.py 的移植）：「关于这台电脑」里的回答和问卷回答，清洗后放进证据包的 userProfile。
/// 都是用户自己填的文字，同样只当数据看待；长度和条数都有上限。
/// </summary>
public static class UserProfile
{
    static List<string> Strs(JsonNode? v, int n, int m)
    {
        var outList = new List<string>();
        if (v is not JsonArray a) return outList;
        foreach (var x in a)
        {
            if (Py.S(x) is { } s && Py.Strip(s).Length > 0) outList.Add(Py.Head(Py.Strip(s), m));
            if (outList.Count >= n) break;
        }
        return outList;
    }

    /// <summary>clean_profile()：什么都没填时返回 null（证据包里就不出现 userProfile）。</summary>
    public static JsonObject? Clean(JsonNode? p)
    {
        if (p is not JsonObject o) return null;
        var result = new JsonObject();
        var uses = Strs(o["uses"], 6, 20);
        if (uses.Count > 0) result["uses"] = Py.StrList(uses);
        var pains = Strs(o["pains"], 6, 20);
        if (pains.Count > 0) result["pains"] = Py.StrList(pains);
        if (Py.KindOf(o["portable"]) == Py.Kind.Bool) result["portable"] = o["portable"]!.GetValue<bool>();
        if (Py.S(o["who"]) is { } who && Py.Strip(who).Length > 0) result["who"] = Py.Head(Py.Strip(who), 20);
        if (Py.S(o["note"]) is { } note && Py.Strip(note).Length > 0) result["note"] = Py.Head(Py.Strip(note), 300);
        var answers = new JsonArray();
        if (o["answers"] is JsonArray raw)
            foreach (var a in raw.Skip(Math.Max(0, raw.Count - 10)))
            {
                if (a is not JsonObject ao) continue;
                if (Py.S(ao["question"]) is not { } q || Py.Strip(q).Length == 0 || Py.S(ao["answer"]) is not { } ans || Py.Strip(ans).Length == 0) continue;
                var item = new JsonObject { ["question"] = Py.Head(Py.Strip(q), 120), ["answer"] = Py.Head(Py.Strip(ans), 200) };
                var ids = Strs(ao["flagIds"], 10, 10);
                if (ids.Count > 0) item["flagIds"] = Py.StrList(ids);
                answers.Add(item);
            }
        if (answers.Count > 0) result["answers"] = answers;
        return result.Count > 0 ? result : null;
    }
}
