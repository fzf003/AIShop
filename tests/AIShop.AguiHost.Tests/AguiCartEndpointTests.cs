using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AIShop.Core.StaticData;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;

namespace AIShop.AguiHost.Tests;

/// <summary>
/// agui-client-support T13 / T14：根级购物车 REST 端点（<c>GET /cart</c>、<c>POST /cart/items</c>、
/// <c>PUT /cart/items/{itemId:guid}</c>、<c>DELETE /cart/items/{itemId:guid}</c>、<c>DELETE /cart</c>）的契约测试
/// （DTO 形状、加购合并、改量绝对数量、移除、清空幂等、入参校验、身份必填 / 存在性校验、落库与「不另建实现」的静态源码断言）。
/// 覆盖 spec ADDED「客户端支撑端点的身份来源」（缺参 400 且不回落）、「新端点的用户存在性校验与错误契约」
/// （校验先于写入）、「购物车端点」（读 / 加购 / 合并 / 非法入参 / 改量与移除 / 条目不存在不假成功 / 清空幂等 /
/// 非 GUID 段不进业务）、「客户端支撑端点复用既有仓储」（落 agui.db、直查 CartItems 表）。
/// <para>
/// 驱动方式：WAF <b>真实宿主</b>（<see cref="WebApplicationFactory{TEntryPoint}"/>）+ 临时业务库（Program 的 T16
/// 环境变量 seam <c>Agui__DbConnection</c>）+ 真实播种——临时库由 <c>InitializeAsync</c> 的 <c>MigrateAsync</c>
/// 与幂等播种建好，18 个商品与 3 个种子用户（marla / steve / fzf003）<b>真实存在</b>，故<b>不替换</b>任何仓储替身
/// （「查无此人」用非种子名 <c>ghost</c>）；「落库」断言直查该临时业务库的 <c>Carts</c> / <c>CartItems</c> 表，
/// 证明数据确实落在 AguiHost 自己的 <c>agui.db</c>（此处为临时库）而非老库。
/// </para>
/// <para>
/// 挂 <c>[Collection(nameof(AguiRequestTests))]</c>（约束 D）：本类启动真实宿主（MigrateAsync / 播种 / RAG 预热）
/// 并操作进程级环境变量，必须与其它宿主级测试串行，避免并行迁移同一 SQLite 文件库 / 互相污染 seam。
/// </para>
/// </summary>
[Collection(nameof(AguiRequestTests))]
public sealed class AguiCartEndpointTests : IDisposable
{
    /// <summary>中间件缺参错误体片段（spec「客户端支撑端点的身份来源」，与 AG-UI 契约同源常量）。</summary>
    private const string UsernameRequiredBody = "\"detail\":\"Username is required\"";

    /// <summary>用户不存在的错误体片段（与 AG-UI 面逐字节一致）。</summary>
    private const string UserNotFoundBody = "\"detail\":\"User not found\"";

    /// <summary>数量非正的错误体片段。</summary>
    private const string QuantityMustBePositiveBody = "\"detail\":\"Quantity must be greater than 0\"";

    /// <summary>商品不存在的错误体片段。</summary>
    private const string ProductNotFoundBody = "\"detail\":\"Product not found\"";

    /// <summary>改 / 删的条目不在本人购物车时的错误体片段（预检失败，防 200 假成功）。</summary>
    private const string CartItemNotFoundBody = "\"detail\":\"Cart item not found\"";

    /// <summary>条目不存在文案（用于断言「非 GUID 段」的 404 响应体【不含】此文案——它由路由层而非端点产生）。</summary>
    private const string CartItemNotFoundLiteral = "Cart item not found";

    /// <summary>购物车端点的实现文件（静态源码断言对象）。</summary>
    private const string CartEndpointsSourcePath = "src/AIShop.AguiHost/AguiCartEndpoints.cs";

    /// <summary>购物车端点的注册点（装配断言：确实在 Program 里被映射）。</summary>
    private const string ProgramSourcePath = "src/AIShop.AguiHost/Program.cs";

    /// <summary>已存在的种子用户名（真实播种，身份校验会命中）。</summary>
    private const string ExistingUser = "marla";

    /// <summary>另一个已存在的种子用户（用于证明清空 / 预检只作用于该用户、不影响他人购物车）。</summary>
    private const string OtherExistingUser = "steve";

    private readonly List<string> _cleanupDirs = [];
    private readonly List<(string Key, string? Previous)> _envRestore = [];

    private string _businessDbPath = "";

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();

        // 恢复环境变量（host 构建完成后即恢复；残留会污染同进程后续测试的 Program 顶层 seam 读取）
        foreach (var (key, previous) in _envRestore)
            Environment.SetEnvironmentVariable(key, previous);
        _envRestore.Clear();

        foreach (var dir in _cleanupDirs)
        {
            try
            {
                if (Directory.Exists(dir))
                    Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
                // 文件仍被占用时忽略，交由系统清理
            }
        }
        _cleanupDirs.Clear();
    }

    [Fact]
    public async Task GetCart_WhenNoCartRow_Returns200WithEmptyCartNot404()
    {
        // spec「购物车端点」场景 1（读取空购物车）：无购物车记录 → 200 + 空车结构，【不是】404
        // （老链同义；前端空车态无需区分「没有车」与「车是空的」）。
        var factory = StartFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync($"/cart?username={ExistingUser}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;

        Assert.Equal(Guid.Empty, root.GetProperty("id").GetGuid());
        Assert.Empty(root.GetProperty("items").EnumerateArray().ToList());
        Assert.Equal(0, root.GetProperty("totalItems").GetInt32());
        Assert.Equal(0m, root.GetProperty("totalPrice").GetDecimal());
        // 空车结构仍带一个合法时间戳（updatedAt 字段契约存在，前端可直接渲染）
        Assert.NotEqual(default, root.GetProperty("updatedAt").GetDateTime());

        await factory.DisposeAsync();
    }

    [Fact]
    public async Task AddCartItem_PersistsItemAndReturnsUpdatedCart()
    {
        // spec「购物车端点」场景 2（加购成功并返回更新后的购物车）+「复用既有仓储」场景 1（走同一仓储与同一张表）：
        // 返回体含新条目（数量 / 名称 / 单价 / emoji 取自商品目录）且【已落库】——直查临时业务库 CartItems。
        // 商品取自种子数据（不硬编码字面量），数量以播种结果为准。
        var product = ProductSeedData.Products[0];

        var factory = StartFactory();
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync(
            $"/cart/items?username={ExistingUser}",
            new { productId = product.Id, quantity = 2 });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;
        var items = root.GetProperty("items").EnumerateArray().ToList();
        Assert.Single(items);

        var item = items[0];
        Assert.Equal(product.Id, item.GetProperty("productId").GetInt32());
        Assert.Equal(product.Name, item.GetProperty("productName").GetString());
        Assert.Equal(product.Price, item.GetProperty("productPrice").GetDecimal());
        Assert.Equal(product.Emoji, item.GetProperty("productEmoji").GetString());
        Assert.Equal(2, item.GetProperty("quantity").GetInt32());
        Assert.Equal(2, root.GetProperty("totalItems").GetInt32());
        Assert.Equal(product.Price * 2, root.GetProperty("totalPrice").GetDecimal());

        // 落库断言：先释放宿主与 SQLite 连接池，再直查临时业务库
        await factory.DisposeAsync();
        SqliteConnection.ClearAllPools();

        var (rows, quantity) = await ReadCartItemAsync(_businessDbPath, product.Id);
        Assert.Equal(1L, rows);
        Assert.Equal(2L, quantity);
    }

    [Fact]
    public async Task AddCartItem_SameProductTwice_MergesQuantityWithoutDuplicateRow()
    {
        // spec「购物车端点」场景 3（重复加购按既有规则合并）：同商品再购 → 数量累加、不产生第二条同商品条目。
        // 合并规则由既有 Cart.AddItem 承担，端点不复制该规则——本用例同时锁定「响应内单条目」与「库内单行」。
        var product = ProductSeedData.Products[0];

        var factory = StartFactory();
        using var client = factory.CreateClient();

        using var first = await client.PostAsJsonAsync(
            $"/cart/items?username={ExistingUser}",
            new { productId = product.Id, quantity = 2 });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        using var second = await client.PostAsJsonAsync(
            $"/cart/items?username={ExistingUser}",
            new { productId = product.Id, quantity = 1 });
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        using var json = JsonDocument.Parse(await second.Content.ReadAsStringAsync());
        var root = json.RootElement;
        var items = root.GetProperty("items").EnumerateArray().ToList();
        Assert.Single(items);
        Assert.Equal(3, items[0].GetProperty("quantity").GetInt32());
        Assert.Equal(3, root.GetProperty("totalItems").GetInt32());

        await factory.DisposeAsync();
        SqliteConnection.ClearAllPools();

        var (rows, quantity) = await ReadCartItemAsync(_businessDbPath, product.Id);
        Assert.Equal(1L, rows);
        Assert.Equal(3L, quantity);
    }

    [Fact]
    public async Task AddCartItem_InvalidQuantityOrUnknownProduct_Returns400AndLeavesCartUnchanged()
    {
        // spec「购物车端点」场景 4（非法数量与不存在商品被拒）：quantity 非正与 productId 不存在分别返回 400
        // 与各自文案，且购物车【无任何变化】（入参校验全部发生在写入之前）。
        var knownProductId = ProductSeedData.Products[0].Id;
        var unknownProductId = ProductSeedData.Products.Max(p => p.Id) + 1;

        var factory = StartFactory();
        using var client = factory.CreateClient();

        using var zero = await client.PostAsJsonAsync(
            $"/cart/items?username={ExistingUser}",
            new { productId = knownProductId, quantity = 0 });
        Assert.Equal(HttpStatusCode.BadRequest, zero.StatusCode);
        Assert.Contains(QuantityMustBePositiveBody, await zero.Content.ReadAsStringAsync());

        using var negative = await client.PostAsJsonAsync(
            $"/cart/items?username={ExistingUser}",
            new { productId = knownProductId, quantity = -3 });
        Assert.Equal(HttpStatusCode.BadRequest, negative.StatusCode);
        Assert.Contains(QuantityMustBePositiveBody, await negative.Content.ReadAsStringAsync());

        using var unknownProduct = await client.PostAsJsonAsync(
            $"/cart/items?username={ExistingUser}",
            new { productId = unknownProductId, quantity = 2 });
        Assert.Equal(HttpStatusCode.BadRequest, unknownProduct.StatusCode);
        Assert.Contains(ProductNotFoundBody, await unknownProduct.Content.ReadAsStringAsync());

        await factory.DisposeAsync();
        SqliteConnection.ClearAllPools();

        Assert.Equal(0L, await CountTableRowsAsync(_businessDbPath, "Carts"));
        Assert.Equal(0L, await CountTableRowsAsync(_businessDbPath, "CartItems"));
    }

    [Fact]
    public async Task GetCart_WithoutUsername_Returns400AndDoesNotFallBackToDefaultUser()
    {
        // spec「客户端支撑端点的身份来源」场景 2（缺失身份显式拒绝且不回落）：REST 面永不回落缺省用户——
        // 缺 ?username= 由中间件 400 短路（不注入、不进端点），而不是按 AguiUsernameForwarder.DefaultUsername 处理。
        var factory = StartFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/cart");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(UsernameRequiredBody, await response.Content.ReadAsStringAsync());

        await factory.DisposeAsync();
        SqliteConnection.ClearAllPools();

        // 「未回落缺省用户」的库侧证据：本测试只发过这一个请求（无任何其它写入），故整表计数即缺省用户的购物车行数。
        Assert.Equal(0L, await CountTableRowsAsync(_businessDbPath, "Carts"));
        Assert.Equal(0L, await CountTableRowsAsync(_businessDbPath, "CartItems"));
    }

    [Fact]
    public async Task AddCartItem_UnknownUser_Returns404AndWritesNothing()
    {
        // spec「新端点的用户存在性校验与错误契约」场景 3（校验先于写入）：用户不存在时即便商品与数量均合法，
        // 也必须 404 + {"detail":"User not found"} 且购物车表无新增行（校验发生在写入之前）。
        var product = ProductSeedData.Products[0];

        var factory = StartFactory();
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync(
            "/cart/items?username=ghost",
            new { productId = product.Id, quantity = 2 });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains(UserNotFoundBody, await response.Content.ReadAsStringAsync());

        await factory.DisposeAsync();
        SqliteConnection.ClearAllPools();

        Assert.Equal(0L, await CountTableRowsAsync(_businessDbPath, "CartItems"));
    }

    [Fact]
    public async Task UpdateThenRemoveCartItem_ReturnsUpdatedCartAndPersistsChanges()
    {
        // spec「购物车端点」场景 5（改量与移除成功）：PUT 把条目设为【绝对数量】5（不是 2+5 累加），
        // 随后 DELETE 该条目 → 200 且返回体不再含该条目；库侧条目已删、无残留。
        var product = ProductSeedData.Products[0];

        var factory = StartFactory();
        using var client = factory.CreateClient();

        var itemId = await AddItemAndGetItemIdAsync(client, ExistingUser, product.Id, quantity: 2);

        using var updated = await client.PutAsJsonAsync(
            $"/cart/items/{itemId}?username={ExistingUser}", new { quantity = 5 });
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);

        using (var json = JsonDocument.Parse(await updated.Content.ReadAsStringAsync()))
        {
            var root = json.RootElement;
            var items = root.GetProperty("items").EnumerateArray().ToList();
            Assert.Single(items);
            Assert.Equal(itemId, items[0].GetProperty("id").GetGuid());
            Assert.Equal(5, items[0].GetProperty("quantity").GetInt32());
            Assert.Equal(5, root.GetProperty("totalItems").GetInt32());
        }

        using var removed = await client.DeleteAsync($"/cart/items/{itemId}?username={ExistingUser}");
        Assert.Equal(HttpStatusCode.OK, removed.StatusCode);

        using (var json = JsonDocument.Parse(await removed.Content.ReadAsStringAsync()))
        {
            var root = json.RootElement;
            Assert.Empty(root.GetProperty("items").EnumerateArray().ToList());
            Assert.Equal(0, root.GetProperty("totalItems").GetInt32());
        }

        await factory.DisposeAsync();
        SqliteConnection.ClearAllPools();

        // 库侧：该商品的条目行已随 DELETE 删除（PUT 时确实写到了 5，删除后归零）
        var (rows, quantity) = await ReadCartItemAsync(_businessDbPath, product.Id);
        Assert.Equal(0L, rows);
        Assert.Equal(0L, quantity);
    }

    [Fact]
    public async Task UpdateOrRemoveCartItem_ForeignOrUnknownItemId_Returns404AndLeavesAllCartsUnchanged()
    {
        // spec「购物车端点」场景 6（条目不存在不得假成功）：itemId 不在 marla 车里 —— 含【属于另一用户购物车】的条目，
        // 也必须 404 + {"detail":"Cart item not found"}，且任何购物车条目均未被修改或删除。
        // 这是「预检防仓储静默 no-op 的 200 假成功」的核心断言（改 / 删两端点各验一次）。
        var marlaProduct = ProductSeedData.Products[0];
        var steveProduct = ProductSeedData.Products[1];

        var factory = StartFactory();
        using var client = factory.CreateClient();

        _ = await AddItemAndGetItemIdAsync(client, ExistingUser, marlaProduct.Id, quantity: 2);
        var steveItemId = await AddItemAndGetItemIdAsync(client, OtherExistingUser, steveProduct.Id, quantity: 3);
        var unknownItemId = Guid.NewGuid();

        // ① 属于 steve 的条目：marla 改量 / 移除 → 均 404
        using (var foreignPut = await client.PutAsJsonAsync(
            $"/cart/items/{steveItemId}?username={ExistingUser}", new { quantity = 9 }))
        {
            Assert.Equal(HttpStatusCode.NotFound, foreignPut.StatusCode);
            Assert.Contains(CartItemNotFoundBody, await foreignPut.Content.ReadAsStringAsync());
        }

        using (var foreignDelete = await client.DeleteAsync($"/cart/items/{steveItemId}?username={ExistingUser}"))
        {
            Assert.Equal(HttpStatusCode.NotFound, foreignDelete.StatusCode);
            Assert.Contains(CartItemNotFoundBody, await foreignDelete.Content.ReadAsStringAsync());
        }

        // ② 完全不存在的条目：marla 改量 / 移除 → 均 404
        using (var unknownPut = await client.PutAsJsonAsync(
            $"/cart/items/{unknownItemId}?username={ExistingUser}", new { quantity = 9 }))
        {
            Assert.Equal(HttpStatusCode.NotFound, unknownPut.StatusCode);
            Assert.Contains(CartItemNotFoundBody, await unknownPut.Content.ReadAsStringAsync());
        }

        using (var unknownDelete = await client.DeleteAsync($"/cart/items/{unknownItemId}?username={ExistingUser}"))
        {
            Assert.Equal(HttpStatusCode.NotFound, unknownDelete.StatusCode);
            Assert.Contains(CartItemNotFoundBody, await unknownDelete.Content.ReadAsStringAsync());
        }

        await factory.DisposeAsync();
        SqliteConnection.ClearAllPools();

        // 「任何条目均未被修改或删除」：steve 的条目数量仍为 3（没被 marla 的 PUT 改成 9、也没被 DELETE 删掉）
        var (steveRows, steveQuantity) = await ReadCartItemAsync(_businessDbPath, steveProduct.Id);
        Assert.Equal(1L, steveRows);
        Assert.Equal(3L, steveQuantity);

        // marla 自己的条目也未被这些失败请求改动
        var (marlaRows, marlaQuantity) = await ReadCartItemAsync(_businessDbPath, marlaProduct.Id);
        Assert.Equal(1L, marlaRows);
        Assert.Equal(2L, marlaQuantity);
    }

    [Fact]
    public async Task UpdateCartItem_NonPositiveQuantity_Returns400AndLeavesQuantityUnchanged()
    {
        // spec「购物车端点」第 4 段：改量的 quantity 非正必须 400（拦在 Cart.UpdateItemQuantity 抛异常之前），
        // 且条目数量【不变】（校验发生在写入之前）。
        var product = ProductSeedData.Products[0];

        var factory = StartFactory();
        using var client = factory.CreateClient();

        var itemId = await AddItemAndGetItemIdAsync(client, ExistingUser, product.Id, quantity: 2);

        using (var zero = await client.PutAsJsonAsync(
            $"/cart/items/{itemId}?username={ExistingUser}", new { quantity = 0 }))
        {
            Assert.Equal(HttpStatusCode.BadRequest, zero.StatusCode);
            Assert.Contains(QuantityMustBePositiveBody, await zero.Content.ReadAsStringAsync());
        }

        using (var negative = await client.PutAsJsonAsync(
            $"/cart/items/{itemId}?username={ExistingUser}", new { quantity = -4 }))
        {
            Assert.Equal(HttpStatusCode.BadRequest, negative.StatusCode);
            Assert.Contains(QuantityMustBePositiveBody, await negative.Content.ReadAsStringAsync());
        }

        await factory.DisposeAsync();
        SqliteConnection.ClearAllPools();

        var (rows, quantity) = await ReadCartItemAsync(_businessDbPath, product.Id);
        Assert.Equal(1L, rows);
        Assert.Equal(2L, quantity);
    }

    [Fact]
    public async Task ClearCart_RemovesOwnItemsOnlyAndIsIdempotent()
    {
        // spec「购物车端点」场景 7（清空购物车且幂等）：marla 有 2 个条目 → DELETE /cart → 200 空车；
        // 【再次】调用仍 200 空车（幂等）；库侧该用户条目全删，【其他用户购物车不受影响】。
        var marlaFirst = ProductSeedData.Products[0];
        var marlaSecond = ProductSeedData.Products[1];
        var steveProduct = ProductSeedData.Products[2];

        var factory = StartFactory();
        using var client = factory.CreateClient();

        _ = await AddItemAndGetItemIdAsync(client, ExistingUser, marlaFirst.Id, quantity: 2);
        _ = await AddItemAndGetItemIdAsync(client, ExistingUser, marlaSecond.Id, quantity: 1);
        _ = await AddItemAndGetItemIdAsync(client, OtherExistingUser, steveProduct.Id, quantity: 4);

        using (var cleared = await client.DeleteAsync($"/cart?username={ExistingUser}"))
        {
            Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);

            using var json = JsonDocument.Parse(await cleared.Content.ReadAsStringAsync());
            var root = json.RootElement;
            Assert.Empty(root.GetProperty("items").EnumerateArray().ToList());
            Assert.Equal(0, root.GetProperty("totalItems").GetInt32());
            Assert.Equal(0m, root.GetProperty("totalPrice").GetDecimal());
        }

        // 幂等：无购物车可清时同样 200 空车（仓储内部 no-op，不抛、不建行）
        using (var again = await client.DeleteAsync($"/cart?username={ExistingUser}"))
        {
            Assert.Equal(HttpStatusCode.OK, again.StatusCode);

            using var json = JsonDocument.Parse(await again.Content.ReadAsStringAsync());
            Assert.Empty(json.RootElement.GetProperty("items").EnumerateArray().ToList());
            Assert.Equal(0, json.RootElement.GetProperty("totalItems").GetInt32());
        }

        await factory.DisposeAsync();
        SqliteConnection.ClearAllPools();

        // 该用户两个条目均已删除
        Assert.Equal(0L, (await ReadCartItemAsync(_businessDbPath, marlaFirst.Id)).Rows);
        Assert.Equal(0L, (await ReadCartItemAsync(_businessDbPath, marlaSecond.Id)).Rows);

        // 其他用户购物车不受影响：steve 的条目仍在、数量仍为 4；整表只剩这一行
        var (steveRows, steveQuantity) = await ReadCartItemAsync(_businessDbPath, steveProduct.Id);
        Assert.Equal(1L, steveRows);
        Assert.Equal(4L, steveQuantity);
        Assert.Equal(1L, await CountTableRowsAsync(_businessDbPath, "CartItems"));
    }

    [Fact]
    public async Task NonGuidItemSegment_Returns404WithoutEnteringBusinessHandling()
    {
        // spec「购物车端点」场景 8（非 GUID 条目段不进入业务处理）：路由约束 {itemId:guid} 不匹配 →
        // 框架直接 404，【不进 handler】——故响应体不含端点的 "Cart item not found"（空体），也不暴露任何用户数据。
        var factory = StartFactory();
        using var client = factory.CreateClient();

        using (var delete = await client.DeleteAsync($"/cart/items/not-a-guid?username={ExistingUser}"))
        {
            Assert.Equal(HttpStatusCode.NotFound, delete.StatusCode);
            Assert.DoesNotContain(CartItemNotFoundLiteral, await delete.Content.ReadAsStringAsync());
        }

        using (var put = await client.PutAsJsonAsync(
            $"/cart/items/not-a-guid?username={ExistingUser}", new { quantity = 1 }))
        {
            Assert.Equal(HttpStatusCode.NotFound, put.StatusCode);
            Assert.DoesNotContain(CartItemNotFoundLiteral, await put.Content.ReadAsStringAsync());
        }

        await factory.DisposeAsync();
        SqliteConnection.ClearAllPools();

        // 未进入业务处理 → 零副作用
        Assert.Equal(0L, await CountTableRowsAsync(_businessDbPath, "Carts"));
        Assert.Equal(0L, await CountTableRowsAsync(_businessDbPath, "CartItems"));
    }

    [Fact]
    public void CartEndpointSource_UsesExistingRepositoriesWithoutSecondImplementation()
    {
        // spec「客户端支撑端点复用既有仓储」：购物车端点的唯一读写入口是既有 ICartRepository、商品走既有
        // IProductRepository.GetAll()，不得出现第二套实现 / 第二份商品查询 / 新的数据访问栈。
        // 期望存在的文件缺失即显式失败（ReadRepoFile 内 FileNotFoundException），避免断言空转。
        var source = ReadRepoFile(CartEndpointsSourcePath);
        var programSource = ReadRepoFile(ProgramSourcePath);

        // 正向：确实经既有仓储供数（否则「复用既有仓储」无从谈起）
        Assert.Contains("ICartRepository", source);
        Assert.Contains("IProductRepository", source);
        Assert.Contains("GetAll", source);

        // 反向：端点层不得直连 EF 数据上下文 / 不得自行提交更改（一出现即说明另建了数据访问路径）
        Assert.DoesNotContain("AppDbContext", source);
        Assert.DoesNotContain("DbContext", source);
        Assert.DoesNotContain("SaveChangesAsync", source);

        // 装配：注册点确实调用了扩展方法（否则 /cart 根本没被映射）
        Assert.Contains("MapCartEndpoints", programSource);
    }

    /// <summary>
    /// 装配 WAF 真实宿主：临时业务 / 向量 / 会话 / 聊天历史四套库（Program T16 环境变量 seam
    /// <c>Agui__*Connection</c>），<b>不替换任何服务</b>——商品与用户都来自真实播种，这正是本类断言的
    /// 「同一批仓储 / 同一批用户」前提。
    /// </summary>
    /// <remarks>
    /// 环境变量在 <c>CreateClient()</c>（触发 host 构建、Program 顶层读取 seam 的时点）之后立即恢复，
    /// 防进程级污染同进程后续测试。宿主离线可启动：模型工厂构造只解析配置、底层客户端懒建。
    /// </remarks>
    private WebApplicationFactory<Program> StartFactory()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"agui_t13_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _cleanupDirs.Add(dir);

        _businessDbPath = Path.Combine(dir, "business.db");

        SetEnvironment("Agui__DbConnection", $"Data Source={_businessDbPath}");
        SetEnvironment("Agui__RagConnection", $"Data Source={Path.Combine(dir, "rag.db")}");
        SetEnvironment("Agui__SessionConnection", $"Data Source={Path.Combine(dir, "sessions.db")}");
        SetEnvironment("Agui__ChatConnection", $"Data Source={Path.Combine(dir, "chat.db")}");

        try
        {
            var factory = new WebApplicationFactory<Program>();
            // CreateClient() 触发 host 构建（Program 顶层读取 seam 配置的时点），构建完成后即可恢复环境变量
            _ = factory.CreateClient();
            return factory;
        }
        finally
        {
            RestoreEnvironment();
        }
    }

    private void SetEnvironment(string key, string value)
    {
        _envRestore.Add((key, Environment.GetEnvironmentVariable(key)));
        Environment.SetEnvironmentVariable(key, value);
    }

    private void RestoreEnvironment()
    {
        foreach (var (key, previous) in _envRestore)
            Environment.SetEnvironmentVariable(key, previous);
        _envRestore.Clear();
    }

    /// <summary>
    /// 经 <c>POST /cart/items</c> 加购并返回<b>该商品</b>条目的 <c>itemId</c>（T14 的改量 / 移除需要真实存在的条目 ID）。
    /// 断言响应 200 且该商品在车内恰有一条（重复加购会合并而非新增行，故 <c>Single</c> 成立）；
    /// 若加购未生效，调用方拿到的会是 <c>Guid.Empty</c>，后续断言随之显式失败。
    /// </summary>
    private static async Task<Guid> AddItemAndGetItemIdAsync(
        HttpClient client, string username, int productId, int quantity)
    {
        using var response = await client.PostAsJsonAsync(
            $"/cart/items?username={username}", new { productId, quantity });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("items").EnumerateArray()
            .Single(i => i.GetProperty("productId").GetInt32() == productId)
            .GetProperty("id").GetGuid();
    }

    /// <summary>
    /// 直查临时业务库某表行数（落库 / 零写入断言用）：库文件不存在 → 0；表不存在 → 0（都表示「未写入」）。
    /// 计数语句按表名分支写成字面量（不做 SQL 字符串拼接，避免注入面与静态分析告警）。
    /// </summary>
    private static async Task<long> CountTableRowsAsync(string dbPath, string table)
    {
        if (!File.Exists(dbPath))
            return 0;

        await using var connection = new SqliteConnection($"Data Source={dbPath}");
        await connection.OpenAsync();

        if (!await TableExistsAsync(connection, table))
            return 0;

        await using var countCommand = connection.CreateCommand();
        countCommand.CommandText = table switch
        {
            "Carts" => "SELECT COUNT(*) FROM Carts",
            "CartItems" => "SELECT COUNT(*) FROM CartItems",
            _ => throw new ArgumentOutOfRangeException(nameof(table), table, "未覆盖的表名"),
        };
        return (long)(await countCommand.ExecuteScalarAsync() ?? 0L);
    }

    /// <summary>
    /// 直查临时业务库中某商品的 CartItems 条目数与数量合计（<c>(行数, 数量合计)</c>）：库 / 表不存在 → <c>(0, 0)</c>。
    /// 行数用于断言「同商品不产生重复条目」，数量合计用于断言「合并 / 绝对数量」的落库结果。
    /// </summary>
    private static async Task<(long Rows, long Quantity)> ReadCartItemAsync(string dbPath, int productId)
    {
        if (!File.Exists(dbPath))
            return (0, 0);

        await using var connection = new SqliteConnection($"Data Source={dbPath}");
        await connection.OpenAsync();

        if (!await TableExistsAsync(connection, "CartItems"))
            return (0, 0);

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*), COALESCE(SUM(Quantity), 0) FROM CartItems WHERE ProductId = $productId";
        command.Parameters.AddWithValue("$productId", productId);

        await using var reader = await command.ExecuteReaderAsync();
        return await reader.ReadAsync() ? (reader.GetInt64(0), reader.GetInt64(1)) : (0, 0);
    }

    private static async Task<bool> TableExistsAsync(SqliteConnection connection, string table)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $name";
        command.Parameters.AddWithValue("$name", table);
        return (long)(await command.ExecuteScalarAsync() ?? 0L) > 0;
    }

    /// <summary>
    /// 仓库根定位（约束 E）：从 <see cref="AppContext.BaseDirectory"/> 上溯找含 <c>AIShop.sln</c> 的目录。
    /// <b>不用</b> <c>Path.GetFullPath(相对路径)</c>——它相对测试进程 CWD（bin/Debug/net10.0）解析，会让断言空转。
    /// </summary>
    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "AIShop.sln")))
                return dir.FullName;
        }

        throw new InvalidOperationException($"未能从 {AppContext.BaseDirectory} 上溯找到含 AIShop.sln 的仓库根");
    }

    /// <summary>读取仓库内文件全文；<b>期望存在的文件缺失即显式失败</b>（不得静默跳过，否则断言空转）。</summary>
    private static string ReadRepoFile(string relativePath)
    {
        var fullPath = Path.Combine(FindRepoRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(fullPath))
            throw new FileNotFoundException($"期望存在的仓库文件缺失：{fullPath}", fullPath);

        return File.ReadAllText(fullPath);
    }
}
