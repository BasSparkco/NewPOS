using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using POS.Application.Abstractions;
using POS.Application.Models;
using POS.Core.Enums;
using POS.Wpf.Localization;
using POS.Wpf.Windows;

namespace POS.Wpf.ViewModels;

public partial class ProductManagementViewModel : ObservableObject
{
    private readonly IServiceScopeFactory _scopeFactory;
    private List<ProductListItemDto> _allProducts = new();
    private Guid? _selectedCategoryId;

    public ProductManagementViewModel(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    [ObservableProperty]
    private ObservableCollection<CategoryDto> _categories = new();

    [ObservableProperty]
    private ObservableCollection<CategoryRailItem> _categoryRail = new();

    [ObservableProperty]
    private ObservableCollection<ProductListItemDto> _products = new();

    [ObservableProperty]
    private ObservableCollection<StockMovementRow> _stockMovements = new();

    [ObservableProperty]
    private ProductListItemDto? _selectedProduct;

    [ObservableProperty]
    private string _searchQuery = "";

    [ObservableProperty]
    private string _newCategoryName = "";

    [ObservableProperty]
    private bool _isAddingCategory;

    [ObservableProperty]
    private string _stockHistoryStatus = "";

    [ObservableProperty]
    private string _headerSubtitle = "";

    [ObservableProperty]
    private string _resultCountLabel = "";

    public bool IsEmptyResult => Products.Count == 0;
    public bool HasSelection => SelectedProduct is not null;
    public bool HasNoActivity => StockMovements.Count == 0;

    public async Task LoadAsync()
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var catalog = scope.ServiceProvider.GetRequiredService<IProductCatalogService>();

        var cats = await catalog.GetCategoriesAsync();
        Categories = new ObservableCollection<CategoryDto>(cats);

        var list = await catalog.SearchProductsAsync(null, includeInactive: true);
        _allProducts = list.OrderBy(p => p.Name).ToList();

        BuildCategoryRail();
        ApplyFilter();

        HeaderSubtitle = string.Format(
            CultureInfo.CurrentUICulture,
            Locale.Get("ProductMgmt_SubtitleFormat"),
            _allProducts.Count,
            Categories.Count);

        await LoadStockMovementsSafeAsync(scope.ServiceProvider);
    }

    private async Task ReloadAsync(Guid? keepSelectedProductId = null)
    {
        await LoadAsync();
        if (keepSelectedProductId.HasValue)
            SelectedProduct = _allProducts.FirstOrDefault(p => p.Id == keepSelectedProductId.Value);
    }

    private void BuildCategoryRail()
    {
        var counts = _allProducts
            .GroupBy(p => p.CategoryId)
            .ToDictionary(g => g.Key, g => g.Count());

        var rail = new ObservableCollection<CategoryRailItem>
        {
            new()
            {
                CategoryId = null,
                Name = Locale.Get("ProductMgmt_AllProducts"),
                Count = _allProducts.Count,
                IsSelected = _selectedCategoryId is null
            }
        };

        foreach (var c in Categories)
            rail.Add(new CategoryRailItem
            {
                CategoryId = c.Id,
                Name = c.Name,
                Count = counts.GetValueOrDefault(c.Id),
                IsSelected = _selectedCategoryId == c.Id
            });

        CategoryRail = rail;
    }

    private void ApplyFilter()
    {
        IEnumerable<ProductListItemDto> query = _allProducts;

        if (_selectedCategoryId.HasValue)
            query = query.Where(p => p.CategoryId == _selectedCategoryId.Value);

        if (!string.IsNullOrWhiteSpace(SearchQuery))
        {
            var term = SearchQuery.Trim();
            query = query.Where(p =>
                p.Name.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                (p.Barcode is not null && p.Barcode.Contains(term, StringComparison.OrdinalIgnoreCase)));
        }

        var filtered = query.ToList();
        Products = new ObservableCollection<ProductListItemDto>(filtered);
        OnPropertyChanged(nameof(IsEmptyResult));

        ResultCountLabel = filtered.Count == _allProducts.Count
            ? string.Format(CultureInfo.CurrentUICulture, Locale.Get("ProductMgmt_CountAllFormat"), filtered.Count)
            : string.Format(CultureInfo.CurrentUICulture, Locale.Get("ProductMgmt_CountFilteredFormat"), filtered.Count, _allProducts.Count);
    }

    partial void OnSearchQueryChanged(string value) => ApplyFilter();

    partial void OnSelectedProductChanged(ProductListItemDto? value)
    {
        OnPropertyChanged(nameof(HasSelection));
        _ = LoadStockMovementsSafeAsync();
    }

    [RelayCommand]
    private void SelectCategory(CategoryRailItem? item)
    {
        foreach (var c in CategoryRail) c.IsSelected = false;
        if (item is not null) item.IsSelected = true;
        _selectedCategoryId = item?.CategoryId;
        ApplyFilter();
    }

    // ═══════════════════════════════════════════════════════════════
    // Categories
    // ═══════════════════════════════════════════════════════════════

    [RelayCommand]
    private void ToggleAddCategory()
    {
        IsAddingCategory = !IsAddingCategory;
        NewCategoryName = "";
    }

    [RelayCommand]
    private async Task AddCategoryAsync()
    {
        if (string.IsNullOrWhiteSpace(NewCategoryName))
            return;

        await using var scope = _scopeFactory.CreateAsyncScope();
        var catalog = scope.ServiceProvider.GetRequiredService<IProductCatalogService>();
        try
        {
            await catalog.CreateCategoryAsync(NewCategoryName.Trim());
            NewCategoryName = "";
            IsAddingCategory = false;
            await ReloadAsync(SelectedProduct?.Id);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, Locale.Get("App_TitleShort"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    [RelayCommand]
    private void BeginRenameCategory(CategoryRailItem? item)
    {
        if (item?.CategoryId is null)
            return;
        item.EditName = item.Name;
        item.IsEditing = true;
    }

    [RelayCommand]
    private void CancelRenameCategory(CategoryRailItem? item)
    {
        if (item is not null)
            item.IsEditing = false;
    }

    [RelayCommand]
    private async Task ConfirmRenameCategoryAsync(CategoryRailItem? item)
    {
        if (item?.CategoryId is null || string.IsNullOrWhiteSpace(item.EditName))
            return;

        await using var scope = _scopeFactory.CreateAsyncScope();
        var catalog = scope.ServiceProvider.GetRequiredService<IProductCatalogService>();
        try
        {
            await catalog.UpdateCategoryAsync(item.CategoryId.Value, item.EditName.Trim());
            item.IsEditing = false;
            await ReloadAsync(SelectedProduct?.Id);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, Locale.Get("App_TitleShort"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    [RelayCommand]
    private async Task DeleteCategoryAsync(CategoryRailItem? item)
    {
        if (item?.CategoryId is null)
            return;

        var confirmMsg = string.Format(CultureInfo.CurrentUICulture, Locale.Get("Msg_DeleteConfirmFormat"), item.Name);
        if (MessageBox.Show(confirmMsg, Locale.Get("Msg_ConfirmTitle"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        await using var scope = _scopeFactory.CreateAsyncScope();
        var catalog = scope.ServiceProvider.GetRequiredService<IProductCatalogService>();
        try
        {
            await catalog.DeleteCategoryAsync(item.CategoryId.Value);
            if (_selectedCategoryId == item.CategoryId)
                _selectedCategoryId = null;
            await ReloadAsync(SelectedProduct?.Id);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, Locale.Get("App_TitleShort"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // Products
    // ═══════════════════════════════════════════════════════════════

    [RelayCommand]
    private async Task AddProductAsync()
    {
        if (Categories.Count == 0)
        {
            MessageBox.Show(Locale.Get("Msg_CreateCategoryFirst"), Locale.Get("App_TitleShort"), MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var edit = new ProductEditDto
        {
            CategoryId = _selectedCategoryId ?? Categories[0].Id,
            InitialStock = 0,
            Price = 0,
            Cost = 0
        };
        var dlg = new Windows.ProductEditWindow(edit, Categories.ToList()) { Owner = System.Windows.Application.Current.MainWindow };
        if (dlg.ShowDialog() != true)
            return;

        await using var scope = _scopeFactory.CreateAsyncScope();
        var catalog = scope.ServiceProvider.GetRequiredService<IProductCatalogService>();
        try
        {
            await catalog.CreateProductAsync(dlg.Result!);
            await ReloadAsync(dlg.Result!.Id);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, Locale.Get("App_TitleShort"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    [RelayCommand]
    private async Task EditProductAsync()
    {
        if (SelectedProduct is null)
            return;

        await using var scope = _scopeFactory.CreateAsyncScope();
        var catalog = scope.ServiceProvider.GetRequiredService<IProductCatalogService>();
        var existing = await catalog.GetProductForEditAsync(SelectedProduct.Id);
        if (existing is null)
        {
            MessageBox.Show(Locale.Get("Msg_ProductNotFound"), Locale.Get("App_TitleShort"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var dlg = new ProductEditWindow(existing, Categories.ToList()) { Owner = System.Windows.Application.Current.MainWindow };
        if (dlg.ShowDialog() != true)
            return;

        try
        {
            await catalog.UpdateProductAsync(dlg.Result!);
            await ReloadAsync(dlg.Result!.Id);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, Locale.Get("App_TitleShort"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    [RelayCommand]
    private async Task ToggleActiveAsync()
    {
        if (SelectedProduct is null)
            return;

        await using var scope = _scopeFactory.CreateAsyncScope();
        var catalog = scope.ServiceProvider.GetRequiredService<IProductCatalogService>();
        try
        {
            var full = await catalog.GetProductForEditAsync(SelectedProduct.Id);
            if (full is null)
                return;

            full.IsActive = !full.IsActive;
            await catalog.UpdateProductAsync(full);
            await ReloadAsync(full.Id);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, Locale.Get("App_TitleShort"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    [RelayCommand]
    private async Task DeleteProductAsync()
    {
        if (SelectedProduct is null)
            return;

        var confirmMsg = string.Format(CultureInfo.CurrentUICulture, Locale.Get("Msg_DeleteConfirmFormat"), SelectedProduct.Name);
        if (MessageBox.Show(confirmMsg, Locale.Get("Msg_ConfirmTitle"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        await using var scope = _scopeFactory.CreateAsyncScope();
        var catalog = scope.ServiceProvider.GetRequiredService<IProductCatalogService>();
        try
        {
            await catalog.DeleteProductAsync(SelectedProduct.Id);
            await ReloadAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, Locale.Get("App_TitleShort"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // Stock activity
    // ═══════════════════════════════════════════════════════════════

    [RelayCommand]
    private Task RefreshInventoryAsync() => LoadStockMovementsSafeAsync();

    private async Task LoadStockMovementsSafeAsync(IServiceProvider? serviceProvider = null)
    {
        try
        {
            await LoadStockMovementsAsync(serviceProvider);
        }
        catch (Exception ex)
        {
            StockMovements = new ObservableCollection<StockMovementRow>();
            OnPropertyChanged(nameof(HasNoActivity));
            StockHistoryStatus = $"{Locale.Get("ProductMgmt_StockLedger")}: {ex.Message}";
        }
    }

    private async Task LoadStockMovementsAsync(IServiceProvider? serviceProvider = null)
    {
        if (serviceProvider is null)
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            await LoadStockMovementsAsync(scope.ServiceProvider);
            return;
        }

        var catalog = serviceProvider.GetRequiredService<IProductCatalogService>();
        var movements = await catalog.GetStockMovementsAsync(SelectedProduct?.Id);

        StockMovements = new ObservableCollection<StockMovementRow>(movements.Take(30).Select(m => new StockMovementRow(
            m.ProductName,
            TranslateMovementType(m.Type),
            m.QuantityDelta,
            m.QuantityAfter,
            m.Reference,
            m.CreatedAt)));
        OnPropertyChanged(nameof(HasNoActivity));

        StockHistoryStatus = Locale.ToDisplayDigits(SelectedProduct is null
            ? string.Format(CultureInfo.CurrentUICulture, Locale.Get("ProductMgmt_StockHistoryAllFormat"), StockMovements.Count)
            : string.Format(CultureInfo.CurrentUICulture, Locale.Get("ProductMgmt_StockHistoryFilteredFormat"), SelectedProduct.Name, StockMovements.Count));
    }

    private static string TranslateMovementType(StockMovementType type) => Locale.Get(type switch
    {
        StockMovementType.OpeningStock => "MovementType_OpeningStock",
        StockMovementType.ManualSetAdjustment => "MovementType_ManualSetAdjustment",
        StockMovementType.Sale => "MovementType_Sale",
        StockMovementType.Refund => "MovementType_Refund",
        _ => "MovementType_ManualSetAdjustment"
    });
}

public sealed record StockMovementRow(
    string ProductName,
    string TypeLabel,
    decimal QuantityDelta,
    decimal QuantityAfter,
    string? Reference,
    DateTime CreatedAt)
{
    public bool IsPositive => QuantityDelta >= 0;
}
