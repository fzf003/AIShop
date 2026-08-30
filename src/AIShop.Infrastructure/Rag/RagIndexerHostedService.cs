using AIShop.Core.Interfaces;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace AIShop.Infrastructure.Rag;

/// <summary>
/// RAG 索引启动预构建服务（design §2.4 / §5.5）：应用启动时调用 RagIndexer.EnsureIndexedAsync 预构建 18 条商品索引。
/// 索引是派生数据，权威在业务库：预构建失败仅记录 Warning、**不阻塞应用启动**（AI-3/AI-5），
/// 检索期 RagSearchService 向量路还会再调一次 EnsureIndexedAsync 作懒构建兜底（覆盖启动失败/首次访问场景）。
/// 优雅关闭时捕获 OperationCanceledException 正常退出 ExecuteAsync，不视为错误。
/// </summary>
public sealed class RagIndexerHostedService : BackgroundService
{
    private readonly IRagIndexer _indexer;

    public RagIndexerHostedService(IRagIndexer indexer)
    {
        _indexer = indexer;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await _indexer.EnsureIndexedAsync(stoppingToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 预构建失败（模型缺失 / embedding 加载失败 / 存储错误）不阻塞启动：
            // 记 Warning 后让宿主继续，检索期懒构建兜底（与 PreferenceWriteHostedService 的 worker 容错思路一致）
            Log.Warning(ex, "RAG index prebuild failed at startup, lazy build will retry on first search");
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // 主机优雅关闭（SIGTERM / dotnet stop）：stoppingToken 被取消，正常退出 ExecuteAsync，不视为错误
            return;
        }
    }
}
