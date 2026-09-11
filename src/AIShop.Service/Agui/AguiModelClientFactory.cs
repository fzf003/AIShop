using System.Collections.Concurrent;
using System.ClientModel;
using AIShop.AgentTelemetry;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using OpenAI;

namespace AIShop.Service.Agui;

/// <summary>
/// 「模型 → 底层客户端」工厂实现（agui-model-switch C5 M1）。
/// 读 <c>Models</c> 节（多模型子节 qwen/deepseek/gpt-4.1，各含 Endpoint/Key/Model/Name）+ 顶层 <c>ActiveModel</c>，
/// 解析语义同老 Service <c>ModelRouter</c>（src/AIShop.Service/ModelRouter.cs L53-71 只读参照）但独立实现、不引用老类型。
/// 每模型底层 chatClient 懒建并缓存（<see cref="ConcurrentDictionary{TKey,TValue}"/> + <see cref="Lazy{T}"/>，
/// 模型 id <see cref="StringComparer.OrdinalIgnoreCase"/>），每客户端统一外包 OTel 遥测——RouterChatClient 切到
/// 任一模型（含非默认）时 <c>gen_ai</c> 遥测链路都可见。
/// </summary>
/// <remarks>
/// MVP 说明：每模型底层 = OpenAI 兼容直连（<c>OpenAIClient → GetChatClient(Model).AsIChatClient()</c>），
/// qwen / gpt-4.1 / deepseek 端点 OpenAI 兼容都走这条路径。老 <c>ModelRouter.CreateChatClient</c> 的
/// DeepSeek 自建 HTTP 清洗链（<c>DeepSeekChatClient</c>/<c>DeepSeekDelegatingChatClient</c>）与
/// <c>QwenToolCallFixClient</c> 工具调用修复中间件按需提炼属后续（spec 非目标，MVP 不并入）。
/// OTel 外包刻意移入工厂每客户端（而非留在全局 seam 注册处）：Router 可切任意模型，保证非默认模型同样有遥测；
/// 全局 seam ＝ <see cref="GetDefaultClient"/> 也自然覆盖。
/// </remarks>
public sealed class AguiModelClientFactory : IModelChatClientFactory
{
    /// <summary>单模型配置（仿老 <c>ModelRouter.ModelConfig</c>）：Endpoint/Key/Model 取节值，Name 缺省取节键。</summary>
    private sealed record ModelConfig(string Endpoint, string Key, string Model, string Name);

    private readonly IReadOnlyDictionary<string, ModelConfig> _models;
    private readonly AgentTelemetryOptions _telemetryOptions;
    private readonly ConcurrentDictionary<string, Lazy<IChatClient>> _clients;

    /// <summary>缺省模型 id（＝ ActiveModel；缺失时取 Models 首键）。</summary>
    public string DefaultModelId { get; }

    public AguiModelClientFactory(IConfiguration configuration, AgentTelemetryOptions telemetryOptions)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(telemetryOptions);
        _telemetryOptions = telemetryOptions;

        // 解析 Models 节（同老 ModelRouter L53-71）：无 Models 节 / 空节 → InvalidOperationException。
        var modelsSection = configuration.GetSection("Models");
        if (!modelsSection.Exists() || !modelsSection.GetChildren().Any())
        {
            throw new InvalidOperationException(
                "未找到模型配置。请在 appsettings.json 中配置 \"Models\" 节（含 Endpoint/Key/Model/Name 子节）。");
        }

        var models = new Dictionary<string, ModelConfig>(StringComparer.OrdinalIgnoreCase);
        foreach (var section in modelsSection.GetChildren())
        {
            var cfg = new ModelConfig(
                section["Endpoint"] ?? "",
                section["Key"] ?? "",
                section["Model"] ?? "",
                section["Name"] ?? section.Key);
            models[section.Key] = cfg;
        }

        _models = models;
        DefaultModelId = configuration["ActiveModel"] ?? models.Keys.FirstOrDefault() ?? string.Empty;
        _clients = new ConcurrentDictionary<string, Lazy<IChatClient>>(StringComparer.OrdinalIgnoreCase);
    }

    /// <inheritdoc />
    public bool ContainsModel(string modelId) => _models.ContainsKey(modelId);

    /// <inheritdoc />
    /// <exception cref="KeyNotFoundException">模型 id 未配置时抛出（未知模型不得静默建默认底层）。</exception>
    public IChatClient GetClient(string modelId)
    {
        if (!_models.ContainsKey(modelId))
        {
            throw new KeyNotFoundException($"模型 '{modelId}' 未注册");
        }

        // 懒建缓存：同一 modelId 只构建一次底层客户端，后续复用同一实例（OrdinalIgnoreCase 命中大小写变体）。
        return _clients.GetOrAdd(modelId, key => new Lazy<IChatClient>(() => BuildClient(_models[key]))).Value;
    }

    /// <inheritdoc />
    public IChatClient GetDefaultClient() => GetClient(DefaultModelId);

    /// <summary>
    /// 构建指定模型的底层 chatClient（MVP：OpenAI 兼容直连）+ 统一外包 OTel 遥测。
    /// </summary>
    private IChatClient BuildClient(ModelConfig cfg)
    {
        var clientOptions = new OpenAIClientOptions { Endpoint = new Uri(cfg.Endpoint) };
        var openAi = new OpenAIClient(new ApiKeyCredential(cfg.Key), clientOptions);
        var chatClient = openAi.GetChatClient(cfg.Model).AsIChatClient();

        // OTel 外包：每个底层客户端统一 UseOpenTelemetry(sourceName)，Router 切到任一模型 gen_ai 链路都可见。
        return chatClient
            .AsBuilder()
            .UseOpenTelemetry(sourceName: _telemetryOptions.SourceName)
            .Build();
    }
}
