using Serilog;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.AI;
using System.ClientModel;
using System.ClientModel.Primitives;
using System.Collections.Concurrent;
using System.Collections.Generic;
using AIShop.Core.Interfaces;
using AIShop.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using OpenAI;

namespace AIShop.Api.Agents;

/// <summary>
/// 公开暴露的模型信息（不含 Key/Endpoint 等敏感字段）。
/// </summary>
public record ModelInfo(string Id, string Name, bool IsDefault);

/// <summary>
/// 多模型 Agent 切换核心类。
/// 从 IConfiguration 读取模型配置，支持向后兼容旧版 "OpenAI" 配置格式。
/// </summary>
public class ModelRouter
{
    /// <summary>
    /// 内部使用的完整模型配置（含 Key/Endpoint，不对外暴露）。
    /// </summary>
    internal sealed record ModelConfig(string Endpoint, string Key, string Model, string Name);

    private readonly IReadOnlyDictionary<string, ModelConfig> _models;
    private readonly string _activeModel;
    private readonly IServiceProvider _sp;
    private readonly ConcurrentDictionary<string, Lazy<ShoppingAssistantAgent>> _agents = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Serilog.ILogger Logger = Serilog.Log.ForContext<ModelRouter>();

    /// <summary>
    /// 用于测试的受保护无参构造函数。
    /// 子类应覆盖 <see cref="GetAgent"/> 和 <see cref="GetDefaultAgent"/> 方法。
    /// </summary>
    protected ModelRouter()
    {
        _models = new Dictionary<string, ModelConfig>(StringComparer.OrdinalIgnoreCase);
        _activeModel = "default";
        _sp = null!;
    }

    public ModelRouter(IConfiguration configuration, IServiceProvider sp)
    {
        _sp = sp;
        var modelsSection = configuration.GetSection("Models");
        var openaiSection = configuration.GetSection("OpenAI");

        if (modelsSection.Exists() && modelsSection.GetChildren().Any())
        {
            // 正常路径：从 "Models" 节解析多模型配置
            var models = new Dictionary<string, ModelConfig>(StringComparer.OrdinalIgnoreCase);

            foreach (var section in modelsSection.GetChildren())
            {
                var model = section["Model"];
                if (string.IsNullOrEmpty(model))
                    continue;

                var name = section["Name"];
                if (string.IsNullOrEmpty(name))
                    name = model;

                models[section.Key] = new ModelConfig(
                    section["Endpoint"] ?? string.Empty,
                    section["Key"] ?? string.Empty,
                    model,
                    name);
            }

            _models = models;
            _activeModel = configuration["ActiveModel"]
                ?? configuration["Models:ActiveModel"]
                ?? models.Keys.First();
        }
        else if (openaiSection.Exists())
        {
            // 向后兼容：旧版 "OpenAI" 节 → 包装为单模型
            var model = openaiSection["Model"] ?? string.Empty;
            var name = openaiSection["Name"];
            if (string.IsNullOrEmpty(name))
                name = model;

            _models = new Dictionary<string, ModelConfig>(StringComparer.OrdinalIgnoreCase)
            {
                ["legacy"] = new ModelConfig(
                    openaiSection["Endpoint"] ?? string.Empty,
                    openaiSection["Key"] ?? string.Empty,
                    model,
                    name)
            };
            _activeModel = "legacy";

            Logger.Warning("检测到旧版 OpenAI 配置格式，建议迁移至新版 Models 节。");
        }
        else
        {
            throw new InvalidOperationException(
                "配置中缺少 Models 节或 OpenAI 节。请在 appsettings.json 中配置至少一个模型。");
        }
    }

    /// <summary>
    /// 返回可用的模型列表（仅暴露 Id / Name / IsDefault，不含 Key/Endpoint）。
    /// </summary>
    public IEnumerable<ModelInfo> GetAvailableModels()
    {
        foreach (var (id, config) in _models)
        {
            yield return new ModelInfo(
                id,
                config.Name,
                string.Equals(id, _activeModel, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>
    /// 检查模型是否已注册。
    /// </summary>
    public bool IsModelRegistered(string modelName) =>
        _models.ContainsKey(modelName);

    /// <summary>
    /// 获取当前激活的模型 ID。
    /// </summary>
    internal string ActiveModel => _activeModel;

    /// <summary>
    /// 判断模型是否为 OpenAI 模型（gpt- / o1- / o3- 前缀），
    /// 委托至 <see cref="ShoppingAssistantAgent.IsOpenAIModel"/>。
    /// </summary>
    public static bool IsOpenAIModel(string model) => ShoppingAssistantAgent.IsOpenAIModel(model);

    /// <summary>
    /// 根据模型配置创建 IChatClient 实例。
    /// 每个 Agent 拥有独立的 HTTP 客户端和连接，避免并发竞争。
    /// </summary>
    private static IChatClient CreateChatClient(ModelConfig cfg)
    {
        var handler = new HttpClientHandler { UseProxy = false, Proxy = null };
        var httpClient = new HttpClient(new DebugHandler(handler)) { Timeout = TimeSpan.FromSeconds(120) };
        var clientOptions = new OpenAIClientOptions
        {
            Endpoint = new Uri(cfg.Endpoint),
            Transport = new HttpClientPipelineTransport(httpClient),
        };
        var client = new OpenAIClient(new ApiKeyCredential(cfg.Key), clientOptions);
        return client.GetChatClient(cfg.Model).AsIChatClient();
    }

    /// <summary>
    /// 获取指定模型名称对应的 <see cref="ShoppingAssistantAgent"/> 实例。
    /// 每个模型首次访问时延迟创建，后续复用，线程安全。
    /// </summary>
    /// <param name="modelName">模型 ID（配置节键名）。</param>
    /// <returns>Agent 实例。</returns>
    /// <exception cref="KeyNotFoundException">模型未注册时抛出。</exception>
    public virtual IShoppingAssistantAgent GetAgent(string modelName)
    {
        if (!_models.ContainsKey(modelName))
            throw new KeyNotFoundException($"模型 '{modelName}' 未注册");

        return _agents.GetOrAdd(modelName, key => new Lazy<ShoppingAssistantAgent>(() =>
        {
            var cfg = _models[key];
            var chatClient = CreateChatClient(cfg);
            var dbFactory = _sp.GetRequiredService<IDbContextFactory<AppDbContext>>();
            var catalog = _sp.GetRequiredService<IProductCatalogService>();
            var cartTools = _sp.GetRequiredService<CartToolProvider>();
            var isOpenAI = IsOpenAIModel(cfg.Model);
            return new ShoppingAssistantAgent(chatClient, dbFactory, catalog, cartTools, isOpenAI);
        })).Value;
    }

    /// <summary>
    /// 获取默认 Agent 实例（当前激活模型）。
    /// </summary>
    public virtual IShoppingAssistantAgent GetDefaultAgent() => GetAgent(_activeModel);
}
