using Microsoft.Extensions.AI;

namespace AIShop.Service.Tools;

/// <summary>
/// 日期时间工具 — 获取当前日期、时间、星期等信息
/// 多 Agent 编排中，TimeResolverAgent 用此工具获取当前时间以解析自然语言时间表达
/// </summary>
public static class DateTimeTool
{
    /// <summary>
    /// 创建日期时间查询 AIFunction，名称 get_current_datetime
    /// </summary>
    public static AIFunction Create()
    {
        return AIFunctionFactory.Create(GetCurrentDateTime, new AIFunctionFactoryOptions
        {
            Name = "get_current_datetime",
            Description = "获取当前日期、时间、星期、时区等信息。无需参数，Agent 用于解析自然语言时间表达（如'今天'、'明天'、'后天'、'17点'）。"
        });
    }

    /// <summary>
    /// 获取当前日期时间信息
    /// </summary>
    /// <returns>格式化的日期时间信息</returns>
    public static string GetCurrentDateTime()
    {
        var now = DateTimeOffset.Now;

        var weekDay = now.DayOfWeek switch
        {
            DayOfWeek.Monday => "星期一",
            DayOfWeek.Tuesday => "星期二",
            DayOfWeek.Wednesday => "星期三",
            DayOfWeek.Thursday => "星期四",
            DayOfWeek.Friday => "星期五",
            DayOfWeek.Saturday => "星期六",
            DayOfWeek.Sunday => "星期日",
            _ => now.DayOfWeek.ToString()
        };

        var today = now.Date;
        var tomorrow = today.AddDays(1);
        var dayAfterTomorrow = today.AddDays(2);

        return $@"当前时间: {now:yyyy-MM-dd HH:mm:ss}
当前日期: {now:yyyy年MM月dd日}
星期: {weekDay}
时区: UTC{now:zzz}

今日: {now:yyyy-MM-dd}
明日: {tomorrow:yyyy-MM-dd}
后天: {dayAfterTomorrow:yyyy-MM-dd}

Unix 时间戳: {now.ToUnixTimeSeconds()}";
    }
}
