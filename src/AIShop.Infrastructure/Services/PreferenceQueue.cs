using System.Threading.Channels;
using AIShop.Core.Interfaces;

namespace AIShop.Infrastructure.Services;

/// <summary>
/// 偏好写入队列的 Channel 实现（design 4.2）：有界容量 64，满时丢弃最旧（DropOldest），
/// SingleReader=true 保证仅 PreferenceWriteHostedService 单 worker 串行消费。
/// TryEnqueue 非阻塞：队列满（DropOldest 挤出最旧后仍有容量）或 channel 已关闭时返回 false，
/// 不阻塞聊天响应。
/// </summary>
public sealed class PreferenceQueue : IPreferenceQueue
{
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
            new BoundedChannelOptions(capacity: 64)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
            });

        return new PreferenceQueue(channel);
    }

    /// <summary>
    /// 非阻塞入队：写入成功返回 true；channel 已关闭返回 false。
    /// 队列满时 DropOldest 会先挤出最旧消息再写入，因此容量满时新项仍可入队并返回 true。
    /// </summary>
    public bool TryEnqueue(UserPreferenceUpdate update) => _channel.Writer.TryWrite(update);

    /// <summary>
    /// 暴露读取端，供 PreferenceWriteHostedService 单 worker 消费（ReadAllAsync 串行读-改-写）。
    /// </summary>
    public ChannelReader<UserPreferenceUpdate> Reader => _channel.Reader;
}
