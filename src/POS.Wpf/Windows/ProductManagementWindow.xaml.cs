using System.Windows;
using POS.Wpf.Localization;
using POS.Wpf.ViewModels;

namespace POS.Wpf.Windows;

public partial class ProductManagementWindow : Window
{
    public ProductManagementWindow(ProductManagementViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        Locale.ApplyFlowDirection(this);
        Title = Locale.Get("ProductMgmt_Title");
        PmNewCategoryLbl.Text    = Locale.Get("ProductMgmt_NewCategory");
        PmAddCategoryBtn.Content = Locale.Get("ProductMgmt_AddCategory");
        PmAddProductBtn.Content  = Locale.Get("ProductMgmt_AddProduct");
        PmEditBtn.Content        = Locale.Get("ProductMgmt_Edit");
        PmDeleteBtn.Content      = Locale.Get("ProductMgmt_Delete");
        PmSnapshotHeader.Text    = Locale.Get("ProductMgmt_InventorySnapshot");
        PmRefreshInventoryBtn.Content = Locale.Get("ProductMgmt_RefreshHistory");
        PmHistoryHeader.Text     = Locale.Get("ProductMgmt_StockLedger");
        PmHistoryHint.Text       = Locale.Get("ProductMgmt_StockHistoryHint");
        if (ProductsGrid.Columns.Count >= 4)
        {
            ProductsGrid.Columns[0].Header = Locale.Get("Col_Name");
            ProductsGrid.Columns[1].Header = Locale.Get("Col_Barcode");
            ProductsGrid.Columns[2].Header = Locale.Get("Col_Price");
            ProductsGrid.Columns[3].Header = Locale.Get("Col_Qty");
        }

        if (StockMovementsGrid.Columns.Count >= 6)
        {
            StockMovementsGrid.Columns[0].Header = Locale.Get("Col_Product");
            StockMovementsGrid.Columns[1].Header = Locale.Get("Col_MovementType");
            StockMovementsGrid.Columns[2].Header = Locale.Get("Col_Change");
            StockMovementsGrid.Columns[3].Header = Locale.Get("Col_After");
            StockMovementsGrid.Columns[4].Header = Locale.Get("Col_Reference");
            StockMovementsGrid.Columns[5].Header = Locale.Get("Col_When");
        }

        if (DataContext is ProductManagementViewModel vm)
        {
            try
            {
                await vm.LoadAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, Locale.Get("App_TitleShort"), MessageBoxButton.OK, MessageBoxImage.Warning);
                Close();
            }
        }
    }
}
