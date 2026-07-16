# MAF NuGet Packages

| Package | Version | Purpose |
|---------|---------|---------|
| `Microsoft.Agents.AI` | 1.10.0 | Core agent framework (ChatClientAgent, ChatHistoryProvider) |
| `Microsoft.Agents.AI.OpenAI` | 1.10.0 | OpenAI integration |
| `Microsoft.Agents.AI.Abstractions` | 1.10.0 | Abstractions (transitive via Microsoft.Agents.AI) |
| `Microsoft.Extensions.AI` | 10.7.0 | IChatClient, ChatMessage, ChatRole |
| `Microsoft.Extensions.AI.OpenAI` | 10.7.0 | OpenAI IChatClient — `AsIChatClient()` extension |
| `Microsoft.Extensions.AI.Abstractions` | 10.7.0 | Core AI abstractions |
| `OpenAI` | 2.10.0 | Official OpenAI SDK (transitive via Microsoft.Extensions.AI.OpenAI) |

## Version Compatibility

### .NET SDK 要求

| 包版本 | 最低 .NET SDK | 推荐 .NET SDK |
|--------|-------------|--------------|
| MAF 1.10.x | .NET 8.0 | .NET 10.0 |
| MEAI 10.x | .NET 8.0 | .NET 10.0 |

### 包依赖关系

`Microsoft.Agents.AI` 1.10.0 依赖:
- `Microsoft.Extensions.AI` ≥ 10.6.0
- `Microsoft.Extensions.AI.Abstractions` ≥ 10.6.0

`Microsoft.Agents.AI.OpenAI` 1.10.0 依赖:
- `Microsoft.Agents.AI` 1.10.0
- `Microsoft.Extensions.AI.OpenAI` ≥ 10.6.0
- `OpenAI` ≥ 2.10.0

### 已验证的版本组合

| 组合 | MAF | MEAI | OpenAI SDK | 状态 |
|------|-----|------|-----------|------|
| A | 1.10.0 | 10.6.0 | 2.10.0 | ✅ 已验证 |
| B | 1.10.0 | 10.7.0 | 2.10.0 | ✅ 已验证 |

> **注意**：`Microsoft.Agents.Core` 不是 `Microsoft.Agents.AI` 或 `Microsoft.Agents.AI.OpenAI` 的依赖项。
> 除非需要使用 builder/hosting 功能，否则不要添加它。
