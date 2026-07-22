using Serilog;
using AIShop.Api.Features.Chat;

namespace AIShop.Api.Agents;

public record ModelInfo(string Id, string Name, bool IsDefault);

public class ModelRouter
{
    private static readonly Serilog.ILogger Logger = Log.ForContext<ModelRouter>();

    private readonly Dictionary<string, ModelConfig> _modelConfigs;
    private readonly string _activeModel;
    private readonly IShoppingAssistantAgent _defaultAgent;

    private sealed record ModelConfig(string Endpoint, string Key, string Model, string Name);

    public string ActiveModel => _activeModel;

    /// <summary>用于测试的受保护无参构造函数。</summary>
    protected ModelRouter()
    {
        _modelConfigs = new Dictionary<string, ModelConfig>(StringComparer.OrdinalIgnoreCase);
        _activeModel = string.Empty;
        _defaultAgent = null!;
    }

    public ModelRouter(IConfiguration configuration, IShoppingAssistantAgent defaultAgent)
    {
        _defaultAgent = defaultAgent;
        var modelsSection = configuration.GetSection("Models");
        var openaiSection = configuration.GetSection("OpenAI");

        if (modelsSection.Exists() && modelsSection.GetChildren().Any())
        {
            // 新版格式："Models" 节下多个子节
            _modelConfigs = new Dictionary<string, ModelConfig>(StringComparer.OrdinalIgnoreCase);

            foreach (var section in modelsSection.GetChildren())
            {
                var config = new ModelConfig(
                    section["Endpoint"] ?? "",
                    section["Key"] ?? "",
                    section["Model"] ?? "",
                    section["Name"] ?? section.Key
                );
                _modelConfigs[section.Key] = config;
            }

            _activeModel = configuration["ActiveModel"] ?? _modelConfigs.Keys.FirstOrDefault() ?? "";
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

    /// <summary>获取指定模型对应的 Agent 实例。</summary>
    /// <exception cref="KeyNotFoundException">模型未注册时抛出。</exception>
    public virtual IShoppingAssistantAgent GetAgent(string modelName)
    {
        if (!_modelConfigs.ContainsKey(modelName))
            throw new KeyNotFoundException($"模型 '{modelName}' 未注册");

        return _defaultAgent;
    }

    /// <summary>获取当前激活模型的默认 Agent 实例。</summary>
    public virtual IShoppingAssistantAgent GetDefaultAgent() => _defaultAgent;
}
