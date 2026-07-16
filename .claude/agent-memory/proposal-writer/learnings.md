# proposal-writer 经验记忆

> 每次完成提案后自动追加。启动时读取，帮助理解项目上下文。

## 项目领域术语

<!-- 记录从 exploration.md 中发现的核心概念 -->

## 架构约束

<!-- 发现的必须遵守的架构规则 -->

- ProductCatalog 无状态纯逻辑类应注册为 Singleton 而非 Scoped，节省对象创建和 GC 开销
- Infrastructure 层不得依赖任何 Agent/AI 框架包（Microsoft.Agents.*、Microsoft.Extensions.AI.*），Agent 相关类型需迁移到 Api 层
- IUnitOfWork 在仅有一个仓储的项目规模下属于过度抽象，SaveChangesAsync 直接在仓储接口上定义
- 全局异常处理中间件 UseExceptionHandler 必须在所有 MapXxxEndpoints 之前注册
- McpServer 作为独立可执行程序允许例外引用 Infrastructure

## 设计模式

<!-- 记录从提案中提炼的可复用模式 -->

- **前端分类过滤模式**：当展示数据量小（<= 50 条）且数据已在前端就绪时，纯前端过滤（分类/搜索/排序）比后端新增查询参数方案改动量更小、交付更快，零后端风险
- **Agent Singleton 优化模式**：对于无状态 AI Agent，将所有依赖验证为 Singleton 后改为 AddSingleton 注册，可避免每次请求重建 Agent 的昂贵开销（200-500ms），降幅达 100x
- **合并变更模式**：两个互不干扰的优化方向（纯前端 + 纯后端）可合并为一个变更管理，降低管理开销，仍允许独立实施和独立验证

## 常用备选方案

<!-- 本次未采用但值得记录的方案 -->

- **IUnitOfWork 模式**：被否定理由为单一仓储时过度抽象
- **保留静态类 + IServiceProvider 门面**：被否定理由为服务定位器反模式，无法 Mock
- **条件编译保留 Infrastructure MAF 引用**：被否定理由为复杂度高、无法编译验证
- **后端新增搜索/分类/排序 API**：被否定理由为 18 条数据不值得服务端过滤，增加交付周期
- **保持 Scoped + 缓存 BuildInstructions 结果**：被否定理由为不如改 Singleton 彻底，引入额外缓存逻辑增加复杂度
