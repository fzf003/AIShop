namespace AIShop.Service;

public sealed record AgentChatResult(
    string Reply, string[] Keywords, string[]? Preferences);
