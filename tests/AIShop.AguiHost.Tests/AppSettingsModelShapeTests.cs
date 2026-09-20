using System.Text.Json;

namespace AIShop.AguiHost.Tests;

/// <summary>
/// AguiHost 模型配置<b>形状</b>校验（原 T8「两份 appsettings 的模型配置一致」，2026-09-20 范围收窄后由
/// <c>AppSettingsModelParityTests</c> 改名）。
///
/// <para>
/// <b>为什么收窄</b>：AG-UI 的三个变更只针对 <c>AIShop.AguiHost</c>，<c>AIShop.Api</c> 不属其范围（用户裁决）。
/// 原用例 <c>Models_AreIdenticalAcrossApiAndAguiHost</c> 把两个宿主绑在一起 —— <c>AIShop.Api</c> 侧独立调整模型名时，
/// AguiHost 的提交会被与之无关的红灯拦住。故改为<b>只校验 AguiHost 自身</b>。
/// </para>
/// <para>
/// <b>失去的保护（知情接受）</b>：两个宿主的模型清单从此可各自漂移而无人拦截。跨宿主一致性由
/// <c>AIShop.Api</c> 的归属方自行保证；若要恢复，请连同本文件的断言一起改回，<b>勿只改名字</b>。
/// </para>
/// <para>
/// <b>与 <c>AguiModelsEndpointTests</c> 的分工</b>：后者用硬编码期望值锁死 <c>GET /models</c> 的<b>响应内容</b>
/// （具体 id/name/model/isDefault）；本用例补的是<b>源文件层</b>的形状（字段齐备、<c>ActiveModel</c> 可解析）。
/// 两者不重复：端点若被改成硬编码供数，本用例仍能发现配置漂移。
/// </para>
///
/// 配置文件按【仓库根】定位（自测试输出目录上溯找 <c>AIShop.sln</c>），而非相对测试进程 CWD——
/// xUnit 的 CWD 是输出目录，那里只有被复制的 appsettings 副本，路径解析错会让断言失真
/// （agui-session-prod S8 教训）。文件缺失/解析失败一律显式失败，不得静默跳过。
/// </summary>
public sealed class AppSettingsModelShapeTests
{
    private const string SolutionFileName = "AIShop.sln";
    private const string AguiHostProject = "AIShop.AguiHost";

    /// <summary>
    /// AguiHost <c>Models</c> 节形状：非空；每项含非空 <c>Endpoint</c> / <c>Model</c> / <c>Name</c>；
    /// <c>ActiveModel</c> 指向一个已存在的 <c>Models</c> 键。
    /// <b>不断言具体字面值</b>——模型名会随额度/供应商变化（如 qwen 免费额度耗尽时切换供应商），
    /// 锁字面值会误伤正常运维；具体值由 <c>AguiModelsEndpointTests</c> 锁。
    /// </summary>
    [Fact]
    public void AguiHostModels_AreWellFormed()
    {
        using var agui = LoadSettings(ResolveSettingsPath(AguiHostProject));
        var root = agui.RootElement;

        // ① Models 节存在且非空（清空该节会让 /models 返回空数组、登录页无模型可选）
        Assert.True(root.TryGetProperty("Models", out var models), "AguiHost/appsettings.json 缺 Models 节");
        var keys = models.EnumerateObject().Select(p => p.Name).OrderBy(k => k, StringComparer.Ordinal).ToArray();
        Assert.NotEmpty(keys);

        // ② 每项三字段齐备且非空（缺任一字段都会让 IModelChatClientFactory 构造出不可用的模型条目）
        foreach (var entry in models.EnumerateObject())
        {
            foreach (var field in new[] { "Endpoint", "Model", "Name" })
            {
                Assert.True(entry.Value.TryGetProperty(field, out var value),
                    $"Models.{entry.Name} 缺 {field} 字段");
                Assert.False(string.IsNullOrWhiteSpace(value.GetString()),
                    $"Models.{entry.Name}.{field} 为空");
            }
        }

        // ③ ActiveModel 必须指向已存在的键（否则 /models 无 isDefault 项，前端登录页与顶栏徽标都没有默认选中）
        Assert.True(root.TryGetProperty("ActiveModel", out var active), "AguiHost/appsettings.json 缺 ActiveModel");
        var activeName = active.GetString();
        Assert.Contains(activeName, keys);
    }

    /// <summary>
    /// 缺失 / 解析失败必须显式失败（不得静默跳过——静默会让断言空转）。
    /// 两条路径各验一次：不存在的路径 → <see cref="FileNotFoundException"/>；非法 JSON → <see cref="InvalidDataException"/>。
    /// </summary>
    [Fact]
    public void LoadSettings_WhenFileMissingOrMalformed_FailsExplicitly()
    {
        var missingPath = Path.Combine(Path.GetTempPath(), $"ais-hop-missing-{Guid.NewGuid():N}.json");
        Assert.Throws<FileNotFoundException>(() => LoadSettings(missingPath));

        var malformedPath = Path.Combine(Path.GetTempPath(), $"ais-hop-malformed-{Guid.NewGuid():N}.json");
        File.WriteAllText(malformedPath, "{ \"Models\": { \"qwen\": ");
        try
        {
            Assert.Throws<InvalidDataException>(() => LoadSettings(malformedPath));
        }
        finally
        {
            File.Delete(malformedPath);
        }
    }

    /// <summary>
    /// 读取并解析一份 appsettings.json。文件缺失抛 <see cref="FileNotFoundException"/>、内容非法抛
    /// <see cref="InvalidDataException"/>（两者都让用例失败，绝不静默跳过）；调用方负责 Dispose 返回的文档。
    /// </summary>
    private static JsonDocument LoadSettings(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"期望存在的配置文件缺失：{path}（形状断言不得静默通过）", path);

        try
        {
            return JsonDocument.Parse(File.ReadAllText(path));
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"配置文件解析失败：{path}", ex);
        }
    }

    /// <summary>自 <see cref="AppContext.BaseDirectory"/> 逐级上溯，返回 <c>src/{宿主项目}/appsettings.json</c> 的绝对路径。</summary>
    private static string ResolveSettingsPath(string hostProject)
    {
        var repoRoot = FindRepositoryRoot()
            ?? throw new InvalidOperationException(
                $"未找到仓库根：自测试输出目录 {AppContext.BaseDirectory} 逐级上溯均未见 {SolutionFileName}，无法定位 {hostProject}/appsettings.json");
        return Path.Combine(repoRoot, "src", hostProject, "appsettings.json");
    }

    /// <summary>自 <see cref="AppContext.BaseDirectory"/> 逐级上溯，返回首个含 <c>AIShop.sln</c> 的目录；找不到返回 null。</summary>
    private static string? FindRepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, SolutionFileName)))
                return dir.FullName;
        }

        return null;
    }
}
