using System.Reflection;
using AIShop.Core.Interfaces;
using AIShop.Core.ValueObjects;

namespace AIShop.Api.Tests;

/// <summary>
/// T4 接口契约测试：用反射断言 IPreferenceQueue / IPreferenceRepository / UserPreferenceUpdate
/// 的签名与 design 4.2 完全一致（对应 spec「偏好权重累加」「偏好异步写入不阻塞响应」的接口契约）。
/// </summary>
public sealed class PreferenceContractsTests
{
    [Fact]
    public void TryEnqueue_TakesSingleUserPreferenceUpdateAndReturnsBool()
    {
        var method = typeof(IPreferenceQueue).GetMethod(nameof(IPreferenceQueue.TryEnqueue));

        Assert.NotNull(method);
        Assert.Equal(typeof(bool), method!.ReturnType);

        var parameters = method.GetParameters();
        Assert.Single(parameters);
        Assert.Equal(typeof(UserPreferenceUpdate), parameters[0].ParameterType);
        Assert.Equal("update", parameters[0].Name);
    }

    [Fact]
    public void UserPreferenceUpdate_IsPositionalRecordWithUserIdAndPreferences()
    {
        var type = typeof(UserPreferenceUpdate);

        // positional record 编译器会生成 <Clone>$ 方法（instance），非 record 类不存在
        var cloneMethod = type.GetMethod("<Clone>$", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.NotNull(cloneMethod);

        var userIdProp = type.GetProperty(nameof(UserPreferenceUpdate.UserId));
        Assert.NotNull(userIdProp);
        Assert.Equal(typeof(Guid), userIdProp!.PropertyType);

        var preferencesProp = type.GetProperty(nameof(UserPreferenceUpdate.Preferences));
        Assert.NotNull(preferencesProp);
        Assert.Equal(typeof(IReadOnlyList<string>), preferencesProp!.PropertyType);
    }

    [Fact]
    public void IPreferenceRepository_GetByUserIdAsyncMatchesDesign()
    {
        var method = typeof(IPreferenceRepository).GetMethod(nameof(IPreferenceRepository.GetByUserIdAsync));

        Assert.NotNull(method);
        Assert.Equal(typeof(Task<PreferenceProfile?>), method!.ReturnType);

        var parameters = method.GetParameters();
        Assert.Equal(2, parameters.Length);
        Assert.Equal(typeof(Guid), parameters[0].ParameterType);
        Assert.Equal("userId", parameters[0].Name);
        Assert.Equal(typeof(CancellationToken), parameters[1].ParameterType);
        Assert.True(parameters[1].IsOptional, "CancellationToken 参数应为可选（默认 default）");
    }

    [Fact]
    public void IPreferenceRepository_UpsertAsyncTakesPreferenceProfileAndReturnsTask()
    {
        var method = typeof(IPreferenceRepository).GetMethod(nameof(IPreferenceRepository.UpsertAsync));

        Assert.NotNull(method);
        Assert.Equal(typeof(Task), method!.ReturnType);

        var parameters = method.GetParameters();
        Assert.Equal(2, parameters.Length);
        Assert.Equal(typeof(PreferenceProfile), parameters[0].ParameterType);
        Assert.Equal("profile", parameters[0].Name);
        Assert.Equal(typeof(CancellationToken), parameters[1].ParameterType);
        Assert.True(parameters[1].IsOptional, "CancellationToken 参数应为可选（默认 default）");
    }
}
