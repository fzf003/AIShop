using Serilog;
using Microsoft.Extensions.Configuration;
using System.Collections.Generic;

namespace AIShop.Api.Agents;

/// <summary>
/// 公开暴露的模型信息（不含 Key/Endpoint 等敏感字段）。
/// </summary>
public record ModelInfo(string Id, string Name, bool IsDefault);

/// <summary>
/// 多模型 Agent 切换核心类。
/// 从 IConfiguration 读取模型配置，支持向后兼容旧版 "OpenAI" 配置格式。
/// </summary>
public sealed class ModelRouter
{
    /// <summary>
    /// 内部使用的完整模型配置（含 Key/Endpoint，不对外暴露）。
    /// </summary>
    private sealed record ModelConfig(string Endpoint, string Key, string Model, string Name);

    private readonly IReadOnlyDictionary<string, ModelConfig> _models;
    private readonly string _activeModel;
    private static readonly Serilog.ILogger Logger = Serilog.Log.ForContext<ModelRouter>();

    public ModelRouter(IConfiguration configuration)
    {
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
    /// 判断模型是否为 OpenAI 模型（gpt- / o1- / o3- 前缀），
    /// 委托至 <see cref="ShoppingAssistantAgent.IsOpenAIModel"/>。
    /// </summary>
    public static bool IsOpenAIModel(string model) => ShoppingAssistantAgent.IsOpenAIModel(model);
}
