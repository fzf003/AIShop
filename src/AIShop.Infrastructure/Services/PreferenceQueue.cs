using System.Threading.Channels;
using AIShop.Core.Interfaces;
using Serilog;

namespace AIShop.Infrastructure.Services;

/// <summary>
/// 偏好写入队列的 Channel 实现（design 4.2）：有界容量 64，满时丢弃最旧（DropOldest），
/// SingleReader=true 保证仅 PreferenceWriteHostedService 单 worker 串行消费。
/// DropOldest 语义：队列满时新入队会先挤出最旧消息再写入并返回 true；仅当 channel 标记
/// 完成后 TryEnqueue 才返回 false。因此「返回 false」≠「队列满丢弃」，调用方不应据此判断丢弃。
/// </summary>
public sealed class PreferenceQueue : IPreferenceQueue
{
    private const int Capacity = 64;

    private readonly Channel<UserPreferenceUpdate> _channel;

    private PreferenceQueue(Channel<UserPreferenceUpdate> channel)
    {
        _channel = channel;
    }

    /// <summary>
    /// 创建容量 64、DropOldest、SingleReader 的有界 Channel 队列实例。
    /// </summary>
    public static PreferenceQueue Create()
    {
        var channel = Channel.CreateBounded<UserPreferenceUpdate>(
            new BoundedChannelOptions(capacity: Capacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
            });

        return new PreferenceQueue(channel);
    }

    /// <summary>
    /// 非阻塞入队：写入成功返回 true；channel 已关闭返回 false。
    /// 队列满时 DropOldest 会先挤出最旧消息再写入，因此容量满时新项仍可入队并返回 true。
    /// 入队前若队列已处于满容量（本次写入必然挤掉最旧），记录 Warning 近似观测丢弃事件（P2-3）。
    /// </summary>
    public bool TryEnqueue(UserPreferenceUpdate update)
    {
        if (_channel.Reader.CanCount && _channel.Reader.Count >= Capacity)
        {
            Log.Warning("Preference queue near full, dropping oldest for {UserId}", update.UserId);
        }

        return _channel.Writer.TryWrite(update);
    }

    /// <summary>
    /// 暴露读取端，供 PreferenceWriteHostedService 单 worker 消费（ReadAllAsync 串行读-改-写）。
    /// </summary>
    public ChannelReader<UserPreferenceUpdate> Reader => _channel.Reader;
}
