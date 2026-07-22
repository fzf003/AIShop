using Serilog;

namespace AIShop.Api.Agents;

public record ModelInfo(string Id, string Name, bool IsDefault);

public sealed class ModelRouter
{
    private static readonly Serilog.ILogger Logger = Log.ForContext<ModelRouter>();

    private readonly Dictionary<string, ModelConfig> _modelConfigs;
    private readonly string _activeModel;

    private sealed record ModelConfig(string Endpoint, string Key, string Model, string Name);

    public ModelRouter(IConfiguration configuration)
    {
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

    public IEnumerable<ModelInfo> GetAvailableModels()
    {
        foreach (var (id, config) in _modelConfigs)
        {
            yield return new ModelInfo(
                id,
                config.Name,
                string.Equals(id, _activeModel, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>
    /// 判断模型是否为 OpenAI 系列（gpt-, o1-, o3-）。
    /// 由 T4（Lazy Agent 实例化）使用，暂未直接调用。
    /// </summary>
#pragma warning disable S1144 // 由 T4（ModelRouter Lazy 路由）使用
    private static bool IsOpenAIModel(string model) =>
        model.StartsWith("gpt-", StringComparison.OrdinalIgnoreCase) ||
        model.StartsWith("o1-", StringComparison.OrdinalIgnoreCase) ||
        model.StartsWith("o3-", StringComparison.OrdinalIgnoreCase);
#pragma warning restore S1144
}
