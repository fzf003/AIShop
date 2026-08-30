using AIShop.AgentTelemetry;
using AIShop.Core.Interfaces;
using AIShop.Core.StaticData;
using AIShop.Service.Clients;
using AIShop.Service.Tools;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using OpenAI;
using Polly;
using Serilog;
using System.ClientModel;
using System.ClientModel.Primitives;
using System.Collections.Concurrent;

namespace AIShop.Service;

public record ModelInfo(string Id, string Name, bool IsDefault);

public class ModelRouter
{
    private static readonly Serilog.ILogger Logger = Log.ForContext<ModelRouter>();

    private readonly IReadOnlyDictionary<string, ModelConfig> _modelConfigs;
    private readonly string _activeModel;
    private readonly IServiceProvider _sp;
    private readonly ConcurrentDictionary<string, Lazy<ShoppingAssistantAgent>> _agents = new(StringComparer.OrdinalIgnoreCase);
    private readonly bool _enableDebugHandler;

    internal sealed record ModelConfig(string Endpoint, string Key, string Model, string Name);

    public virtual string ActiveModel => _activeModel;

    /// <summary>用于测试的受保护无参构造函数。</summary>
    protected ModelRouter()
    {
        _modelConfigs = new Dictionary<string, ModelConfig>(StringComparer.OrdinalIgnoreCase);
        _activeModel = string.Empty;
        _sp = null!;
    }

    public ModelRouter(IConfiguration configuration, IServiceProvider sp)
    {
        _sp = sp;
        // DebugHandler 默认关闭（控制台不打印 LLM 报文、不读流）；排查时经 AgentTelemetry:DebugHandler=true 开启
        _enableDebugHandler = configuration.GetValue<bool?>("AgentTelemetry:DebugHandler") ?? false;
        var modelsSection = configuration.GetSection("Models");
        var openaiSection = configuration.GetSection("OpenAI");

        if (modelsSection.Exists() && modelsSection.GetChildren().Any())
        {
            // 新版格式："Models" 节下多个子节
            var models = new Dictionary<string, ModelConfig>(StringComparer.OrdinalIgnoreCase);

            foreach (var section in modelsSection.GetChildren())
            {
                var config = new ModelConfig(
                    section["Endpoint"] ?? "",
                    section["Key"] ?? "",
                    section["Model"] ?? "",
                    section["Name"] ?? section.Key
                );
                models[section.Key] = config;
            }

            _modelConfigs = models;
            _activeModel = configuration["ActiveModel"] ?? models.Keys.FirstOrDefault() ?? "";
        }
        else if (openaiSection.Exists())
        {
            // 向后兼容：旧版 "OpenAI" 节 → 包装为单模型
            Logger.Warning("检测到旧版 OpenAI 配置格式，请迁移至新版 Models 节");

            var modelValue = openaiSection["Model"] ?? "legacy";
            _modelConfigs = new Dictionary<string, ModelConfig>(StringComparer.OrdinalIgnoreCase)
            {
                ["legacy"] = new ModelConfig(
                    openaiSection["Endpoint"] ?? "",
                    openaiSection["Key"] ?? "",
                    modelValue,
                    modelValue
                )
            };

            _activeModel = "legacy";
        }
        else
        {
            throw new InvalidOperationException(
                "未找到模型配置。请在 appsettings.json 中配置 \"Models\" 节（新版格式），" +
                "或保留旧版 \"OpenAI\" 节以启用向后兼容。");
        }
    }

    public virtual IEnumerable<ModelInfo> GetAvailableModels()
    {
        foreach (var (id, config) in _modelConfigs)
        {
            yield return new ModelInfo(
                id,
                config.Name,
                string.Equals(id, _activeModel, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>获取指定模型对应的 Agent 实例（首次访问时延迟创建，后续复用）。</summary>
    /// <exception cref="KeyNotFoundException">模型未注册时抛出。</exception>
    public virtual IShoppingAssistantAgent GetAgent(string modelName)
    {
        var containsKey = _modelConfigs.ContainsKey(modelName);
        Logger.Information("GetAgent: modelName={ModelName} ContainsKey={ContainsKey} ConfigKeys=[{Keys}]",
            modelName, containsKey, string.Join(",", _modelConfigs.Keys));

        if (!containsKey)
            throw new KeyNotFoundException($"模型 '{modelName}' 未注册");

        return _agents.GetOrAdd(modelName, key => new Lazy<ShoppingAssistantAgent>(() =>
        {
            var cfg = _modelConfigs[key];
            Logger.Information("GetAgent.Lazy: 开始创建 model={ModelName} endpoint={Endpoint}", key, cfg.Endpoint);
            var chatClient = CreateChatClient(cfg);
            var chatHistoryStore = _sp.GetRequiredService<IChatHistoryStore>();
            var compaction = _sp.GetRequiredService<IChatCompactionPolicy>();
            var cartTools = _sp.GetRequiredService<CartToolProvider>();
            var isOpenAI = ShoppingAssistantAgent.IsOpenAIModel(cfg.Model);
            var telemetryOptions = _sp.GetRequiredService<AgentTelemetryOptions>();
            // 知识检索服务（IRagSearchService，AddRag 注册）可选注入：未注册（RAG 未启用）时为 null，
            // ShoppingAssistantAgent 不挂载 TextSearchProvider（search_knowledge 工具随之缺席），保持既有行为
            var ragSearchService = _sp.GetService<IRagSearchService>();
            var agent = new ShoppingAssistantAgent(
                chatClient, chatHistoryStore, compaction, ProductKeywordMap.Entries, cartTools, isOpenAI, telemetryOptions,
                _sp.GetRequiredService<IPreferenceQueue>(),
                _sp.GetRequiredService<IServiceScopeFactory>(),
                ragSearchService);
            Logger.Information("GetAgent.Lazy: 创建成功 model={ModelName}", key);
            return agent;
        })).Value;
    }

    /// <summary>获取当前激活模型的默认 Agent 实例。</summary>
    public virtual IShoppingAssistantAgent GetDefaultAgent() => GetAgent(_activeModel);

    private IChatClient CreateChatClient(ModelConfig cfg)
    {
        var handler = new HttpClientHandler { UseProxy = false, Proxy = null };

        // 统一路径：所有模型经 DeepSeekDelegatingChatClient 清洗 + 分流
        var isDeepSeek = cfg.Name.Contains("DeepSeek", StringComparison.OrdinalIgnoreCase);

        // DeepSeek 需绕过 MEAI 序列化，自建 HTTP Client
        HttpClient? httpClient = null;
        IChatClient chatClient;

        if (isDeepSeek)
        {
            // DeepSeek 路径接入标准弹性策略（429/5xx/网络抖动自动重试）；DebugHandler 在管线内层、每次重试尝试可见
            var deepSeekHttpClient = new HttpClient(BuildChatHttpPipeline(handler, _enableDebugHandler)) { Timeout = TimeSpan.FromSeconds(120) };
            deepSeekHttpClient.BaseAddress = new Uri(cfg.Endpoint + "/chat/completions");
            deepSeekHttpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {cfg.Key}");

            httpClient = deepSeekHttpClient;
            // DeepSeek 不走 inner，DelegatingChatClient 需要一个空壳
            chatClient = new DeepSeekChatClient(deepSeekHttpClient, cfg.Model);
        }
        else
        {
            // OpenAI/Qwen 路径同样接入标准弹性策略；与 DeepSeek 各自构建、不共享同一 DelegatingHandler 实例
            var dehttpClient = new HttpClient(BuildChatHttpPipeline(handler, _enableDebugHandler)) { Timeout = TimeSpan.FromSeconds(120) };
            var clientOptions = new OpenAIClientOptions
            {
                Endpoint = new Uri(cfg.Endpoint),
                Transport = new HttpClientPipelineTransport(dehttpClient),
            };
            var client = new OpenAIClient(new ApiKeyCredential(cfg.Key), clientOptions);
            chatClient = client.GetChatClient(cfg.Model).AsIChatClient();

            // Qwen 补上修复层
            if (!ShoppingAssistantAgent.IsOpenAIModel(cfg.Model))
                chatClient = new QwenToolCallFixClient(chatClient);
        }
 
        return chatClient.AsBuilder()
            .Use(client => new DeepSeekDelegatingChatClient(client, httpClient, cfg.Model))
            .Build();
    }

    /// <summary>
    /// 构建自建 HttpClient 的 HTTP 管线（标准弹性策略 + DebugHandler 链）——T11 测试缝。
    /// 外层 <see cref="ResilienceHandler"/> 承载标准弹性策略，内层 <see cref="DebugHandler"/> 按开关透明转发——
    /// 因包在 ResilienceHandler 内层，每次重试尝试都可见，不破坏既有 DebugHandler 包装链。
    /// DeepSeek 与 OpenAI/Qwen 两条路径各自调用本方法构建，不共享同一 DelegatingHandler 实例；
    /// HttpClient.Timeout=120s 留在外层作硬天花板（&gt; 弹性 TotalRequestTimeout=110s，弹性先到点、外层兜底，无双重超时冲突）。
    /// 注意：Microsoft.Extensions.Http.Resilience 10.7.0（ServiceDefaults 锁定）无
    /// ResiliencePipelineBuilder.AddStandardResilienceHandler 重载（仅 IHttpClientBuilder 有），
    /// 故用公开策略积木等效复现标准弹性——总请求超时（最外层）+ 标准 HTTP 重试 + 熔断。
    /// </summary>
    internal static HttpMessageHandler BuildChatHttpPipeline(HttpMessageHandler inner, bool enableDebugHandler)
    {
        var pipeline = new ResiliencePipelineBuilder<HttpResponseMessage>()
            .AddTimeout(TimeSpan.FromSeconds(110))                      // TotalRequestTimeout=110s：整体预算，弹性先到点
            .AddRetry(new HttpRetryStrategyOptions())                   // 标准重试：IsTransient（429/5xx/网络抖动）自动重试 ≤3 次
            .AddCircuitBreaker(new HttpCircuitBreakerStrategyOptions()) // 标准熔断：持续失败时快速失败、防上游被打爆
            .Build();

        return new ResilienceHandler(pipeline)
        {
            InnerHandler = new DebugHandler(inner, enableDebugHandler)
        };
    }
}
