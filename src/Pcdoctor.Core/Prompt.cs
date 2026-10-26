namespace Pcdoctor.Core;

/// <summary>研判系统提示（对应 run_judge.py 的 load_system_prompt）。</summary>
public static class Prompt
{
    const string Marker = "## 系统提示";

    /// <summary>读嵌入的 judge-prompt.md，取「## 系统提示」之后的部分，把 {{OPERATIONS}} 换成操作表。</summary>
    public static string LoadSystemPrompt() => FromMarkdown(Resources.ReadText(Resources.JudgePrompt));

    public static string FromMarkdown(string text)
    {
        var start = text.IndexOf(Marker, StringComparison.Ordinal);
        if (start < 0) throw new FormatException($"提示词文件里没有「{Marker}」一节");
        var body = text[(start + Marker.Length)..].Trim();
        return body.Replace("{{OPERATIONS}}", Oplib.OperationsTable());
    }
}
