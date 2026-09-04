using System.Text.Json;
using Microsoft.Extensions.AI;

namespace AIShop.Service.Tests;

/// <summary>
/// 诊断：验证 search_product 带可选参数 category 时，AIFunctionFactory 生成的 schema
/// 是否把 category 标成 required（若是，LLM 不传 category 会调用失败 → Error: Function failed）。
/// </summary>
public sealed class ToolSchemaDebug
{
    [Fact]
    public void Lambda_CategoryParameter_IsRequired()
    {
        // 根因确认：AIFunctionFactory 用 lambda 注册，参数无法携带默认值 → 全部标 required
        var fn = AIFunctionFactory.Create(
            (Func<string, string?, Task<string>>)((keyword, category) => Task.FromResult(keyword ?? "")),
            "search_product", "搜索商品");
        Assert.Contains("category", RequiredOf(fn));
    }

    [Fact]
    public void MethodGroup_DefaultParameter_IsOptional()
    {
        // 方法组（保留真实方法信息）→ AIFunctionFactory 能读到参数默认值 → 可选参数 optional
        var fn = AIFunctionFactory.Create((Func<string, string, Task<string>>)Demo, "demo", "d");
        Assert.DoesNotContain("category", RequiredOf(fn));
    }

    private static Task<string> Demo(string keyword, string category = "") => Task.FromResult(keyword);

    private static List<string?> RequiredOf(AIFunction fn)
    {
        var json = JsonSerializer.Serialize(fn.JsonSchema);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("required", out var req) && req.ValueKind == JsonValueKind.Array)
            return req.EnumerateArray().Select(e => e.GetString()).ToList();
        return [];
    }
}
