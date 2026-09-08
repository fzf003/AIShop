using System.Text.Json;
using Microsoft.Extensions.AI;

namespace AIShop.Service.Tools;

/// <summary>
/// 天气查询工具 — 将 SKILL.md 描述的 wttr.in API 封装为 AIFunction
/// Agent 通过 function calling 直接调用此工具获取实时天气
/// </summary>
public static class WeatherTool
{
    private static readonly HttpClient _httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(10)
    };

    /// <summary>
    /// 创建天气查询 AIFunction，名称 get_weather_forecast
    /// </summary>
    public static AIFunction Create()
    {
        return AIFunctionFactory.Create(GetWeatherForecastAsync, new AIFunctionFactoryOptions
        {
            Name = "get_weather_forecast",
            Description = "查询任意城市的实时天气和未来天气预报，使用 wttr.in 免费 API。传入城市拼音即可获取当前温度、湿度、天气描述、风速等信息。"
        });
    }

    /// <summary>
    /// 查询指定城市的实时天气
    /// </summary>
    /// <param name="city">城市拼音，如 beijing, shanghai, guangzhou</param>
    /// <returns>格式化的天气信息</returns>
    public static async Task<string> GetWeatherForecastAsync(string city)
    {
        try
        {
            var url = $"https://wttr.in/{city}?format=j1";
            var response = await _httpClient.GetStringAsync(url);

            using var doc = JsonDocument.Parse(response);
            var root = doc.RootElement;

            var current = root.GetProperty("current_condition")[0];

            var temp = current.GetProperty("temp_C").GetString();
            var feelsLike = current.GetProperty("FeelsLikeC").GetString();
            var humidity = current.GetProperty("humidity").GetString();
            var weatherDesc = current.GetProperty("weatherDesc")[0].GetProperty("value").GetString();
            var windSpeed = current.GetProperty("windspeedKmph").GetString();
            var windDir = current.GetProperty("winddir16Point").GetString();
            var precip = current.GetProperty("precipMM").GetString();
            var visibility = current.GetProperty("visibility").GetString();

            // 尝试获取今天预报
            string forecast = "";
            if (root.TryGetProperty("weather", out var weatherArray) && weatherArray.GetArrayLength() > 0)
            {
                var today = weatherArray[0];
                var maxTemp = today.GetProperty("maxtempC").GetString();
                var minTemp = today.GetProperty("mintempC").GetString();
                forecast = $"\n今日预报: {minTemp}°C ~ {maxTemp}°C";
            }

            return $@"城市: {city}
🌡 当前温度: {temp}°C（体感 {feelsLike}°C）
☁️ 天气: {weatherDesc}
💧 湿度: {humidity}%
🌬 风速: {windSpeed} km/h ({windDir})
🌧 降水量: {precip} mm
👁 能见度: {visibility} km{forecast}";
        }
        catch (HttpRequestException ex)
        {
            return $"❌ 无法查询 {city} 的天气: {ex.Message}";
        }
        catch (Exception ex)
        {
            return $"❌ 查询天气时出错: {ex.Message}";
        }
    }
}
