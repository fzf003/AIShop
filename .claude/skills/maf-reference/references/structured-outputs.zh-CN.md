# 结构化输出

使用 JSON Schema 从 Agent 生成类型化的结构化数据。当底层聊天客户端兼容时，`ChatClientAgent` 支持此功能。

## 命名空间

```csharp
using Microsoft.Extensions.AI;
```

## 方法 1：`RunAsync<T>`（编译时类型）

输出类型在编译时已知时使用：

```csharp
public class PersonInfo
{
    public string? Name { get; set; }
    public int? Age { get; set; }
    public string? Occupation { get; set; }
}

// 非流式
AgentResponse<PersonInfo> response = await agent.RunAsync<PersonInfo>(
    "请提供关于张三的信息，他是一名 35 岁的软件工程师。");

Console.WriteLine($"姓名: {response.Result.Name}, 年龄: {response.Result.Age}");

// 支持基元、数组和复杂类型
// 数组需要包装在容器类型中（不支持直接使用 List<T>）：
public class MovieListWrapper
{
    public List<string> Movies { get; set; }
}
```

## 方法 2：`ResponseFormat`（动态/运行时类型）

编译时类型未知，或需要使用原始 JSON Schema 时：

```csharp
using System.Text.Json;

AgentRunOptions runOptions = new()
{
    ResponseFormat = ChatResponseFormat.ForJsonSchema<PersonInfo>()
};

AgentResponse response = await agent.RunAsync(
    "请提供关于张三的信息",
    options: runOptions);

PersonInfo personInfo = JsonSerializer.Deserialize<PersonInfo>(
    response.Text, JsonSerializerOptions.Web)!;
```

### 使用原始 JSON Schema 字符串

```csharp
string jsonSchema = """
{
    "type": "object",
    "properties": {
        "name": { "type": "string" },
        "age": { "type": "integer" },
        "occupation": { "type": "string" }
    },
    "required": ["name", "age", "occupation"]
}
""";

AgentRunOptions runOptions = new()
{
    ResponseFormat = ChatResponseFormat.ForJsonSchema(
        JsonElement.Parse(jsonSchema),
        "PersonInfo",
        "个人信息")
};

AgentResponse response = await agent.RunAsync("...", options: runOptions);
JsonElement result = JsonSerializer.Deserialize<JsonElement>(response.Text);
```

## 方法 3：在 Agent 创建时配置（支持流式）

在构造 Agent 时于 `ChatOptions` 中设置 `ResponseFormat`：

```csharp
var agent = new ChatClientAgent(chatClient, new ChatClientAgentOptions
{
    Name = "HelpfulAssistant",
    ChatOptions = new()
    {
        Instructions = "你是一个有用的助手。",
        ResponseFormat = ChatResponseFormat.ForJsonSchema<PersonInfo>()
    }
});

// 流式 — 先收集所有更新，再反序列化
IAsyncEnumerable<AgentResponseUpdate> updates = agent.RunStreamingAsync(
    "请提供关于张三的信息");

AgentResponse response = await updates.ToAgentResponseAsync();
PersonInfo personInfo = JsonSerializer.Deserialize<PersonInfo>(response.Text)!;
```

## 内置响应格式

| 格式 | 用法 | 说明 |
|---|---|---|
| `ChatResponseFormat.Text` | `ResponseFormat = ChatResponseFormat.Text` | 纯文本响应 |
| `ChatResponseFormat.Json` | `ResponseFormat = ChatResponseFormat.Json` | JSON 对象，无特定 Schema |
| `ChatResponseFormat.ForJsonSchema<T>()` | 泛型方法 | 匹配类型 T 的 Schema 的 JSON |
| `ChatResponseFormat.ForJsonSchema(JsonElement, string, string?)` | 原始 Schema | 匹配自定义 JSON Schema 的 JSON |

## 对于不支持原生结构化输出的 Agent

创建装饰器 Agent，包装任何 `AIAgent`，通过额外 LLM 调用将文本响应转换为结构化 JSON：

```csharp
// 参考：https://github.com/microsoft/agent-framework/blob/main/dotnet/samples/02-agents/Agents/Agent_Step02_StructuredOutput
```

由于此方法依赖额外 LLM 调用，可靠性可能不够稳定。

## 关键约定

- `RunAsync<T>` 支持基元、数组（通过包装器类型）和复杂类型
- `ResponseFormat` **不直接支持**基元和数组——请使用 `RunAsync<T>` 或包装为容器类型
- 流式处理时，始终先收集所有更新为完整的 `AgentResponse`，再反序列化
- 并非所有 Agent 类型都支持原生结构化输出——`ChatClientAgent` 在兼容的聊天客户端下支持
