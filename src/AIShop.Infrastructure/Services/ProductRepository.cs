using AIShop.Core.Entities;
using AIShop.Core.Interfaces;

namespace AIShop.Infrastructure.Services;

internal sealed class ProductRepository : IProductRepository
{
    public IReadOnlyList<Product> GetAll() => ProductCatalog.All;
}
