using System.Net;
using System.Text;
using System.Text.Json;
using AIShop.Service.Agui;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace AIShop.AguiHost.Tests;

/// <summary>
/// agui-client-support T2：根级 <c>GET /models</c> 端点契约 + 「端点层不二次解析模型配置」的静态源码断言。
/// 覆盖 spec ADDED R1（模型清单端点：清单与配置一致 / 顺序确定不随请求变化 / 无需身份即可读取）与
/// R2（清单来源单一：静态源码断言 + 行为断言由 <c>AguiModelClientFactoryTests</c> 承担）。
/// <para>
/// 驱动方式：WAF 真实宿主（<see cref="WebApplicationFactory{TEntryPoint}"/>，内容根 = <c>src/AIShop.AguiHost</c>，
/// 读真实 <c>appsettings.json</c>）+ <b>不替换</b>模型工厂 seam——本类的断言对象正是「真实工厂读真实配置」的产物
/// （替换成 stub 会让「清单与 appsettings 一致」自证）。宿主启动不需要真实模型密钥：工厂构造只解析配置、
/// 底层客户端懒建（首个请求才会构建），故离线可启动。
/// </para>
/// <para>
/// 挂 <c>[Collection(nameof(AguiRequestTests))]</c>（约束 D）：本类启动真实宿主（MigrateAsync / 播种 / RAG 预热），
/// 必须与其它宿主级测试串行，避免多个宿主并行迁移同一 SQLite 文件库。
/// </para>
/// </summary>
[Collection(nameof(AguiRequestTests))]
public sealed class AguiModelsEndpointTests
{
    /// <summary>
    /// 端点层「模型配置节读取」的禁止模式（spec R2 场景 1：确认<b>不含模型配置节访问</b>与默认模型直接读取）。
    /// <para>
    /// 说明为什么用「配置访问形态」而不是裸标识 <c>ActiveModel</c>：<c>Program.cs</c> 内本就存在与 <c>/models</c>
    /// 无关的既有标识符（<c>SetActiveModel</c> / <c>IActiveModelProvider</c>，属 C5 请求级模型注入中间件，
    /// 是 <c>ActiveModel</c> 的子串），用裸标识检索会对这些无关命中假红。裸标识检索仅对<b>本工单新增</b>的
    /// <c>AguiEndpoints.cs</c> 保留（见 <see cref="ModelsEndpointSource_DoesNotReparseModelConfiguration"/>）。
    /// </para>
    /// </summary>
    private static readonly string[] ModelConfigAccessPatterns =
    [
        "GetSection(\"Models\")",
        "GetSection(\"ActiveModel\")",
        "[\"Models\"]",
        "[\"ActiveModel\"]",
    ];

    /// <summary>AguiHost 的模型仓库根相对路径（约束 E：从 <see cref="AppContext.BaseDirectory"/> 上溯定位）。</summary>
    private const string EndpointsSourcePath = "src/AIShop.AguiHost/AguiEndpoints.cs";

    /// <summary><c>/models</c> 的注册点（spec R2 场景 1 要求一并检查）。</summary>
    private const string ProgramSourcePath = "src/AIShop.AguiHost/Program.cs";

    [Fact]
    public async Task GetModels_ReturnsConfiguredModelList_MatchingAguiHostAppSettings()
    {
        // spec R1 场景 1：Models 节含 qwen/deepseek/gpt-4.1（ActiveModel=gpt-4.1）→ 200 + 长度 3 的 JSON 数组，
        // 每项 id/name/model 与该节配置一致，且仅 gpt-4.1 项 isDefault = true。
        // 期望值按 src/AIShop.AguiHost/appsettings.json 硬编码（不读 IConfiguration 反推——否则断言自证）。
        using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/models");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(body);
        var items = json.RootElement.EnumerateArray().ToList();

        Assert.Equal(3, items.Count);
        // 期望序 = 子键序数升序（deepseek < gpt-4.1 < qwen），非 appsettings 书写序（spec R1 第 4 段）
        AssertModel(items[0], expectedId: "deepseek", expectedName: "DeepSeek", expectedModel: "deepseek-v4-flash", expectedDefault: false);
        AssertModel(items[1], expectedId: "gpt-4.1", expectedName: "Mimo", expectedModel: "mimo-v2.5", expectedDefault: true);
        AssertModel(items[2], expectedId: "qwen", expectedName: "Qwen 3.7", expectedModel: "kimi-k3", expectedDefault: false);

        // 响应不含敏感信息（spec R1 第 5 段：模型密钥与端点地址不在响应中）
        Assert.DoesNotContain("Endpoint", body);
        Assert.DoesNotContain("dashscope", body);
        Assert.DoesNotContain("Key", body);
    }

    [Fact]
    public async Task GetModels_TwoConsecutiveCallsWithDifferentUnrelatedHeaders_ReturnSameOrderAsFactoryList()
    {
        // spec R1 场景 2：连续两次请求（一次带无关 Origin 头、一次不带）→ 数组元素顺序完全相同，
        // 且等于工厂 AvailableModels 的顺序（Models 子键序数升序，本配置下 [deepseek, gpt-4.1, qwen]），与请求上下文无关。
        using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();

        var first = await ReadModelIdsAsync(client, origin: "http://agui-client-tests.example");
        var second = await ReadModelIdsAsync(client, origin: null);

        Assert.Equal(first, second);
        Assert.Equal(new[] { "deepseek", "gpt-4.1", "qwen" }, first);

        // 顺序口径与工厂暴露的清单一一对应（端点层未重排、未另起解析）
        var factoryOrder = factory.Services.GetRequiredService<IModelChatClientFactory>()
            .AvailableModels.Select(m => m.Id).ToArray();
        Assert.Equal(factoryOrder, first);
    }

    [Fact]
    public async Task GetModels_WithoutAnyIdentity_ReturnsFullList()
    {
        // spec R1 场景 4：客户端尚未选定任何身份（不带 username 等身份信息）→ 200 与完整清单，不返回 401/404、
        // 不要求任何身份信息（登录页在选定身份之前就需要该清单）。
        using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/models");
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Headers.Contains("WWW-Authenticate"), "公开可读端点不应发出认证挑战");

        var ids = await ReadModelIdsAsync(client, origin: null);
        Assert.Equal(new[] { "deepseek", "gpt-4.1", "qwen" }, ids);
    }

    [Fact]
    public void ModelsEndpointSource_DoesNotReparseModelConfiguration()
    {
        // spec R2 场景 1（静态源码断言）：沿仓库根上溯定位 /models 的实现文件与注册点，确认端点层不含
        // 模型配置节读取、不直接读取默认模型——模型数据只来自 IModelChatClientFactory.AvailableModels。
        var endpointsSource = ReadRepoFile(EndpointsSourcePath);
        var programSource = ReadRepoFile(ProgramSourcePath);

        // 正向：实现文件确实经工厂清单供数（否则「数据来源单一」无从谈起）
        Assert.Contains("IModelChatClientFactory", endpointsSource);
        Assert.Contains("AvailableModels", endpointsSource);

        // 端点实现文件是全新文件 → 按 tasks T2 口径另加裸标识检索（连注释都不提默认模型键）
        Assert.DoesNotContain("IConfiguration", endpointsSource);
        Assert.DoesNotContain("ActiveModel", endpointsSource);

        // 两份源码均不得出现模型配置节访问（任一出现即说明端点层重新解析了配置）
        foreach (var pattern in ModelConfigAccessPatterns)
        {
            Assert.DoesNotContain(pattern, endpointsSource);
            Assert.DoesNotContain(pattern, programSource);
        }

        // 反向/装配：注册点确实调用了扩展方法（否则 /models 根本没被映射）
        Assert.Contains("MapSupportEndpoints", programSource);
    }

    /// <summary>断言清单项四字段（属性名同时锁定 camelCase 契约：PascalCase 契约下 GetProperty 会抛 KeyNotFoundException）。</summary>
    private static void AssertModel(JsonElement item, string expectedId, string expectedName, string expectedModel, bool expectedDefault)
    {
        Assert.Equal(expectedId, item.GetProperty("id").GetString());
        Assert.Equal(expectedName, item.GetProperty("name").GetString());
        Assert.Equal(expectedModel, item.GetProperty("model").GetString());
        Assert.Equal(expectedDefault, item.GetProperty("isDefault").GetBoolean());
    }

    /// <summary>请求 <c>GET /models</c>（<paramref name="origin"/> 非 null 时带无关 <c>Origin</c> 头），返回响应中的模型 id 序列。</summary>
    private static async Task<string[]> ReadModelIdsAsync(HttpClient client, string? origin)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/models");
        if (origin is not null)
            request.Headers.Add("Origin", origin);

        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return [.. json.RootElement.EnumerateArray().Select(item => item.GetProperty("id").GetString() ?? "")];
    }

    /// <summary>
    /// 仓库根定位（约束 E）：从 <see cref="AppContext.BaseDirectory"/> 上溯找含 <c>AIShop.sln</c> 的目录。
    /// <b>不用</b> <c>Path.GetFullPath(相对路径)</c>——它相对测试进程 CWD（bin/Debug/net10.0）解析，会让断言空转。
    /// </summary>
    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "AIShop.sln")))
                return dir.FullName;
        }

        throw new InvalidOperationException($"未能从 {AppContext.BaseDirectory} 上溯找到含 AIShop.sln 的仓库根");
    }

    /// <summary>读取仓库内文件全文；<b>期望存在的文件缺失即显式失败</b>（不得静默跳过，否则断言空转）。</summary>
    private static string ReadRepoFile(string relativePath)
    {
        var fullPath = Path.Combine(FindRepoRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(fullPath))
            throw new FileNotFoundException($"期望存在的仓库文件缺失：{fullPath}", fullPath);

        return File.ReadAllText(fullPath);
    }
}
