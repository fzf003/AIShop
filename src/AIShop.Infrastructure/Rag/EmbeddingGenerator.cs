using Microsoft.Extensions.AI;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;

namespace AIShop.Infrastructure.Rag;

/// <summary>
/// 本地 bge-small-zh-v1.5 ONNX embedding 生成器（design §5.4，Task1 POC D-b/D-d/D-e/D-f 实测修正落地）。
/// 自研包装 Microsoft.ML.OnnxRuntime 直接推理（NuGet 无 Microsoft.Extensions.AI.ONNX 包，D-b）：
///   - tokenizer 用 vocab.txt + BertOptions（Xenova 导出的 tokenizer.json 无法被 BertTokenizer.Create 加载，D-d）
///   - 句向量 = last_hidden_state[:,0,:]（CLS pooling）+ L2 normalize（Xenova 导出仅 backbone、无 pooler，D-e）
///   - 输出 512 维（bge-small-zh-v1.5 hidden_size，D-a，非 design 早期假设的 384）
/// 离线推理、零调用成本（AB-3）；模型缺失 / 加载失败在构造时抛明确异常，由上层降级关键词路（AI-3）。
/// </summary>
public sealed class EmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
    /// <summary>bge-small-zh-v1.5 最大序列长度（config.json max_position_embeddings=512）。</summary>
    private const int MaxSequenceLength = 512;

    /// <summary>padding 位置 id（config.json pad_token_id=0）。</summary>
    private const int PaddingTokenId = 0;

    /// <summary>模型必需输入张量名（标准 BERT / Xenova 导出固定这 3 个）。</summary>
    private static readonly string[] RequiredInputNames = ["input_ids", "attention_mask", "token_type_ids"];

    private readonly InferenceSession _session;
    private readonly BertTokenizer _tokenizer;
    private readonly int _dimensions;
    private readonly string _outputName;

    /// <summary>tokenizer 与 session 共享的串行化锁：OnnxRuntime Run 线程安全但 tokenizer 状态不保证线程安全，
    /// 18 条量级单次推理足够，串行比并发锁更简单可靠。</summary>
    private readonly object _sync = new();

    /// <summary>
    /// 加载 ONNX 模型 + vocab 词表。模型缺失 / 加载失败在此抛明确异常（含下载指引），
    /// 上层（RagIndexer 索引构建失败 → 检索降级关键词路，AI-3）据此降级，不崩溃。
    /// </summary>
    /// <param name="modelPath">model.onnx 完整路径（Task 9 AddRag 用 Path.Combine(AppContext.BaseDirectory, RagOptions.EmbeddingModelPath)）。</param>
    /// <param name="vocabPath">vocab.txt 完整路径（RagOptions.EmbeddingVocabPath）。</param>
    /// <param name="dimensions">期望输出维度，默认 512（与 [VectorStoreVector(512)] 保持一致，RagOptions.EmbeddingDimensions）。</param>
    public EmbeddingGenerator(string modelPath, string vocabPath, int dimensions = RagOptions.EmbeddingDimensions)
    {
        if (!File.Exists(modelPath))
        {
            throw new InvalidOperationException(
                $"ONNX 模型文件不存在：{modelPath}。首次运行前需下载 bge-small-zh-v1.5（约 95MB，R4，gitignore 不提交）：" +
                "https://hf-mirror.com/Xenova/bge-small-zh-v1.5/resolve/main/onnx/model.onnx");
        }

        if (!File.Exists(vocabPath))
        {
            throw new InvalidOperationException(
                $"tokenizer 词表文件不存在：{vocabPath}。" +
                "请从 https://hf-mirror.com/Xenova/bge-small-zh-v1.5/resolve/main/vocab.txt 下载（D-d：用 vocab.txt 而非 tokenizer.json）。");
        }

        _dimensions = dimensions;
        _session = new InferenceSession(modelPath);

        // 构造期校验输入张量齐全，避免推理时才发现模型不匹配
        var inputNames = _session.InputMetadata.Keys.ToHashSet(StringComparer.Ordinal);
        var missingInput = RequiredInputNames.FirstOrDefault(name => !inputNames.Contains(name));
        if (missingInput is not null)
        {
            throw new InvalidOperationException($"模型 {Path.GetFileName(modelPath)} 缺少必需输入张量：{missingInput}");
        }

        // Xenova 导出仅 backbone、唯一输出 last_hidden_state（D-e）
        _outputName = _session.OutputMetadata.Keys.Single();

        // 构造期校验输出维度，防止误加载错误模型（bge-small-zh-v1.5 hidden_size=512，D-a）
        var hiddenDim = _session.OutputMetadata[_outputName].Dimensions[2];
        if (hiddenDim != _dimensions)
        {
            throw new InvalidOperationException($"模型输出维度 {hiddenDim} 与配置 {_dimensions} 不符（bge-small-zh-v1.5 应为 {RagOptions.EmbeddingDimensions}）。");
        }

        // BertOptions 与 bge 训练配置对齐（D-d）：IndividuallyTokenizeCjk 对应 tokenize_chinese_chars=true
        //（中文逐字切分）、LowerCaseBeforeTokenization=false（bge 区分大小写，不应小写化）
        _tokenizer = BertTokenizer.Create(vocabPath, new BertOptions
        {
            IndividuallyTokenizeCjk = true,
            LowerCaseBeforeTokenization = false,
        });
    }

    /// <inheritdoc />
    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var texts = values as string[] ?? values.ToArray();

        var result = new GeneratedEmbeddings<Embedding<float>>(texts.Length);
        if (texts.Length == 0)
        {
            return Task.FromResult(result);
        }

        // 推理是 CPU 密集的同步工作（本地 ONNX），直接算完返回已完成 Task：
        // 相较 Task.Run 少一次线程池跳转，18 条量级单次推理足够，语义等同 async/await
        lock (_sync)
        {
            result.AddRange(GenerateBatch(texts, cancellationToken));
        }

        return Task.FromResult(result);
    }

    /// <summary>
    /// 批量推理：tokenize → 构造 input_ids/attention_mask/token_type_ids（Int64）→ 前向 → CLS pooling + L2 normalize。
    /// 调用方保证持 _sync 锁（串行推理）。
    /// </summary>
    private List<Embedding<float>> GenerateBatch(string[] texts, CancellationToken ct)
    {
        // 1. tokenize（EncodeToIds 含 [CLS]/[SEP] 特殊 token；超长输入截断到模型最大序列长度）
        var tokenIds = new int[texts.Length][];
        var maxLen = 0;
        for (var i = 0; i < texts.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            var ids = _tokenizer.EncodeToIds(
                texts[i],
                addSpecialTokens: true,
                considerPreTokenization: true,
                considerNormalization: true);
            tokenIds[i] = ids.Count <= MaxSequenceLength ? ids.ToArray() : ids.Take(MaxSequenceLength).ToArray();
            maxLen = Math.Max(maxLen, tokenIds[i].Length);
        }

        // 2. 组 batch 张量：padding 到批内最长序列，attention_mask=0 屏蔽 padding 位（模型输入须等长，Int64）
        var inputIds = new DenseTensor<long>([texts.Length, maxLen]);
        var attentionMask = new DenseTensor<long>([texts.Length, maxLen]);
        var tokenTypeIds = new DenseTensor<long>([texts.Length, maxLen]);
        for (var i = 0; i < texts.Length; i++)
        {
            for (var j = 0; j < maxLen; j++)
            {
                if (j < tokenIds[i].Length)
                {
                    inputIds[i, j] = tokenIds[i][j];
                    attentionMask[i, j] = 1;
                }
                else
                {
                    inputIds[i, j] = PaddingTokenId;
                    attentionMask[i, j] = 0;
                }
            }
        }

        // 3. 前向推理，输出 last_hidden_state 形状 [batch, maxLen, hidden]（Run 结果须 Dispose 释放原生句柄）
        ct.ThrowIfCancellationRequested();
        using var outputs = _session.Run(
        [
            NamedOnnxValue.CreateFromTensor("input_ids", inputIds),
            NamedOnnxValue.CreateFromTensor("attention_mask", attentionMask),
            NamedOnnxValue.CreateFromTensor("token_type_ids", tokenTypeIds),
        ],
        [_outputName]);

        var hiddenBuffer = outputs.Single().AsTensor<float>().ToArray();

        // 4. CLS pooling + L2 normalize（bge 惯例，D-e）：取每条样本第 0 个 token（[CLS]）的 hidden state 并归一化
        var hiddenSize = _dimensions;
        var embeddings = new List<Embedding<float>>(texts.Length);
        for (var i = 0; i < texts.Length; i++)
        {
            var start = i * maxLen * hiddenSize;
            var normSquared = 0f;
            for (var j = 0; j < hiddenSize; j++)
            {
                var value = hiddenBuffer[start + j];
                normSquared += value * value;
            }

            var norm = MathF.Sqrt(normSquared);
            var vector = new float[hiddenSize];
            for (var j = 0; j < hiddenSize; j++)
            {
                var value = hiddenBuffer[start + j];
                // 全零向量（norm=0）保持全零而非除零得 NaN，保证下游 RRF/cos 数值稳定
                vector[j] = norm > 0f ? value / norm : 0f;
            }

            embeddings.Add(new Embedding<float>(vector));
        }

        return embeddings;
    }

    /// <inheritdoc />
    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    /// <inheritdoc />
    public void Dispose() => _session.Dispose();
}
