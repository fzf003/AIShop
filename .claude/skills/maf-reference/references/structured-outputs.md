<!-- lastSynced: 2026-06-24 -->

# Structured Outputs

Generate typed, structured data from agents using JSON Schema. Available on `ChatClientAgent` when backed by a compatible chat client.

## Namespace

```csharp
using Microsoft.Extensions.AI;
```

## Method 1: `RunAsync<T>` (compile-time type)

When the output type is known at compile time:

```csharp
public class PersonInfo
{
    public string? Name { get; set; }
    public int? Age { get; set; }
    public string? Occupation { get; set; }
}

// Non-streaming
AgentResponse<PersonInfo> response = await agent.RunAsync<PersonInfo>(
    "Please provide information about John Smith, a 35-year-old software engineer.");

Console.WriteLine($"Name: {response.Result.Name}, Age: {response.Result.Age}");

// Supports primitives, arrays, and complex types
// For arrays, wrap in a container type (List<T> not directly supported):
public class MovieListWrapper
{
    public List<string> Movies { get; set; }
}
```

## Method 2: `ResponseFormat` (dynamic/runtime type)

When the type is unknown at compile time, or you need raw JSON schema:

```csharp
using System.Text.Json;

AgentRunOptions runOptions = new()
{
    ResponseFormat = ChatResponseFormat.ForJsonSchema<PersonInfo>()
};

AgentResponse response = await agent.RunAsync(
    "Please provide information about John Smith",
    options: runOptions);

PersonInfo personInfo = JsonSerializer.Deserialize<PersonInfo>(
    response.Text, JsonSerializerOptions.Web)!;
```

### With raw JSON schema string

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
        JsonDocument.Parse(jsonSchema).RootElement,
        "PersonInfo",
        "Information about a person")
};

AgentResponse response = await agent.RunAsync("...", options: runOptions);
JsonElement result = JsonSerializer.Deserialize<JsonElement>(response.Text);
```

## Method 3: Configure on agent creation (with streaming)

Set `ResponseFormat` in `ChatOptions` at agent construction time:

```csharp
var agent = new ChatClientAgent(chatClient, new ChatClientAgentOptions
{
    Name = "HelpfulAssistant",
    ChatOptions = new()
    {
        Instructions = "You are a helpful assistant.",
        ResponseFormat = ChatResponseFormat.ForJsonSchema<PersonInfo>()
    }
});

// Streaming — collect all updates first, then deserialize
IAsyncEnumerable<AgentResponseUpdate> updates = agent.RunStreamingAsync(
    "Please provide information about John Smith");

AgentResponse response = await updates.ToAgentResponseAsync();
PersonInfo personInfo = JsonSerializer.Deserialize<PersonInfo>(response.Text)!;
```

## Built-in response formats

| Format | Usage | Description |
|---|---|---|
| `ChatResponseFormat.Text` | `ResponseFormat = ChatResponseFormat.Text` | Plain text response |
| `ChatResponseFormat.Json` | `ResponseFormat = ChatResponseFormat.Json` | JSON object, no schema |
| `ChatResponseFormat.ForJsonSchema<T>()` | Generic method | JSON matching the schema of type T |
| `ChatResponseFormat.ForJsonSchema(JsonElement, string, string?)` | Raw schema | JSON matching a custom JSON schema |

## For agents without native structured output support

Create a decorator agent that wraps any `AIAgent` and converts text responses to structured JSON via an extra LLM call:

```csharp
// Reference: https://github.com/microsoft/agent-framework/blob/main/dotnet/samples/02-agents/Agents/Agent_Step02_StructuredOutput
```

Since this adds an extra LLM call, reliability may vary.

## Key conventions

- `RunAsync<T>` supports primitives, arrays (via wrapper type), and complex types
- `ResponseFormat` does NOT support primitives and arrays directly — use `RunAsync<T>` or wrap in a container type
- For streaming, always collect all updates into a complete `AgentResponse` before deserializing
- Not all agent types support native structured output — `ChatClientAgent` does with compatible chat clients
