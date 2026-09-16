using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using AIShop.AguiHost;
using AIShop.AguiHost.Model;
using AIShop.Core.Entities;
using AIShop.Core.Interfaces;
using AIShop.Infrastructure.MemoryService;
using AIShop.Service;
using AIShop.Service.Agui;
using Mem0Sharp;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using Meai = Microsoft.Extensions.AI;

namespace AIShop.AguiHost.Tests;

/// <summary>
/// agui-client-support T3：显式 <c>forwardedProps.username</c> 的存在性校验（404 短路）。
/// 覆盖 spec ADDED R3（显式 username 存在性校验）、R4（缺省回落路径不读库 / 非 AG-UI POST 不受影响）、
/// R5（校验不等于认证）与 MODIFIED R12（校验叠加在注入之前）。
/// <para>
/// 驱动方式：WAF 真实宿主（Program）+ 脚本化模型工厂（免 Key 离线）+ <see cref="IUserRepository"/> 替身
/// （NSubstitute，可精确断言「是否读库 / 读库次数」）+ 三套独立临时库（业务 / 会话 / 聊天历史，经 Program 的
/// T16 环境变量 seam <c>Agui__*Connection</c> 注入，见 <see cref="AguiE2ETests"/> 同款手法）——临时库使「零副作用」
/// 断言可直查 SQLite，且不污染仓库内的 agui.db 与老库。
/// </para>
/// <para>
/// 挂 <c>[Collection(nameof(AguiRequestTests))]</c>（约束 D）：本类启动真实宿主并操作进程级环境变量，
/// 必须与其它宿主级测试串行，避免并行迁移同一 SQLite 文件库 / 互相污染环境变量 seam。
/// </para>
/// </summary>
[Collection(nameof(AguiRequestTests))]
public sealed class AguiUsernameValidationTests : IDisposable
{
    /// <summary>脚本化回复里的 ASCII 标记，便于在 SSE 事件流中断言文本已到达（非 404 路径）。</summary>
    private const string SimulatedText = "T3-MARKER 已为您处理请求。";

    /// <summary>老 <c>POST /api/login</c> 的错误体形状（spec R3 要求对齐）。</summary>
    private const string UserNotFoundBody = "\"detail\":\"User not found\"";

    private readonly List<string> _cleanupDirs = [];
    private readonly List<(string Key, string? Previous)> _envRestore = [];

    private readonly IUserRepository _userRepository = Substitute.For<IUserRepository>();
    private readonly ICurrentUserAccessor _accessor = Substitute.For<ICurrentUserAccessor>();

    private string _businessDbPath = "";
    private string _sessionDbPath = "";
    private string _chatDbPath = "";

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();

        // 恢复环境变量（host 构建完成后即恢复；残留会污染同进程后续测试的 Program 顶层 seam 读取）
        foreach (var (key, previous) in _envRestore)
            Environment.SetEnvironmentVariable(key, previous);
        _envRestore.Clear();

        // 删除临时库目录（业务 / 向量 / 会话 / 聊天历史各独立文件）
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
    public async Task PostRoot_ExplicitUsernameNotInUserTable_Returns404WithLoginErrorShape()
    {
        // spec R3 场景 1 前半 + design §5.2 行为矩阵「查无此人 → 404 + {detail:User not found}」：
        // 显式携带 username=nobody 而用户表查无此人 → 普通 HTTP 404（不是 SSE 事件流）+ 老 /api/login 同形错误体。
        _userRepository.GetByUsernameAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((User?)null);

        using var factory = StartFactory();
        using var client = factory.CreateClient();

        using var response = await client.PostAsync(
            "/",
            new StringContent(RunAgentBody("t3-nobody-thread", username: "nobody"), Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains(UserNotFoundBody, body);

        // 校验确实查过用户表（证明 404 来自存在性校验，而非端点自身的其它 404 分支）
        await _userRepository.Received(1).GetByUsernameAsync("nobody", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PostRoot_ExplicitUsernameNotInUserTable_ProducesNoSessionChatOrCartSideEffects()
    {
        // spec R3 场景 1 后半「不得进入 Agent、不得写入任何会话快照 / 聊天历史 / 购物车 / 记忆数据」：
        // 404 短路发生在 MapAGUIServer 之前 → 三套独立临时库里不应出现任何行（表未建亦视为零写入）。
        _userRepository.GetByUsernameAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((User?)null);

        var factory = StartFactory();
        using var client = factory.CreateClient();

        using var response = await client.PostAsync(
            "/",
            new StringContent(RunAgentBody("t3-nobody-sideeffect-thread", username: "nobody"), Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        // 先释放宿主（避免连接池持锁），再直查临时库
        await factory.DisposeAsync();
        SqliteConnection.ClearAllPools();

        Assert.Equal(0, await CountTableRowsAsync(_sessionDbPath, "agent_sessions"));
        Assert.Equal(0, await CountTableRowsAsync(_chatDbPath, "chat_messages"));
        Assert.Equal(0, await CountTableRowsAsync(_businessDbPath, "Carts"));
        Assert.Equal(0, await CountTableRowsAsync(_businessDbPath, "CartItems"));
    }

    [Fact]
    public async Task PostRoot_ExplicitUsernameInUserTable_PassesThroughAndInjectsUsername()
    {
        // spec R3 场景 2 + MODIFIED R12 场景 1：用户表含 marla → 放行进入 Agent，执行流注入的用户名就是 marla。
        _userRepository.GetByUsernameAsync("marla", Arg.Any<CancellationToken>()).Returns(NewUser("marla"));

        using var factory = StartFactory();
        using var client = factory.CreateClient();

        using var response = await client.PostAsync(
            "/",
            new StringContent(RunAgentBody("t3-marla-thread", username: "marla"), Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("T3-MARKER", await response.Content.ReadAsStringAsync());

        _accessor.Received(1).SetCurrentUser("marla");
    }

    [Fact]
    public async Task PostRoot_NoUsername_FallsBackToDefaultUsername_WithoutQueryingUserRepository()
    {
        // spec R4 场景 1 + MODIFIED R12 场景 1 末段：无 forwardedProps.username → 注入缺省用户，且【不读库】。
        using var factory = StartFactory();
        using var client = factory.CreateClient();

        using var response = await client.PostAsync(
            "/",
            new StringContent(RunAgentBody("t3-default-thread", username: null), Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("T3-MARKER", await response.Content.ReadAsStringAsync());

        _accessor.Received(1).SetCurrentUser(AguiUsernameForwarder.DefaultUsername);

        // 缺省回落路径零开销：中间件不得触碰用户仓储（spec R4「该路径 MUST NOT 查询用户表」）
        await _userRepository.DidNotReceive().GetByUsernameAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PostRoot_UserRepositoryThrows_RequestFailsNotSilentlyAllowed()
    {
        // spec R3 第 4 段：读库失败 MUST NOT 被当作「校验通过」静默放行（异常上抛为 5xx，乐观降级会掩盖数据层故障）。
        _userRepository
            .GetByUsernameAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<Task<User?>>(_ => throw new InvalidOperationException("模拟用户表读取故障"));

        using var factory = StartFactory();
        using var client = factory.CreateClient();

        using var response = await client.PostAsync(
            "/",
            new StringContent(RunAgentBody("t3-repo-throw-thread", username: "marla"), Encoding.UTF8, "application/json"));

        Assert.True(
            (int)response.StatusCode >= 500,
            $"读库异常必须上抛为 5xx（不静默放行），实际状态码 = {(int)response.StatusCode}");
    }

    [Fact]
    public async Task PostNonAguiEndpoint_WithoutUsername_IsNotShortCircuitedByUsernameValidation()
    {
        // spec R4 场景 2（否定性断言）：对 Development 下的 OpenAI wire 端点（/v1/responses）发【不带 username】的 POST，
        // 不得因本校验被短路、响应体不得出现 User not found、且不得触发用户仓储查询。
        // 状态码不作约束（该端点有自身错误语义，见 tasks §七 E 与 design §10）。
        using var factory = StartFactory();
        using var client = factory.CreateClient();

        using var response = await client.PostAsync(
            "/v1/responses",
            new StringContent("{\"model\":\"AGUIShopping\",\"input\":\"你好\"}", Encoding.UTF8, "application/json"));

        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("User not found", body);
        await _userRepository.DidNotReceive().GetByUsernameAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PostRoot_ExistingUsernameWithoutCredentials_PassesThroughPerSpecR5()
    {
        // spec R5 场景 1：客户端以 username=steve 发起请求且【未提供任何凭证】→ 放行并按其用户名读写。
        // 本条是【预期行为】（不是缺陷）：校验只证明「该用户名存在」，不证明请求方是该用户——身份由请求体自称。
        // 相关注释与文档严禁把本校验表述为登录校验 / 认证 / 鉴权。
        _userRepository.GetByUsernameAsync("steve", Arg.Any<CancellationToken>()).Returns(NewUser("steve"));

        using var factory = StartFactory();
        using var client = factory.CreateClient();

        using var response = await client.PostAsync(
            "/",
            new StringContent(RunAgentBody("t3-steve-thread", username: "steve"), Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("T3-MARKER", await response.Content.ReadAsStringAsync());

        // 无任何凭证仍被放行，且执行流按自称用户名归属数据
        _accessor.Received(1).SetCurrentUser("steve");
    }

    /// <summary>构造一个种子用户实体（仅 Username 对校验有意义的断言面）。</summary>
    private static User NewUser(string username) => new() { Username = username, DisplayName = username };

    /// <summary>
    /// 装配 WAF 真实宿主：临时业务 / 向量 / 会话 / 聊天历史四套库（Program T16 环境变量 seam）+ 脚本化模型工厂
    /// （免 Key 离线，agent 聊天底层经 RouterChatClient → 工厂）+ <see cref="IUserRepository"/> /
    /// <see cref="ICurrentUserAccessor"/> 替身（记录读库次数与 SetCurrentUser 调用）。
    /// </summary>
    /// <remarks>
    /// 同时 RemoveAll Mem0 记忆服务三件套（<see cref="IMemoryService"/> / <see cref="IMemoryStore"/> /
    /// <c>SqliteMemoryStore</c>）：记忆链会用全局纯净 <c>IChatClient</c>（= 脚本化 mock）做 LLM 提取，
    /// 移除后 mock 只服务 Agent 工具链路，且不产生额外记忆库文件——记忆不属于本工单断言语义。
    /// 环境变量在 <c>CreateClient()</c>（触发 host 构建、Program 顶层读取 seam 的时点）之后立即恢复，防进程级污染。
    /// </remarks>
    private WebApplicationFactory<Program> StartFactory()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"agui_t3_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _cleanupDirs.Add(dir);

        _businessDbPath = Path.Combine(dir, "business.db");
        _sessionDbPath = Path.Combine(dir, "sessions.db");
        _chatDbPath = Path.Combine(dir, "chat.db");

        SetEnvironment("Agui__DbConnection", $"Data Source={_businessDbPath}");
        SetEnvironment("Agui__RagConnection", $"Data Source={Path.Combine(dir, "rag.db")}");
        SetEnvironment("Agui__SessionConnection", $"Data Source={_sessionDbPath}");
        SetEnvironment("Agui__ChatConnection", $"Data Source={_chatDbPath}");

        try
        {
            var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<IModelChatClientFactory>();
                    services.AddSingleton<IModelChatClientFactory>(new StubModelChatClientFactory(CreateMockChatClient()));

                    // IUserRepository 是 Scoped（EF DbContext）：替换为替身（同一实例返回给每个 scope，
                    // 使中间件内 CreateScope() 解析到的仍是本测试可断言的替身）。
                    services.RemoveAll<IUserRepository>();
                    services.AddScoped<IUserRepository>(_ => _userRepository);

                    services.RemoveAll<ICurrentUserAccessor>();
                    services.AddSingleton<ICurrentUserAccessor>(_accessor);

                    services.RemoveAll<IMemoryService>();
                    services.RemoveAll<IMemoryStore>();
                    services.RemoveAll<SqliteMemoryStore>();
                });
            });

            // CreateClient() 触发 host 构建（Program 顶层读取 seam 配置的时点），构建完成后即可恢复环境变量
            _ = factory.CreateClient();
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

    /// <summary>
    /// 直查指定 SQLite 文件某表行数（零副作用断言用）：文件不存在 → 0；表不存在 → 0（都表示「未写入」）。
    /// 计数语句按表名分支写成字面量（不做 SQL 字符串拼接，避免注入面与静态分析告警）。
    /// </summary>
    private static async Task<long> CountTableRowsAsync(string dbPath, string table)
    {
        if (!File.Exists(dbPath))
            return 0;

        await using var connection = new SqliteConnection($"Data Source={dbPath}");
        await connection.OpenAsync();

        await using (var existsCommand = connection.CreateCommand())
        {
            existsCommand.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $name";
            existsCommand.Parameters.AddWithValue("$name", table);
            if ((long)(await existsCommand.ExecuteScalarAsync() ?? 0L) == 0)
                return 0;
        }

        await using var countCommand = connection.CreateCommand();
        countCommand.CommandText = table switch
        {
            "agent_sessions" => "SELECT COUNT(*) FROM agent_sessions",
            "chat_messages" => "SELECT COUNT(*) FROM chat_messages",
            "Carts" => "SELECT COUNT(*) FROM Carts",
            "CartItems" => "SELECT COUNT(*) FROM CartItems",
            _ => throw new ArgumentOutOfRangeException(nameof(table), table, "未覆盖的表名"),
        };
        return (long)(await countCommand.ExecuteScalarAsync() ?? 0L);
    }

    /// <summary>脚本化文本回复的 chatClient：非流式/流式入口均返回固定文本（ChatClientAgent 走流式）。</summary>
    private static Meai.IChatClient CreateMockChatClient()
    {
        var mockChat = Substitute.For<Meai.IChatClient>();
        mockChat.GetResponseAsync(
                Arg.Any<IEnumerable<Meai.ChatMessage>>(),
                Arg.Any<Meai.ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(new Meai.ChatResponse(new Meai.ChatMessage(Meai.ChatRole.Assistant, SimulatedText)));
        mockChat.GetStreamingResponseAsync(
                Arg.Any<IEnumerable<Meai.ChatMessage>>(),
                Arg.Any<Meai.ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(StreamingTextAsync(SimulatedText));
        return mockChat;
    }

    /// <summary>单条文本增量更新流（ChatClientAgent 走 GetStreamingResponseAsync，yield 一次即结束流）。</summary>
    private static async IAsyncEnumerable<Meai.ChatResponseUpdate> StreamingTextAsync(string text)
    {
        yield return new Meai.ChatResponseUpdate(Meai.ChatRole.Assistant, text);
    }

    /// <summary>
    /// 构造 AG-UI RunAgentInput 形状的请求体 JSON；<paramref name="username"/> 非 null 才带 forwardedProps.username
    /// （null = 模拟「客户端未携带 username」）。消息 id 每次唯一 GUID，避免同 Thread 复用时被按 id 判重合并。
    /// </summary>
    private static string RunAgentBody(string threadId, string? username)
    {
        var root = new JsonObject
        {
            ["threadId"] = threadId,
            ["messages"] = new JsonArray(
                new JsonObject
                {
                    ["id"] = $"m-{Guid.NewGuid():N}",
                    ["role"] = "user",
                    ["content"] = "你好",
                }),
        };

        if (username is not null)
            root["forwardedProps"] = new JsonObject { ["username"] = username };

        return root.ToJsonString();
    }
}
