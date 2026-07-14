using AIShop.Core.Entities;
using AIShop.Core.Interfaces;

namespace AIShop.Infrastructure.Services;

internal sealed class ProductRepository(IProductCatalogService catalog) : IProductRepository
{
    public IReadOnlyList<Product> GetAll() => catalog.All;
}
