using POS.Application.Models;

namespace POS.Application.Abstractions;

public interface IProductCatalogService
{
    Task<IReadOnlyList<CategoryDto>> GetCategoriesAsync(CancellationToken cancellationToken = default);
    Task<CategoryDto> CreateCategoryAsync(string name, CancellationToken cancellationToken = default);
    Task<CategoryDto> UpdateCategoryAsync(Guid id, string name, CancellationToken cancellationToken = default);
    Task DeleteCategoryAsync(Guid id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ProductListItemDto>> SearchProductsAsync(string? query, Guid? categoryId = null, CancellationToken cancellationToken = default, bool includeInactive = false);
    Task<ProductEditDto?> GetProductForEditAsync(Guid id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<StockMovementDto>> GetStockMovementsAsync(Guid? productId = null, CancellationToken cancellationToken = default);
    Task<ProductEditDto> CreateProductAsync(ProductEditDto input, CancellationToken cancellationToken = default);
    Task<ProductEditDto> UpdateProductAsync(ProductEditDto input, CancellationToken cancellationToken = default);
    Task DeleteProductAsync(Guid id, CancellationToken cancellationToken = default);
}
