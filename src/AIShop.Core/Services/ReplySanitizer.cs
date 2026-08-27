using System.Text.RegularExpressions;

namespace AIShop.Core.Services;

/// <summary>
/// LLM 回复清洗器 — 唯一来源（消除 Api/Service 双份复制）。
/// 清除商品 ID 展示格式：#5、商品Id:4、商品ID为 4 等，避免暴露内部 ID。
/// </summary>
public static class ReplySanitizer
{
    private static readonly Regex HashIdPattern =
        new(@"#(?<id>\d+)", RegexOptions.None, TimeSpan.FromSeconds(1));
    private static readonly Regex FixedIdPattern =
        new(@"商品Id[:：]\d+", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
    private static readonly Regex ProductIdLabelPattern =
        new(@"商品ID[\s:：为是]*\d+", RegexOptions.None, TimeSpan.FromSeconds(1));
    private static readonly Regex PricePunctuationPattern =
        new(@"[，,、;；]+\s*(?=[¥￥$])", RegexOptions.None, TimeSpan.FromSeconds(1));

    private const int MinProductId = 1;
    private const int MaxProductId = 18;

    /// <summary>
    /// 清洗完整回复文本：去商品 ID + 清残留标点 + Trim
    /// </summary>
    public static string Clean(string? reply)
    {
        var text = ApplyPatterns(reply ?? "");
        return text.Trim();
    }

    /// <summary>
    /// 增量清洗（流式）：返回可安全发送的前缀和剩余缓冲区
    /// </summary>
    public static (string SafeToEmit, string Remaining) CleanIncremental(
        string newText, string buffer)
    {
        var fullText = buffer + newText;
        int safePos = fullText.Length;

        var fixedMatch = FixedIdPattern.Match(fullText);
        if (fixedMatch.Success && fixedMatch.Index < safePos)
            safePos = fixedMatch.Index;

        var hashMatch = HashIdPattern.Match(fullText);
        if (hashMatch.Success && hashMatch.Index < safePos)
            safePos = hashMatch.Index;

        var labelMatch = ProductIdLabelPattern.Match(fullText);
        if (labelMatch.Success && labelMatch.Index < safePos)
            safePos = labelMatch.Index;

        if (safePos == fullText.Length)
        {
            if (!EndsWithPatternPrefix(fullText))
                return (fullText, "");
            return ("", fullText);
        }

        return (fullText[..safePos], fullText[safePos..]);
    }

    /// <summary>应用全部清洗正则（不含 Trim）</summary>
    public static string ApplyPatterns(string text)
    {
        text = FixedIdPattern.Replace(text, "");
        text = HashIdPattern.Replace(text, static match =>
            IsProductId(match.Groups["id"].Value) ? "" : match.Value);
        text = ProductIdLabelPattern.Replace(text, "");
        text = PricePunctuationPattern.Replace(text, "");
        return text;
    }

    /// <summary>判断文本尾部是否可能是模式前缀（流式安全边界检查）</summary>
    public static bool EndsWithPatternPrefix(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        var tail = text.Length > 10 ? text[^10..] : text;

        if (tail.EndsWith('#')) return true;
        if (tail.Contains("商品Id", StringComparison.OrdinalIgnoreCase) ||
            tail.Contains("商品ID", StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    private static bool IsProductId(string idText) =>
        int.TryParse(idText, out var id) && id is >= MinProductId and <= MaxProductId;
}
