# AITool / Function Tool Registration

Agents register tools via `Microsoft.Extensions.AI.AITool` and `AIFunction`.

## AIFunctionFactory — Create from method

```csharp
using System.ComponentModel;
using Microsoft.Extensions.AI;

// Static method → AIFunction
[Description("Get the weather for a given location")]
static string GetWeather([Description("The location")] string location)
    => $"{location} is cloudy, 15°C";

var weatherTool = AIFunctionFactory.Create(GetWeather);

// Pass to agent
var agent = chatClient.AsAIAgent(
    instructions: "You are helpful",
    tools: [weatherTool]);
```

## AIFunctionFactory — From delegate/MethodInfo

```csharp
// From lambda
var tool = AIFunctionFactory.Create(
    ([Description("Product name")] string name) => SearchProduct(name),
    "SearchProduct",
    "Search for products");

// From MethodInfo
var methodInfo = typeof(ProductService).GetMethod("SearchProducts");
var tool2 = AIFunctionFactory.Create(methodInfo, target: productService);
```

## Register as AITool in ChatClientAgentOptions

```csharp
var agent = new ChatClientAgent(chatClient, new ChatClientAgentOptions
{
    Name = "ShoppingAgent",
    ChatOptions = new ChatOptions
    {
        Tools = [AIFunctionFactory.Create(GetWeather)]
    }
});
```

## Register tools via DI (Hosting mode)

```csharp
builder.AddAIAgent("Agent", "You are helpful")
    .WithAITool(AIFunctionFactory.Create(GetWeather))
    .WithAITool(sp => new ProductSearchTool(sp.GetRequiredService<IProductRepository>()),
               ServiceLifetime.Singleton);
```

## Tool invocation lifecycle

When `UseProvidedChatClientAsIs = false` (default), the framework auto-wraps with `FunctionInvokingChatClient`:

1. Agent sends messages + tool definitions to LLM
2. LLM returns `FunctionCallContent`
3. `FunctionInvokingChatClient` auto-executes the matching `AIFunction`
4. Tool result is sent back as `FunctionResultContent` to LLM
5. LLM generates final response based on results

## Key conventions

- Annotate tool methods with `[Description]` and parameter `[Description]` — LLM relies on these for correct invocation
- All tools auto-execute by default without user approval
- Custom `FunctionInvocationDelegatingAgent` required for approval workflows
- Tool arguments should be treated as untrusted input — validate them
