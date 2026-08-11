using AIShop.Core.Entities;

namespace AIShop.Api.Tests;

/// <summary>
/// UserPreferences 实体 T3 契约测试：默认值与字段/类型（design 3.2，对应 spec「UserPreferences 表持久化」数据模型）。
/// </summary>
public sealed class UserPreferencesEntityTests
{
    [Fact]
    public void NewUserPreferences_KeywordsJson_DefaultsToEmptyJson()
    {
        var prefs = new UserPreferences();

        Assert.Equal("{}", prefs.KeywordsJson);
    }

    [Fact]
    public void NewUserPreferences_UpdatedAt_IsCloseToUtcNow()
    {
        var prefs = new UserPreferences();

        var elapsed = DateTime.UtcNow - prefs.UpdatedAt;
        Assert.InRange(elapsed, TimeSpan.FromSeconds(-5), TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void UserPreferences_FieldNamesAndTypes_MatchContract()
    {
        var type = typeof(UserPreferences);

        Assert.Equal(typeof(Guid), type.GetProperty("UserId")!.PropertyType);
        Assert.Equal(typeof(string), type.GetProperty("KeywordsJson")!.PropertyType);
        Assert.Equal(typeof(DateTime), type.GetProperty("UpdatedAt")!.PropertyType);
    }
}
