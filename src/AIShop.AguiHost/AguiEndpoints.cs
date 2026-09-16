using AIShop.Service.Agui;

namespace AIShop.AguiHost;

/// <summary>
/// AguiHost 的辅助 REST 端点（agui-client-support T2）。
/// <para>
/// <c>GET /models</c>：返回模型清单（<see cref="ModelDescriptor"/> 数组），供前端渲染「登录网格 + 顶栏徽标」
/// 模型选择器。清单数据<b>唯一来源</b> = 模型客户端工厂的 <see cref="IModelChatClientFactory.AvailableModels"/>
/// （spec「模型清单来源单一」）——工厂已实现 <c>Name</c> 缺省（回退节键）、顺序（子键序数升序）与 <c>isDefault</c>
/// 守卫三处口径，端点层<b>不得</b>二次读取模型配置节，否则会形成第二份解析逻辑并与工厂分叉（配置漂移）。
/// </para>
/// <para>
/// <b>公开可读（有意为之的取舍，勿当漏洞改）</b>：本端点不做 username 校验、不做鉴权。理由：响应内容只有节键、
/// 显示名与真实模型名——模型密钥与端点地址<b>不在</b>响应中；且前端在用户选定身份<b>之前</b>（登录页网格）就需要
/// 该清单。给它叠加身份校验既无收益、又会把登录页锁死（该端点也不因请求上下文改变返回内容）。
/// </para>
/// </summary>
internal static class AguiEndpoints
{
    /// <summary>
    /// 映射 AguiHost 的辅助端点（均为根级路径——AguiHost 无 <c>/api</c> 分组，此处不引入新的分组概念）。
    /// 路径与 <c>"/"</c>（AG-UI SSE）、<c>/health</c>、<c>/alive</c>、<c>/devui</c>、<c>/v1/*</c> 均不冲突。
    /// </summary>
    internal static void MapSupportEndpoints(this WebApplication app)
    {
        // 清单直接取自工厂单例（工厂自身已按配置节解析一次）；端点不持配置、不触碰模型配置节。
        app.MapGet("/models", (IModelChatClientFactory factory) => Results.Ok(factory.AvailableModels));
    }
}
