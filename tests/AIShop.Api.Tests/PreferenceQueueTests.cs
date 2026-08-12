using System.Globalization;
using System.Reflection;
using System.Threading.Channels;
using AIShop.Core.Interfaces;
using AIShop.Infrastructure.Services;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace AIShop.Api.Tests;

/// <summary>
/// T12 PreferenceQueue 实现测试：有界 Channel 队列（容量 64 / DropOldest / SingleReader）
/// 的入队、写满丢弃最旧、关闭后拒绝入队三类语义（对应 spec「偏好异步写入不阻塞响应」的队列语义）。
/// </summary>
public sealed class PreferenceQueueTests
{
    private const int Capacity = 64;

    private static UserPreferenceUpdate Update(int index)
        => new(Guid.NewGuid(), [$"item-{index.ToString(CultureInfo.InvariantCulture)}"]);

    /// <summary>
    /// 容量内（≤64）每条消息 TryEnqueue 均返回 true，表示非阻塞入队成功（对应队列可写）。
    /// </summary>
    [Fact]
    public void ShouldEnqueueTrue_WhenWithinCapacity()
    {
        var queue = PreferenceQueue.Create();

        for (var i = 0; i < Capacity; i++)
        {
            Assert.True(queue.TryEnqueue(Update(i)));
        }
    }

    /// <summary>
    /// 写满 64 条后继续入队：DropOldest 丢弃最旧（第 0 条），第 65 条仍可入队并返回 true；
    /// 读回全部消息恰好 64 条，首条为第 1 条、末条为第 64 条（第 0 条已被挤出）。
    /// </summary>
    [Fact]
    public void ShouldDropOldest_WhenQueueFull()
    {
        var queue = PreferenceQueue.Create();
        for (var i = 0; i < Capacity; i++)
        {
            Assert.True(queue.TryEnqueue(Update(i)));
        }

        // 写满后再入队：DropOldest 挤出最旧，新项入队成功
        Assert.True(queue.TryEnqueue(Update(Capacity)));

        var items = new List<int>();
        while (queue.Reader.TryRead(out var update))
        {
            items.Add(int.Parse(update.Preferences[0].AsSpan(5), CultureInfo.InvariantCulture));
        }

        Assert.Equal(Capacity, items.Count);
        Assert.Equal(1, items[0]);       // 最旧 item-0 被丢弃
        Assert.Equal(Capacity, items[^1]); // 新入队的 item-64 在列
    }

    /// <summary>
    /// channel 标记完成后 TryEnqueue 返回 false（Channel 关闭后不再接受写入）。
    /// 通过反射访问内部 Channel 的 Writer 触发完成（ChannelReader 无 Complete API，仅测试需要）。
    /// </summary>
    [Fact]
    public void ShouldReturnFalse_WhenChannelCompleted()
    {
        var queue = PreferenceQueue.Create();
        Assert.True(queue.TryEnqueue(Update(0)));

        var channel = (Channel<UserPreferenceUpdate>?)typeof(PreferenceQueue)
            .GetField("_channel", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(queue);
        Assert.NotNull(channel);
        Assert.True(channel!.Writer.TryComplete());

        Assert.False(queue.TryEnqueue(Update(1)));
    }

    /// <summary>
    /// P2-3 — 队列满（容量 64 / DropOldest）挤掉最旧时可观测：写满后再入队一条，
    /// TryEnqueue 仍返回 true（DropOldest 挤出最旧后写入成功），但入队前记录
    /// 「near full, dropping oldest」Warning，丢弃不再静默。
    /// 通过临时替换 Serilog 全局 Logger 捕获日志事件（try/finally 恢复，不影响其他用例）。
    /// </summary>
    [Fact]
    public void ShouldLogWarning_WhenQueueNearFullAndDroppingOldest()
    {
        var sink = new CollectingSink();
        var original = Log.Logger;
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.Sink(sink)
            .CreateLogger();
        try
        {
            var queue = PreferenceQueue.Create();
            for (var i = 0; i < Capacity; i++)
            {
                Assert.True(queue.TryEnqueue(Update(i)));
            }

            // 写满后入队：DropOldest 挤出最旧并返回 true，同时记录挤旧 Warning
            Assert.True(queue.TryEnqueue(Update(Capacity)));

            var warning = Assert.Single(sink.Events, e =>
                e.Level == LogEventLevel.Warning
                && e.MessageTemplate.Text.Contains("dropping oldest"));
            Assert.Contains("{UserId}", warning.MessageTemplate.Text);
        }
        finally
        {
            Log.Logger = original;
        }
    }

    /// <summary>
    /// 收集 Serilog 日志事件的内存 sink（仅测试用）。
    /// </summary>
    private sealed class CollectingSink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = [];

        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }
}
