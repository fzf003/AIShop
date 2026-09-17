using AIShop.Core.Interfaces;

namespace AIShop.AguiHost;

/// <summary>
/// AguiHost 的商品 REST 端点（agui-client-support 第 6 项，design §13.2 / §13.8）。
/// <para>
/// <c>GET /products</c>：返回<b>全量商品目录</b>（裸 JSON 数组，camelCase：<c>id</c>/<c>name</c>/<c>category</c>/
/// <c>tags</c>/<c>price</c>/<c>emoji</c>），供前端渲染「全部商品」网格。
/// </para>
/// <para>
/// <b>数据源单一</b>：只取 <see cref="IProductRepository.GetAll()"/>（<c>ProductCatalog.All</c> 亦委托于它，与
/// AI 工具 <c>search_product</c>、加购的商品校验共用同一份 5 分钟缓存）——端点层<b>不得</b>直用 EF 数据库上下文、
/// <b>不得</b>另写商品查询 / 映射逻辑（spec R17 场景 2）。这也是本端点没有投影逻辑、直接返回实体数组的原因：
/// 字段名与形状交给 Web 默认序列化（camelCase），避免出现第二份「商品 → DTO」映射而与实体分叉。
/// </para>
/// <para>
/// <b>公开可读（有意为之的取舍，勿当漏洞改）</b>：端点标记 <see cref="AguiClientRestEndpoint.RequireUsername"/> = <c>false</c>
/// ——未携带 <c>?username=</c> 时中间件<b>不注入身份</b>并放行，端点返回 200。理由：该响应<b>不含任何用户数据</b>
/// （无购物车 / 会话 / 历史），且与请求上下文无关；前端在<b>选定身份之前</b>（登录页选模型阶段 / 账户选择页）就要
/// 拉商品网格渲染——强制要求身份只会锁死登录页，换不到任何安全收益（与 <c>/models</c> 的公开可读取舍同源）。
/// <b>但「不要求身份」≠「忽略身份」</b>：携带了非空 <c>?username=</c> 仍走存在性校验（查无此人 → 404），
/// 避免「客户端写了个不存在的用户名却被静默放行」这一数据质量盲区（design §13.4）。
/// </para>
/// <para>
/// 身份解析本身不在这里——6 个客户端支撑端点的「解析 → 存在性校验 → 注入 → 短路」只有
/// <see cref="AguiUsernameForwarder.UseAguiUsernameForwarding"/> 中间件一处实现，端点只声明「缺参是否拒绝」。
/// </para>
/// </summary>
internal static class AguiProductEndpoints
{
    /// <summary>
    /// 映射 AguiHost 的商品端点（根级路径——AguiHost 无 <c>/api</c> 分组，与 <c>/models</c> 同层）。
    /// 路径与 <c>"/"</c>（AG-UI SSE）、<c>/models</c>、<c>/cart</c> 系列、<c>/health</c>、<c>/alive</c>、
    /// <c>/devui</c>、<c>/v1/*</c> 均不冲突；映射顺序不影响请求管线（路由注册与中间件顺序无关）。
    /// </summary>
    internal static void MapProductEndpoints(this WebApplication app)
    {
        // 公开可读：显式 RequireUsername = false —— 缺 ?username= 时中间件放行且【不注入身份】（不是回落缺省用户），
        // 端点只读商品目录、不读用户；带了非空值则仍由中间件做存在性校验（404 / 注入后放行）。
        // 标记是中间件 REST 分支的唯一依据（设计 §13.3：不按 HTTP 方法、不按路径字符串判定）。
        app.MapGet("/products", (IProductRepository products) => Results.Ok(products.GetAll()))
            .WithMetadata(new AguiClientRestEndpoint { RequireUsername = false });
    }
}
