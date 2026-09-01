using Microsoft.Extensions.AI;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using System;
using System.Collections.Generic;
using System.Text;
using System.Linq;
namespace AIShop.Infrastructure.MemoryService
{
    /// <summary>
    /// 本地 bge-small-zh-v1.5 嵌入生成器（ONNX Runtime）。
    /// 512 维，中文语义向量。模型文件在 models/bge-small-zh-v1.5/ 下。
    /// 分词：中文逐字 + 英文整词（查 vocab），绕开 BertTokenizer 的中文 [UNK] bug。
    /// </summary>
    public sealed class LocalBgeEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
    {
        private const int MaxSeqLen = 512;
        private readonly InferenceSession _session;
        private readonly Dictionary<string, int> _vocab;

        public LocalBgeEmbeddingGenerator(string modelDir)
        {
            _session = new InferenceSession(Path.Combine(modelDir, "model.onnx"));
            _vocab = LoadVocab(Path.Combine(modelDir, "vocab.txt"));
        }

 
        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values,
            EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var list = values.ToList();
            var result = new List<Embedding<float>>(list.Count);
            foreach (var text in list)
            {
                cancellationToken.ThrowIfCancellationRequested();
                result.Add(new Embedding<float>(
                    await Task.Run(() => EmbedOne(text), cancellationToken)));
            }

            return new GeneratedEmbeddings<Embedding<float>>(result);
        }

        private ReadOnlyMemory<float> EmbedOne(string text)
        {
            // 1. 自定义分词：中文逐字 + 英文整词，自动加 [CLS]/[SEP]
            var ids = TokenizeForBge(text, _vocab);
            if (ids.Count > MaxSeqLen)
            {
                ids = ids.Take(MaxSeqLen).ToList();
            }

            int len = ids.Count;
            var inputIds = new long[len];
            var mask = new long[len];
            for (int i = 0; i < len; i++)
            {
                inputIds[i] = ids[i];
                mask[i] = 1;
            }

            var typeIds = new long[len]; // BERT segment id 全 0

            // 2. 跑 ONNX（[1, seq] 形状）
            using var outputs = _session.Run(
            [
                NamedOnnxValue.CreateFromTensor("input_ids", new DenseTensor<long>(inputIds, [1, len])),
            NamedOnnxValue.CreateFromTensor("attention_mask", new DenseTensor<long>(mask, [1, len])),
            NamedOnnxValue.CreateFromTensor("token_type_ids", new DenseTensor<long>(typeIds, [1, len])),
        ]);

            // 3. 取 last_hidden_state [1, seq, 512]，用 [CLS] 位置（token 0）的向量
            var tensor = outputs[0].AsTensor<float>();
            var all = tensor.ToArray();
            int hidden = tensor.Dimensions[^1];
            var vector = all.Take(hidden).ToArray();

            // 4. L2 归一化（bge 官方建议，确保余弦相似度语义正确）
            float norm = 0f;
            foreach (var v in vector)
            {
                norm += v * v;
            }

            norm = MathF.Sqrt(norm);
            if (norm > 0)
            {
                for (int i = 0; i < vector.Length; i++)
                {
                    vector[i] /= norm;
                }
            }

            return new ReadOnlyMemory<float>(vector);
        }

        // ── 分词：中文逐字，英文/数字整词，小写化后查 vocab ──
        private static List<int> TokenizeForBge(string text, Dictionary<string, int> vocab)
        {
            int clsId = vocab["[CLS]"];
            int sepId = vocab["[SEP]"];
            int unkId = vocab["[UNK]"];

            var ids = new List<int> { clsId };

            void AddToken(string token)
            {
                ids.Add(vocab.TryGetValue(token, out var id) ? id : unkId);
            }

            for (int i = 0; i < text.Length;)
            {
                char c = text[i];
                if (IsCjk(c))
                {
                    AddToken(c.ToString());   // 中文逐字
                    i++;
                }
                else if (char.IsLetterOrDigit(c))
                {
                    int start = i;
                    while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] is '.' or '%' or '-'))
                    {
                        i++;
                    }

                    AddToken(text[start..i].ToLowerInvariant());   // 英文整词，小写
                }
                else
                {
                    i++;   // 标点/空格跳过
                }
            }

            ids.Add(sepId);
            return ids;
        }

        private static Dictionary<string, int> LoadVocab(string path)
        {
            var lines = File.ReadAllLines(path);
            var vocab = new Dictionary<string, int>(lines.Length);
            for (int i = 0; i < lines.Length; i++)
            {
                vocab[lines[i]] = i;
            }

            return vocab;
        }

        private static bool IsCjk(char c) => c is >= '一' and <= '鿿';

        public void Dispose() => _session.Dispose();
    }
}
