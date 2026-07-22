# 项目术语表

> 所有子 agent 共享。累积跨变更的领域知识。

| 术语 | 含义 | 首次记录于 |
|------|------|-----------|
| ProblemDetails (RFC 9457) | 标准化的 HTTP API 错误响应格式，用于全局异常处理的统一返回结构 | 四合一架构重构 |
| 双重写路径 (Dual Write Path) | 消息持久化存在两个独立的写入路径导致数据不一致的架构问题 | 四合一架构重构 |
| 配置漂移 (Configuration Drift) | 代码中的配置值（如 MaxMessages=2）与设计文档约定值（如 20）不一致的现象 | 四合一架构重构 |
| IExceptionHandler | .NET 10 内置的全局异常处理接口，无需额外 NuGet 包 | 四合一架构重构 |
| ModelInfo | `public record ModelInfo(string Id, string Name, bool IsDefault)` 表示一个 AI 模型的信息，Id 为配置键，Name 为显示名称，IsDefault 标记是否为默认模型 | T3 |
| ModelRouter | 单例服务，管理多个 AI 模型的配置读取、Agent 实例创建、模型信息查询 | T3 |
| 模型卡片选择 | 登录页面的三列网格布局，每张卡片对应一个 AI 模型，支持点击切换选中态，isDefault 模型自动高亮 | T8 |
