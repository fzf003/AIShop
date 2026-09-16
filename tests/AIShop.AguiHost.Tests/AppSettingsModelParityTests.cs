using System.Text.Json;

namespace AIShop.AguiHost.Tests;

/// <summary>
/// T8 跨宿主模型配置一致性（spec ADDED「两份 appsettings 的模型配置一致（<c>ActiveModel</c> 除外）」）：
/// <c>src/AIShop.Api/appsettings.json</c> 与 <c>src/AIShop.AguiHost/appsettings.json</c> 的
/// <c>Models</c> 节键集合、同名键的 <c>Model</c> 值两项必须一致（以 AguiHost 为准）。
/// <c>ActiveModel</c> 两宿主<b>有意各自独立</b>（<c>AIShop.Api = qwen</c>、<c>AguiHost = gpt-4.1</c>），
/// <b>不参与断言</b>——把老链缺省模型换成指向 MiMo 的 <c>gpt-4.1</c> 会引入间歇性工具调用失效（design §7.2）。
/// 本变更只对齐配置值（design §7.2 方案 A），漂移由本测试锁死。
///
/// 两份文件按【仓库根】定位（自测试输出目录上溯找 <c>AIShop.sln</c>），而非相对测试进程 CWD——
/// xUnit 的 CWD 是输出目录，那里只有被复制的 appsettings 副本，路径解析错会让断言失真
/// （agui-session-prod S8 教训）。任一份缺失/解析失败一律显式失败，不得静默跳过。
/// </summary>
public sealed class AppSettingsModelParityTests
{
    private const string SolutionFileName = "AIShop.sln";
    private const string ApiHostProject = "AIShop.Api";
    private const string AguiHostProject = "AIShop.AguiHost";

    /// <summary>
    /// 一致性断言本体（spec 场景「一致性被测试锁定」）：① <c>Models</c> 键集合相同
    /// ② 同名键的 <c>Model</c> 值相同。任一份文件的键集合或 <c>Model</c> 值被单独修改，本用例即失败；
    /// <c>ActiveModel</c> 有意不参与（见类注释）。
    /// </summary>
    [Fact]
    public void Models_AreIdenticalAcrossApiAndAguiHost()
    {
        using var api = LoadSettings(ResolveSettingsPath(ApiHostProject));
        using var agui = LoadSettings(ResolveSettingsPath(AguiHostProject));

        var apiModels = api.RootElement.GetProperty("Models");
        var aguiModels = agui.RootElement.GetProperty("Models");

        // ① 键集合相同：排序后逐项比对（顺序不参与断言，两份文件的书写序可以不同）
        var apiKeys = apiModels.EnumerateObject().Select(p => p.Name).OrderBy(k => k, StringComparer.Ordinal).ToArray();
        var aguiKeys = aguiModels.EnumerateObject().Select(p => p.Name).OrderBy(k => k, StringComparer.Ordinal).ToArray();
        Assert.True(apiKeys.SequenceEqual(aguiKeys),
            $"Models 节键集合不一致：{ApiHostProject}=[{string.Join(", ", apiKeys)}]，{AguiHostProject}=[{string.Join(", ", aguiKeys)}]");

        // ② 同名键的 Model 值相同（AguiHost 为准）
        foreach (var key in aguiKeys)
        {
            var aguiModel = aguiModels.GetProperty(key).GetProperty("Model").GetString();
            var apiModel = apiModels.GetProperty(key).GetProperty("Model").GetString();
            Assert.True(string.Equals(aguiModel, apiModel, StringComparison.Ordinal),
                $"Models.{key}.Model 不一致：{ApiHostProject}=\"{apiModel}\"，{AguiHostProject}=\"{aguiModel}\"");
        }
    }

    /// <summary>
    /// 缺失 / 解析失败必须显式失败（不得静默跳过——静默会让一致性断言空转）。
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
            throw new FileNotFoundException($"期望存在的配置文件缺失：{path}（一致性断言不得静默通过）", path);

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
