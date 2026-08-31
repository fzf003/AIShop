using AIShop.Infrastructure.Rag;

namespace AIShop.Service.Tests;

/// <summary>
/// EmbeddingGenerator 单元测试（Task 5，design §5.4，AB-3/AI-3）。
/// 用真实 bge-small-zh-v1.5 本地 ONNX 推理（AI-4 允许本地 ONNX，不调任何外部 API）；
/// 模型文件缺失时整类 skip（R13，~95MB 不提交 git，CI/未下载模型的机器不失败）。
/// </summary>
public sealed class EmbeddingGeneratorTests
{
    [EmbeddingModelFact]
    public async Task GenerateAsync_ReturnsVectorsWithConfiguredDimensions()
    {
        // AB-3：bge-small-zh-v1.5 输出 512 维（Task1 POC 实测 D-a，非 design 早期假设的 384）
        using var generator = CreateGenerator();

        var result = await generator.GenerateAsync(["咖啡机"]);

        var vector = Assert.Single(result).Vector;
        Assert.Equal(512, vector.Length);
    }

    [EmbeddingModelFact]
    public async Task GenerateAsync_ChineseInputRunsOffline_WithoutThrowing()
    {
        // AB-3：离线推理（本地 ONNX，无任何 HTTP / 外部 Embedding API 调用）且中文输入不抛异常
        using var generator = CreateGenerator();

        var result = await generator.GenerateAsync(["适合送礼的咖啡机", "专业跑鞋", "香薰蜡烛套装"]);

        Assert.Equal(3, result.Count);
        foreach (var embedding in result)
        {
            Assert.Equal(512, embedding.Vector.Length);
            // L2 normalize 后向量元素全部有限（无 NaN / Infinity）——CLS pooling + 归一化正确性的基本不变量
            Assert.All(embedding.Vector.ToArray(), value => Assert.False(float.IsNaN(value) || float.IsInfinity(value)));
        }
    }

    [EmbeddingModelFact]
    public async Task GenerateAsync_ChineseNearSynonyms_HaveHigherSimilarityThanUnrelated()
    {
        // 复现 Task1 POC R9 证据：cos(咖啡机, 意式浓缩咖啡机) 显著高于 cos(咖啡机, 跑鞋)
        // ——证明生成的是有语义意义的向量，而非「维度正确但内容无意义」的假通过
        using var generator = CreateGenerator();

        var result = await generator.GenerateAsync(["咖啡机", "意式浓缩咖啡机", "跑鞋"]);

        var coffee = result[0].Vector;
        var espresso = result[1].Vector;
        var shoes = result[2].Vector;
        var nearSimilarity = CosineSimilarity(coffee, espresso);
        var farSimilarity = CosineSimilarity(coffee, shoes);

        Assert.True(
            nearSimilarity > farSimilarity + 0.1,
            $"近义词相似度 {nearSimilarity:F3} 应显著高于无关词 {farSimilarity:F3}");
    }

    [Fact]
    public void Ctor_WhenModelFileMissing_ThrowsClearException()
    {
        // AI-3 失败路径边界：模型缺失在构造时抛明确异常（含下载指引），上层据此降级关键词路、不崩溃
        var ex = Assert.Throws<InvalidOperationException>(() =>
            new EmbeddingGenerator("nonexistent/model.onnx", "nonexistent/vocab.txt"));

        Assert.Contains("不存在", ex.Message);
        Assert.Contains("hf-mirror", ex.Message);
    }

    private static EmbeddingGenerator CreateGenerator() =>
        new(
            Path.Combine(AppContext.BaseDirectory, "Models/bge-small-zh-v1.5/model.onnx"),
            Path.Combine(AppContext.BaseDirectory, "Models/bge-small-zh-v1.5/vocab.txt"));

    private static float CosineSimilarity(ReadOnlyMemory<float> a, ReadOnlyMemory<float> b)
    {
        var x = a.Span;
        var y = b.Span;
        float dot = 0, normA = 0, normB = 0;
        for (var i = 0; i < x.Length; i++)
        {
            dot += x[i] * y[i];
            normA += x[i] * x[i];
            normB += y[i] * y[i];
        }

        return dot / (MathF.Sqrt(normA) * MathF.Sqrt(normB));
    }
}

/// <summary>
/// 条件跳过的 Fact：bge ONNX 模型文件（~95MB，gitignore 不提交）缺失时跳过真实模型测试（R13）。
/// xUnit v2 在测试发现阶段实例化 FactAttribute，构造器内可动态设置 Skip，实现「整类 skip 且不影响其余测试」。
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class EmbeddingModelFactAttribute : FactAttribute
{
    public EmbeddingModelFactAttribute()
    {
        var modelPath = Path.Combine(AppContext.BaseDirectory, "Models/bge-small-zh-v1.5/model.onnx");
        var vocabPath = Path.Combine(AppContext.BaseDirectory, "Models/bge-small-zh-v1.5/vocab.txt");
        if (!File.Exists(modelPath) || !File.Exists(vocabPath))
        {
            Skip = "bge-small-zh-v1.5 ONNX 模型文件缺失，跳过真实模型测试（R13）。" +
                   "首次运行请从 https://hf-mirror.com/Xenova/bge-small-zh-v1.5 下载 model.onnx + vocab.txt " +
                   "到 src/AIShop.Infrastructure/Rag/Models/bge-small-zh-v1.5/";
        }
    }
}
