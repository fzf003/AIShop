using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using AIShop.Core.Interfaces;
using AIShop.Core.Services;
using AIShop.Infrastructure.Data;
using AIShop.Infrastructure.Repositories;
using AIShop.Infrastructure.Services;

namespace AIShop.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, string? connectionString = null)
    {
        connectionString ??= "Data Source=aishop.db";

        services.AddDbContextFactory<AppDbContext>(options =>
            options.UseSqlite(connectionString));
        services.AddScoped<AppDbContext>(sp =>
            sp.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContext());

        services.AddMemoryCache();
        services.AddScoped<IProductCatalogService, ProductCatalog>();
        services.AddScoped<RecommendationService>();
        services.AddScoped<IChatMessageRepository, ChatMessageRepository>();
        services.AddScoped<IUserRepository, UserRepository>();
        services.AddScoped<ISessionRepository, SessionRepository>();
        services.AddScoped<IProductRepository, ProductRepository>();
        services.AddScoped<ICartRepository, CartRepository>();

        // 当前用户访问器（P9）：AsyncLocal 封装为显式接口，单例被长生命周期 Agent/工具提供者安全持有
        services.AddSingleton<ICurrentUserAccessor, CurrentUserAccessor>();

        // 聊天历史存储（P2 步骤 2）：ChatHistoryStore 内部用 IDbContextFactory 短生命周期 DbContext，
        // 无状态可安全 Singleton，供长生命周期 ShoppingAssistantAgent 持有；压缩策略为纯规则，Singleton。
        services.AddSingleton<IChatHistoryStore, ChatHistoryStore>();
        services.AddSingleton<IChatCompactionPolicy, RoundBasedCompactionPolicy>();

        // 旧偏好机制（UserPreferences 表 + PreferenceQueue + PreferenceWriteHostedService）已被
        // Mem0 记忆服务（AddMemoryService）取代，2026-09-02 从 DI 拆除；实现文件保留，仅不再注册。

        return services;
    }
}
