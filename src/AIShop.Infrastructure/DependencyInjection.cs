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
        services.AddSingleton<IProductCatalogService, ProductCatalog>();
        services.AddScoped<IChatMessageRepository, ChatMessageRepository>();
        services.AddScoped<IUserRepository, UserRepository>();
        services.AddScoped<ISessionRepository, SessionRepository>();
        services.AddScoped<IProductRepository, ProductRepository>();
        services.AddScoped<ICartRepository, CartRepository>();

        return services;
    }
}
