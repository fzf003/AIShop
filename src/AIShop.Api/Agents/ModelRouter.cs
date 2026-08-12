using AIShop.AgentTelemetry;
using AIShop.Core.StaticData;
using AIShop.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using OpenAI;
using Serilog;
using System.ClientModel;
using System.ClientModel.Primitives;
using System.Collections.Concurrent;
using System.Net.Sockets;

namespace AIShop.Api.Agents;

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
            var dbFactory = _sp.GetRequiredService<IDbContextFactory<AppDbContext>>();
            var cartTools = _sp.GetRequiredService<CartToolProvider>();
            var isOpenAI = ShoppingAssistantAgent.IsOpenAIModel(cfg.Model);
            var telemetryOptions = _sp.GetRequiredService<AgentTelemetryOptions>();
            var agent = new ShoppingAssistantAgent(chatClient, dbFactory, ProductKeywordMap.Entries, cartTools, isOpenAI, telemetryOptions);
            Logger.Information("GetAgent.Lazy: 创建成功 model={ModelName}", key);
            return agent;
        })).Value;
    }

    /// <summary>获取当前激活模型的默认 Agent 实例。</summary>
    public virtual IShoppingAssistantAgent GetDefaultAgent() => GetAgent(_activeModel);

    private IChatClient CreateChatClient(ModelConfig cfg)
    {
        var handler = new HttpClientHandler { UseProxy = false, Proxy = null };

        // 始终挂 DebugHandler，内部按开关透明转发：默认关闭（不打印、不读流），排查时 AgentTelemetry:DebugHandler=true 开启
        var httpHandler = new DebugHandler(handler, _enableDebugHandler);

        // 统一路径：所有模型经 DeepSeekDelegatingChatClient 清洗 + 分流
        var isDeepSeek = cfg.Name.Contains("DeepSeek", StringComparison.OrdinalIgnoreCase);

        // DeepSeek 需绕过 MEAI 序列化，自建 HTTP Client
        HttpClient? httpClient = null;
        IChatClient chatClient;

        if (isDeepSeek)
        {
            var deepSeekHttpClient = new HttpClient(httpHandler) { Timeout = TimeSpan.FromSeconds(120) };
            deepSeekHttpClient.BaseAddress = new Uri(cfg.Endpoint + "/chat/completions");
            deepSeekHttpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {cfg.Key}");

            httpClient = deepSeekHttpClient;
            // DeepSeek 不走 inner，DelegatingChatClient 需要一个空壳
            chatClient = new DeepSeekChatClient(deepSeekHttpClient, cfg.Model);
        }
        else
        {
            var dehttpClient = new HttpClient(httpHandler) { Timeout = TimeSpan.FromSeconds(120) };
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

 
    
}
