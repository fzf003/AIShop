using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using AIShop.Core.Interfaces;
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
        services.AddScoped<IChatMessageRepository, ChatMessageRepository>();
        services.AddScoped<IUserRepository, UserRepository>();
        services.AddScoped<ISessionRepository, SessionRepository>();
        services.AddScoped<IProductRepository, ProductRepository>();
        services.AddScoped<ICartRepository, CartRepository>();

        // 偏好持久化（T15，design 4.2/4.4 修正）：PreferenceWriteHostedService 构造函数注入的是
        // 具体 PreferenceQueue（IPreferenceQueue 无读端，worker 需要读 Channel），故需同时注册
        // 具体类型与接口映射，两者共享同一单例实例；AddHostedService 注册后台 worker 启动消费队列。
        services.AddScoped<IPreferenceRepository, PreferenceRepository>();
        services.AddSingleton<PreferenceQueue>(PreferenceQueue.Create());
        services.AddSingleton<IPreferenceQueue>(sp => sp.GetRequiredService<PreferenceQueue>());
        services.AddHostedService<PreferenceWriteHostedService>();

        return services;
    }
}
