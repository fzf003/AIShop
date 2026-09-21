using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace AIShop.AguiHost.Tests;

/// <summary>
/// L3 缺陷（<c>docs/agui-convergence-inventory.md</c>）的<b>结构性防回归</b>（B 防护）：
/// 「未挂 <see cref="AguiClientRestEndpoint"/> 标记的写端点会走 AG-UI 分支、静默用缺省用户身份写数据，
/// 且返回 200 假成功」。
/// <para>
/// <see cref="AguiUsernameForwarder.UseAguiUsernameForwarding"/> 的分支依据是<b>端点元数据</b>：挂
/// <see cref="AguiClientRestEndpoint"/> 的端点走 REST 身份（<c>?username=</c>），其余写方法端点回落
/// <see cref="AguiUsernameForwarder.DefaultUsername"/>。当前唯一的写端点（<c>POST /cart/items</c>）挂在
/// <c>/cart</c> 组级标记下，故目前<b>没有实际中招</b>——这是潜在缺陷，缺的是「将来有人新增写端点却忘记挂标记」
/// 时能立刻变红的约束。
/// </para>
/// <para>
/// 本用例遍历宿主<b>真实映射</b>的全部端点，断言「写方法（POST / PUT / DELETE / PATCH）端点必须挂
/// <see cref="AguiClientRestEndpoint"/>」；已有意走 AG-UI 分支的端点（<c>POST /</c> 与 Development 下的
/// DevUI / OpenAI wire）挂 <see cref="AguiStreamEndpoint"/>，据此排除（它们不是漏挂）。
/// </para>
/// <para>
/// 挂 <c>[Collection(nameof(AguiRequestTests))]</c>（约束 D）：本类启动真实宿主（MigrateAsync / 播种 / RAG 预热）
/// 并操作进程级环境变量 seam，必须与其它宿主级测试串行。
/// </para>
/// </summary>
[Collection(nameof(AguiRequestTests))]
public sealed class AguiWriteEndpointMarkerTests : IDisposable
{
    private readonly List<string> _cleanupDirs = [];
    private readonly List<(string Key, string? Previous)> _envRestore = [];
    private readonly List<WebApplicationFactory<Program>> _factories = [];

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();

        foreach (var factory in _factories)
            factory.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _factories.Clear();

        // 恢复环境变量（host 构建完成后即恢复；残留会污染同进程后续测试的 Program 顶层 seam 读取）
        foreach (var (key, previous) in _envRestore)
            Environment.SetEnvironmentVariable(key, previous);
        _envRestore.Clear();

        foreach (var dir in _cleanupDirs)
        {
            try
            {
                if (Directory.Exists(dir))
                    Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
                // 文件仍被占用时忽略，交由系统清理
            }
        }
        _cleanupDirs.Clear();
    }

    [Fact]
    public void AllWriteEndpoints_MustCarryRestEndpointMarker()
    {
        // B 防护：任何写方法端点若不挂 AguiClientRestEndpoint（也非 AG-UI 流端点），其请求会落 AG-UI 分支、
        // 以缺省用户身份写数据并返回 200 假成功——本断言让「将来有人漏挂标记」当场变红。
        var factory = StartFactory();

        var endpoints = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints;

        var writeEndpoints = new List<Endpoint>();
        var offenders = new List<string>();

        foreach (var endpoint in endpoints)
        {
            var methods = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods;
            if (methods is null || !methods.Any(IsWriteMethod))
                continue;

            // 排除有意走 AG-UI 分支的端点（POST / 与 Development 下的 DevUI / OpenAI wire）——
            // 它们挂 AguiStreamEndpoint，不是「漏挂标记」，不在此约束内。
            if (endpoint.Metadata.GetMetadata<AguiStreamEndpoint>() is not null)
                continue;

            writeEndpoints.Add(endpoint);

            if (endpoint.Metadata.GetMetadata<AguiClientRestEndpoint>() is null)
                offenders.Add($"{endpoint.DisplayName} [{string.Join(", ", methods)}]");
        }

        // 反空转：必须真的枚举到写端点（否则本断言恒真、零断言力）——当前至少有 /cart 的 4 个写端点。
        Assert.NotEmpty(writeEndpoints);

        Assert.True(
            offenders.Count == 0,
            "以下写方法端点未挂 AguiClientRestEndpoint 标记："
            + "其请求会落 AG-UI 分支、以缺省用户（steve）身份写数据并返回 200 假成功（L3 缺陷）。"
            + "修法：给该端点的 Map* 调用挂 .WithMetadata(new AguiClientRestEndpoint())；"
            + "若它确实走 AG-UI 分支，则改挂 AguiStreamEndpoint 以表明「有意为之」。"
            + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    /// <summary>写方法判定（会改变服务端状态的 HTTP 方法）：POST / PUT / DELETE / PATCH（与中间件同口径）。</summary>
    private static bool IsWriteMethod(string method)
        => HttpMethods.IsPost(method)
            || HttpMethods.IsPut(method)
            || HttpMethods.IsDelete(method)
            || HttpMethods.IsPatch(method);

    /// <summary>
    /// 装配 WAF 真实宿主：临时业务 / 向量 / 会话 / 聊天历史四套库（Program T16 环境变量 seam
    /// <c>Agui__*Connection</c>），<b>不替换任何服务</b>——本用例只读「端点元数据」这一路由层事实，
    /// 需要的是<b>真实映射</b>后的完整端点表（含 Development 下的 DevUI / OpenAI wire）。
    /// </summary>
    private WebApplicationFactory<Program> StartFactory()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"agui_l3_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _cleanupDirs.Add(dir);

        SetEnvironment("Agui__DbConnection", $"Data Source={Path.Combine(dir, "business.db")}");
        SetEnvironment("Agui__RagConnection", $"Data Source={Path.Combine(dir, "rag.db")}");
        SetEnvironment("Agui__SessionConnection", $"Data Source={Path.Combine(dir, "sessions.db")}");
        SetEnvironment("Agui__ChatConnection", $"Data Source={Path.Combine(dir, "chat.db")}");

        try
        {
            var factory = new WebApplicationFactory<Program>();
            // CreateClient() 触发 host 构建（Program 顶层读取 seam 配置的时点），构建完成后即可恢复环境变量
            _ = factory.CreateClient();
            _factories.Add(factory);
            return factory;
        }
        finally
        {
            RestoreEnvironment();
        }
    }

    private void SetEnvironment(string key, string value)
    {
        _envRestore.Add((key, Environment.GetEnvironmentVariable(key)));
        Environment.SetEnvironmentVariable(key, value);
    }

    private void RestoreEnvironment()
    {
        foreach (var (key, previous) in _envRestore)
            Environment.SetEnvironmentVariable(key, previous);
        _envRestore.Clear();
    }
}
