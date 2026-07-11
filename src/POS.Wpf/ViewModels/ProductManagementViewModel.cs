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

    public ProductManagementViewModel(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    [ObservableProperty]
    private ObservableCollection<CategoryDto> _categories = new();

    [ObservableProperty]
    private ObservableCollection<ProductListItemDto> _products = new();

    [ObservableProperty]
    private ObservableCollection<StockMovementRow> _stockMovements = new();

    [ObservableProperty]
    private ProductListItemDto? _selectedProduct;

    [ObservableProperty]
    private string _newCategoryName = "";

    [ObservableProperty]
    private string _stockHistoryStatus = "";

    public async Task LoadAsync()
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var catalog = scope.ServiceProvider.GetRequiredService<IProductCatalogService>();
        var cats = await catalog.GetCategoriesAsync();
        Categories = new ObservableCollection<CategoryDto>(cats);
        var list = await catalog.SearchProductsAsync(null);
        Products = new ObservableCollection<ProductListItemDto>(list);
        await LoadStockMovementsSafeAsync(scope.ServiceProvider);
    }

    partial void OnSelectedProductChanged(ProductListItemDto? value) =>
        _ = LoadStockMovementsSafeAsync();

    [RelayCommand]
    private async Task AddCategoryAsync()
    {
        if (string.IsNullOrWhiteSpace(NewCategoryName))
            return;

        await using var scope = _scopeFactory.CreateAsyncScope();
        var catalog = scope.ServiceProvider.GetRequiredService<IProductCatalogService>();
        try
        {
            var c = await catalog.CreateCategoryAsync(NewCategoryName.Trim());
            Categories.Add(c);
            NewCategoryName = "";
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, Locale.Get("App_TitleShort"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

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
            CategoryId = Categories[0].Id,
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
            await LoadAsync();
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
            await LoadAsync();
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
            await LoadAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, Locale.Get("App_TitleShort"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

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

        StockMovements = new ObservableCollection<StockMovementRow>(movements.Select(m => new StockMovementRow(
            m.ProductName,
            TranslateMovementType(m.Type),
            m.QuantityDelta,
            m.QuantityAfter,
            m.Reference,
            m.CreatedAt)));

        StockHistoryStatus = SelectedProduct is null
            ? string.Format(CultureInfo.CurrentUICulture, Locale.Get("ProductMgmt_StockHistoryAllFormat"), StockMovements.Count)
            : string.Format(CultureInfo.CurrentUICulture, Locale.Get("ProductMgmt_StockHistoryFilteredFormat"), SelectedProduct.Name, StockMovements.Count);
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
    DateTime CreatedAt);
