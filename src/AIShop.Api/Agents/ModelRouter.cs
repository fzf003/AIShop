using Serilog;
using Microsoft.Extensions.AI;
using System.ClientModel;
using System.ClientModel.Primitives;
using System.Collections.Concurrent;
using AIShop.Core.Interfaces;
using AIShop.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using OpenAI;

namespace AIShop.Api.Agents;

public record ModelInfo(string Id, string Name, bool IsDefault);

public class ModelRouter
{
    private static readonly Serilog.ILogger Logger = Log.ForContext<ModelRouter>();

    private readonly IReadOnlyDictionary<string, ModelConfig> _modelConfigs;
    private readonly string _activeModel;
    private readonly IServiceProvider _sp;
    private readonly ConcurrentDictionary<string, Lazy<ShoppingAssistantAgent>> _agents = new(StringComparer.OrdinalIgnoreCase);

    internal sealed record ModelConfig(string Endpoint, string Key, string Model, string Name);

    public string ActiveModel => _activeModel;

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
        if (!_modelConfigs.ContainsKey(modelName))
            throw new KeyNotFoundException($"模型 '{modelName}' 未注册");

        return _agents.GetOrAdd(modelName, key => new Lazy<ShoppingAssistantAgent>(() =>
        {
            var cfg = _modelConfigs[key];
            var chatClient = CreateChatClient(cfg);
            var dbFactory = _sp.GetRequiredService<IDbContextFactory<AppDbContext>>();
            var catalog = _sp.GetRequiredService<IProductCatalogService>();
            var cartTools = _sp.GetRequiredService<CartToolProvider>();
            var isOpenAI = ShoppingAssistantAgent.IsOpenAIModel(cfg.Model);
            return new ShoppingAssistantAgent(chatClient, dbFactory, catalog, cartTools, isOpenAI);
        })).Value;
    }

    /// <summary>获取当前激活模型的默认 Agent 实例。</summary>
    public virtual IShoppingAssistantAgent GetDefaultAgent() => GetAgent(_activeModel);

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
}
