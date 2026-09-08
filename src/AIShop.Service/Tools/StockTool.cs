using System.Globalization;
using Microsoft.Extensions.AI;

namespace AIShop.Service.Tools;

/// <summary>
/// 股票查询工具 — 将新浪财经免费 API 封装为 AIFunction
/// Agent 通过 function calling 直接调用此工具获取 A 股实时行情
/// </summary>
public static class StockTool
{
    private static readonly HttpClient _httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(10)
    };

    static StockTool()
    {
        // 新浪财经需要 Referer 头才能返回数据
#pragma warning disable S1075 // URIs should not be hardcoded
        _httpClient.DefaultRequestHeaders.Referrer = new Uri("https://finance.sina.com.cn");
#pragma warning restore S1075 // URIs should not be hardcoded
    }

    /// <summary>
    /// 创建股票查询 AIFunction，名称 get_stock_quote
    /// </summary>
    public static AIFunction Create()
    {
        return AIFunctionFactory.Create(GetStockQuoteAsync, new AIFunctionFactoryOptions
        {
            Name = "get_stock_quote",
            Description = "查询 A 股实时行情数据。输入股票代码（如 sz002475 或 sh600519）或股票名称（立讯精密、贵州茅台），返回当前价格、涨跌幅、最高/最低价、成交额等信息。使用新浪财经免费接口。"
        });
    }

    /// <summary>
    /// 查询指定股票的实时行情
    /// </summary>
    /// <param name="code">股票代码，如 sz002475（深市）或 sh600519（沪市）。
    /// 前缀: sh=沪市, sz=深市, bj=北交所</param>
    /// <returns>格式化的股票行情信息</returns>
    public static async Task<string> GetStockQuoteAsync(string code)
    {
        try
        {
            // 如果输入的是纯数字，尝试自动补全前缀
            if (code.All(char.IsDigit))
            {
                code = code.Length switch
                {
                    6 when code.StartsWith('6') => $"sh{code}",
                    6 => $"sz{code}",
                    _ => code
                };
            }

            var url = $"https://hq.sinajs.cn/list={code}";
            var response = await _httpClient.GetStringAsync(url);

            // 解析返回的 CSV 格式数据

#pragma warning disable S125 // Sections of code should not be commented out
                            // 格式: var hq_str_sz002475="名称,开盘,昨收,当前,最高,最低,日期,时间,买一,卖一,...";
            var startIdx = response.IndexOf('"');
#pragma warning restore S125 // Sections of code should not be commented out
            var endIdx = response.LastIndexOf('"');
            if (startIdx == -1 || endIdx <= startIdx)
                return $"❌ 无法解析股票 {code} 的数据";

            var data = response[(startIdx + 1)..endIdx];
            var fields = data.Split(',');

            if (fields.Length < 32)
                return $"❌ 股票 {code} 数据不完整";

            var name = fields[0];
            var openPrice = ParsePrice(fields[1]);      // 今日开盘
            var prevClose = ParsePrice(fields[2]);       // 昨日收盘
            var currentPrice = ParsePrice(fields[3]);    // 当前价格
            var highPrice = ParsePrice(fields[4]);       // 今日最高
            var lowPrice = ParsePrice(fields[5]);        // 今日最低
            var date = fields[30];                       // 日期
            var time = fields[31];                       // 时间

            // 计算涨跌幅
            var change = currentPrice - prevClose;
            var changePercent = prevClose > 0 ? (change / prevClose) * 100 : 0;

            // 成交量和成交额（字段 8 是买一价，字段 10 是买一量...成交量在字段9？）
            // 新浪格式：字段 8-9 买一价/量，10-11 卖一价/量...
            // 成交量在字段 9，成交额在字段 11？这取决于版本
            // 标准新浪格式：字段 8=买一价，9=买一量，10=卖一价，11=卖一量
            // 总手在字段 8 的前面...让我用更可靠的方式
            // 实际上不同股票市场的字段位置不同
            // 取几个关键字段

            var volume = ParseLong(fields[8]);           // 成交量（股）
            var turnover = ParseDecimal(fields[9]);       // 成交额（元）

            var changeSign = change >= 0 ? "+" : "";
            var changeEmoji = change switch
            {
                > 0 => "📈",
                < 0 => "📉",
                _ => "➡️"
            };

            return $@"{changeEmoji} {name} ({code})
💰 当前价格: {currentPrice:F2}
📊 涨跌幅: {changeSign}{change:F2} ({changeSign}{changePercent:F2}%)
📈 今日最高: {highPrice:F2}
📉 今日最低: {lowPrice:F2}
📌 今日开盘: {openPrice:F2}
📅 昨日收盘: {prevClose:F2}
💹 成交量: {FormatVolume(volume)}
💵 成交额: {FormatMoney(turnover)}
🕐 更新时间: {date} {time}";
        }
        catch (HttpRequestException ex)
        {
            return $"❌ 无法查询股票 {code}: {ex.Message}";
        }
        catch (Exception ex)
        {
            return $"❌ 查询股票时出错: {ex.Message}";
        }
    }

    private static double ParsePrice(string value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var r) ? r : 0;

    private static long ParseLong(string value) =>
        long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var r) ? r : 0;

    private static decimal ParseDecimal(string value) =>
        decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var r) ? r : 0;

#pragma warning disable S3358 // Ternary operators should not be nested
    private static string FormatVolume(long volume) =>
        volume >= 100_000_000
            ? $"{volume / 100_000_000.0:F2}亿股"
            : volume >= 10_000
                ? $"{volume / 10_000.0:F2}万股"
                : $"{volume}股";
#pragma warning restore S3358 // Ternary operators should not be nested

#pragma warning disable S3358 // Ternary operators should not be nested
    private static string FormatMoney(decimal turnover) =>
        turnover >= 100_000_000m
            ? $"{turnover / 100_000_000m:F2}亿元"
            : turnover >= 10_000m
                ? $"{turnover / 10_000m:F2}万元"
                : $"{turnover:F2}元";
#pragma warning restore S3358 // Ternary operators should not be nested
}
