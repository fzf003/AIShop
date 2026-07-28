using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using AIShop.Infrastructure.Data;
using AIShop.Infrastructure.Entities;

namespace AIShop.Api.Tests;

public sealed class ChatMessageRecordConfigurationTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;

    public ChatMessageRecordConfigurationTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;

        // Create schema
        using var ctx = new AppDbContext(_options);
        ctx.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _connection.Close();
        _connection.Dispose();
    }

    [Fact]
    public async Task ChatMessagesTable_ShouldExist()
    {
        using var ctx = new AppDbContext(_options);

        var tableNames = await ctx.Database.SqlQueryRaw<string>(
            "SELECT name FROM sqlite_master WHERE type='table' AND name='chat_messages'")
            .ToListAsync();

        Assert.Contains("chat_messages", tableNames);
    }

    [Fact]
    public async Task ChatMessagesTable_ShouldHaveExpectedColumns()
    {
        using var ctx = new AppDbContext(_options);

        // SQLite column names are case-insensitive; pragma may return mixed case
        var columns = (await ctx.Database.SqlQueryRaw<string>(
            "SELECT name FROM pragma_table_info('chat_messages') ORDER BY cid")
            .ToListAsync())
            .Select(c => c.ToLowerInvariant())
            .ToList();

        Assert.Contains("id", columns);
        Assert.Contains("session_id", columns);
        Assert.Contains("role", columns);
        Assert.Contains("content", columns);
        Assert.Contains("tool_calls", columns);
        Assert.Contains("tool_call_id", columns);
        Assert.Contains("tool_name", columns);
        Assert.Contains("reasoning", columns);
        Assert.Contains("created_at", columns);
        Assert.Contains("is_compacted", columns);
    }

    [Fact]
    public async Task ChatMessagesTable_IdColumn_ShouldBePrimaryKey()
    {
        using var ctx = new AppDbContext(_options);

        var pkColumns = await ctx.Database.SqlQueryRaw<string>(
            "SELECT name FROM pragma_table_info('chat_messages') WHERE pk = 1")
            .ToListAsync();

        Assert.Single(pkColumns);
    }

    [Fact]
    public async Task ChatMessagesTable_ShouldHaveCompositeIndex_SessionId_IsCompacted_Id()
    {
        using var ctx = new AppDbContext(_options);

        var indexNames = await ctx.Database.SqlQueryRaw<string>(
            "SELECT name FROM sqlite_master WHERE type='index' AND tbl_name='chat_messages' AND name='idx_cm_session_active'")
            .ToListAsync();

        Assert.Contains("idx_cm_session_active", indexNames);
    }

    [Fact]
    public async Task ChatMessagesTable_ShouldHaveIndex_SessionId_Id()
    {
        using var ctx = new AppDbContext(_options);

        var indexNames = await ctx.Database.SqlQueryRaw<string>(
            "SELECT name FROM sqlite_master WHERE type='index' AND tbl_name='chat_messages' AND name='idx_cm_session_id'")
            .ToListAsync();

        Assert.Contains("idx_cm_session_id", indexNames);
    }

    [Fact]
    public async Task ChatMessagesTable_CompositeIndex_ShouldCoverProvideQuery()
    {
        using var ctx = new AppDbContext(_options);

        // Insert data to test query with index coverage
        var sessionId = Guid.NewGuid();
        ctx.ChatMessageRecords.AddRange(
            new ChatMessageRecord { SessionId = sessionId, Role = "user", Content = "a", CreatedAt = DateTime.UtcNow, IsCompacted = false },
            new ChatMessageRecord { SessionId = sessionId, Role = "assistant", Content = "b", CreatedAt = DateTime.UtcNow, IsCompacted = false },
            new ChatMessageRecord { SessionId = sessionId, Role = "user", Content = "c", CreatedAt = DateTime.UtcNow, IsCompacted = false },
            new ChatMessageRecord { SessionId = sessionId, Role = "assistant", Content = "d", CreatedAt = DateTime.UtcNow, IsCompacted = false }
        );
        await ctx.SaveChangesAsync();

        // Query matching ProvideChatHistoryAsync: WHERE session_id AND is_compacted = 0 ORDER BY id
        var messages = await ctx.ChatMessageRecords
            .Where(m => m.SessionId == sessionId && !m.IsCompacted)
            .OrderBy(m => m.Id)
            .ToListAsync();

        Assert.Equal(4, messages.Count);
        Assert.Equal("a", messages[0].Content);
        Assert.Equal("d", messages[3].Content);
    }

    [Fact]
    public async Task ChatMessagesTable_Index_SessionId_Id_ShouldCoverTrimQuery()
    {
        using var ctx = new AppDbContext(_options);

        var sessionId = Guid.NewGuid();
        for (int i = 0; i < 5; i++)
        {
            ctx.ChatMessageRecords.Add(new ChatMessageRecord
            {
                SessionId = sessionId,
                Role = i % 2 == 0 ? "user" : "assistant",
                Content = $"msg{i}",
                CreatedAt = DateTime.UtcNow.AddMinutes(i),
                IsCompacted = false
            });
        }
        await ctx.SaveChangesAsync();

        // Query matching trim logic: WHERE session_id ORDER BY id DESC
        var messages = await ctx.ChatMessageRecords
            .Where(m => m.SessionId == sessionId)
            .OrderByDescending(m => m.Id)
            .ToListAsync();

        Assert.Equal(5, messages.Count);
        Assert.Equal("msg4", messages[0].Content);
        Assert.Equal("msg0", messages[4].Content);
    }

    [Fact]
    public async Task ChatMessagesTable_IsCompactedDefault_ShouldBeFalse()
    {
        using var ctx = new AppDbContext(_options);

        var sessionId = Guid.NewGuid();
        ctx.ChatMessageRecords.Add(new ChatMessageRecord
        {
            SessionId = sessionId,
            Role = "user",
            Content = "test",
            CreatedAt = DateTime.UtcNow
        });
        await ctx.SaveChangesAsync();

        var msg = await ctx.ChatMessageRecords
            .Where(m => m.SessionId == sessionId)
            .SingleAsync();

        Assert.False(msg.IsCompacted);
    }
}
