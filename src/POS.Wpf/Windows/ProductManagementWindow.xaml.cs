using System.Windows;
using System.Windows.Controls;
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

        Title                    = Locale.Get("ProductMgmt_Title");
        HeaderTitleTb.Text       = Locale.Get("ProductMgmt_Title");
        AddProductBtnTb.Text     = Locale.Get("ProductMgmt_AddProduct");
        CategoriesHeaderTb.Text  = Locale.Get("ProductMgmt_CategoriesHeader");
        AddCategoryIconBtn.ToolTip = Locale.Get("ProductMgmt_AddCategory");
        SearchPlaceholderTb.Text = Locale.Get("ProductMgmt_SearchPlaceholder");
        EmptyTitleTb.Text        = Locale.Get("ProductMgmt_EmptyTitle");
        EmptySubtitleTb.Text     = Locale.Get("ProductMgmt_EmptySubtitle");
        NoSelectionHintTb.Text   = Locale.Get("ProductMgmt_NoSelectionHint");
        PriceTileLabelTb.Text    = Locale.Get("Col_Price");
        StockTileLabelTb.Text    = Locale.Get("ProductMgmt_StockLabel");
        EditProductBtn.Content   = Locale.Get("ProductMgmt_Edit");
        DeleteProductBtn.Content = Locale.Get("ProductMgmt_Delete");
        ActivityHeaderTb.Text    = Locale.Get("ProductMgmt_ActivityHeader");
        NoActivityTb.Text        = Locale.Get("ProductMgmt_NoActivity");

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

    private void ProductsListBox_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (DataContext is not ProductManagementViewModel vm)
            return;

        // Ignore double-clicks on empty space below the last row.
        if ((sender as ListBox)?.SelectedItem is null)
            return;

        if (vm.EditProductCommand.CanExecute(null))
            vm.EditProductCommand.Execute(null);
    }
}
