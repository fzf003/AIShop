using Microsoft.EntityFrameworkCore;
using AIShop.Core.Entities;
using AIShop.Infrastructure.Entities;

namespace AIShop.Infrastructure.Data;

public sealed class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<User> Users => Set<User>();
    public DbSet<Session> Sessions => Set<Session>();
    public DbSet<ChatMessageRecord> ChatMessageRecords => Set<ChatMessageRecord>();
    public DbSet<Cart> Carts => Set<Cart>();
    public DbSet<CartItem> CartItems => Set<CartItem>();
    public DbSet<Product> Products => Set<Product>();
    public DbSet<UserPreferences> UserPreferences => Set<UserPreferences>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>(e =>
        {
            e.HasKey(u => u.Id);
            e.HasIndex(u => u.Username).IsUnique();
        });

        modelBuilder.Entity<Session>(e =>
        {
            e.HasKey(s => s.Id);
            e.HasIndex(s => s.UserId);
        });

        modelBuilder.Entity<ChatMessageRecord>(e =>
        {
            e.ToTable("chat_messages");
            e.HasKey(r => r.Id);
            e.Property(r => r.Id).ValueGeneratedOnAdd();
            e.Property(r => r.SessionId).HasColumnName("session_id");
            e.Property(r => r.Role).HasColumnName("role").HasColumnType("TEXT");
            e.Property(r => r.Content).HasColumnName("content").HasColumnType("TEXT");
            e.Property(r => r.ToolCalls).HasColumnName("tool_calls").HasColumnType("TEXT");
            e.Property(r => r.ToolCallId).HasColumnName("tool_call_id").HasColumnType("TEXT");
            e.Property(r => r.ToolName).HasColumnName("tool_name").HasColumnType("TEXT");
            e.Property(r => r.Reasoning).HasColumnName("reasoning").HasColumnType("TEXT");
            e.Property(r => r.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("datetime('now')");
            e.Property(r => r.IsCompacted).HasColumnName("is_compacted").HasDefaultValue(false);
            // 轮次边界字段：run_id 轮次分组（可空，TEXT 存 Guid 字符串）；is_final 轮次终点标记（默认 0）
            e.Property(r => r.RunId).HasColumnName("run_id").HasColumnType("TEXT");
            e.Property(r => r.IsFinal).HasColumnName("is_final").HasDefaultValue(false);
            // idx_cm_session_active 含 run_id 维度：支撑按轮分组压缩 + Provide 按 (session, is_compacted) 按 id 升序加载
            e.HasIndex(r => new { r.SessionId, r.IsCompacted, r.RunId, r.Id }).HasDatabaseName("idx_cm_session_active");
            e.HasIndex(r => new { r.SessionId, r.Id }).HasDatabaseName("idx_cm_session_id");
        });

        modelBuilder.Entity<Cart>(e =>
        {
            e.HasKey(c => c.Id);
            e.HasIndex(c => c.UserId).IsUnique();
            e.HasMany(c => c.Items)
                .WithOne()
                .HasForeignKey(i => i.CartId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<CartItem>(e =>
        {
            e.HasKey(i => i.Id);
            e.Property(i => i.Id).ValueGeneratedOnAdd();
            e.HasIndex(i => i.CartId);
        });

        modelBuilder.Entity<Product>(e =>
        {
            e.HasKey(p => p.Id);
            // 种子显式写入 Id 1..18，禁止数据库自增生成，保证与购物车/工具引用的 productId 一致
            e.Property(p => p.Id).ValueGeneratedNever();
            // Tags 为 string[]，依赖 EF 原始集合默认 JSON 列（SQLite 存 TEXT），无需显式配置
        });

        modelBuilder.Entity<UserPreferences>(e =>
        {
            e.HasKey(u => u.UserId);
            e.Property(u => u.KeywordsJson).HasColumnType("TEXT");
        });
    }
}
