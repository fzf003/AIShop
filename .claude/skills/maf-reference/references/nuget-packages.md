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

`Microsoft.Agents.AI` 1.10.0 depends on:
- `Microsoft.Extensions.AI` 10.6.0+
- `Microsoft.Extensions.AI.Abstractions` 10.6.0+

`Microsoft.Agents.AI.OpenAI` 1.10.0 depends on:
- `Microsoft.Agents.AI` 1.10.0
- `Microsoft.Extensions.AI.OpenAI` 10.6.0+
- `OpenAI` 2.10.0

**Note**: `Microsoft.Agents.Core` is NOT required by `Microsoft.Agents.AI` or `Microsoft.Agents.AI.OpenAI`. Do not add it unless you need builder/hosting functionality.
