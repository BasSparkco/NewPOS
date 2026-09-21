using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using POS.Application.Models;
using POS.Wpf.Converters;
using POS.Wpf.Localization;

namespace POS.Wpf.Windows;

public partial class ProductEditWindow : Window
{
    private readonly ProductEditDto _model;
    private readonly bool _isNew;
    private static readonly FilePathToImageConverter _imgConverter = new();

    public ProductEditWindow(ProductEditDto model, IReadOnlyList<CategoryDto> categories)
    {
        InitializeComponent();
        _model = model;
        _isNew = model.Id == Guid.Empty;
        ApplyLocalization();
        Title = _isNew ? Locale.Get("Product_AddTitle") : Locale.Get("Product_EditTitle");

        // Bind simple fields
        NameBox.Text    = model.Name;
        BarcodeBox.Text = model.Barcode ?? "";
        PriceBox.Text   = model.Price.ToString("N2", CultureInfo.InvariantCulture);
        CostBox.Text    = model.Cost.ToString("N2", CultureInfo.InvariantCulture);
        StockBox.Text   = model.InitialStock.ToString("N2", CultureInfo.InvariantCulture);
        ActiveToggle.IsChecked = model.IsActive;

        // Categories
        CategoryBox.ItemsSource    = categories;
        CategoryBox.SelectedValue  = model.CategoryId;

        // Image
        UpdateImagePreview(model.ImagePath);
    }

    private void ApplyLocalization()
    {
        Locale.ApplyFlowDirection(this);
        DialogSubtitleTb.Text  = Locale.Get("Product_Subtitle");
        SectionBasicTb.Text    = Locale.Get("Product_SectionBasicInfo");
        SectionPricingTb.Text  = Locale.Get("Product_SectionPricing");
        SectionInventoryTb.Text = Locale.Get("Product_SectionInventory");
        SectionStatusTb.Text   = Locale.Get("Product_SectionStatus");
        PeNameLbl.Text     = Locale.Get("Product_NameLabel");
        PeBarcodeLbl.Text  = Locale.Get("Product_BarcodeLabel");
        PeCategoryLbl.Text = Locale.Get("Product_CategoryLabel");
        PePriceLbl.Text    = Locale.Get("Product_SellingPrice");
        PeCostLbl.Text     = Locale.Get("Product_CostPrice");
        PeStockLbl.Text    = _isNew ? Locale.Get("Product_InitialStock") : Locale.Get("Product_CurrentStock");
        PeImageLbl.Text    = Locale.Get("Product_ImageLabel");
        PeNoImageTb.Text   = Locale.Get("Product_NoImage");
        PeBrowseBtn.Content = Locale.Get("Product_Browse");
        PeClearBtn.Content  = Locale.Get("Product_Clear");
        PeActiveLbl.Text    = Locale.Get("Product_ActiveLabel");
        PeActiveHelpTb.Text = Locale.Get("Product_ActiveHelp");
        PeCancelBtn.Content = Locale.Get("Product_Cancel");
        PeSaveBtn.Content   = Locale.Get("Product_Save");
    }

    public ProductEditDto? Result { get; private set; }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(NameBox.Text))
        {
            MessageBox.Show(Locale.Get("Product_NameRequired"), Locale.Get("App_TitleShort"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (CategoryBox.SelectedValue is not Guid catId)
        {
            MessageBox.Show(Locale.Get("Product_SelectCategory"), Locale.Get("App_TitleShort"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!decimal.TryParse(PriceBox.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out var price) || price < 0)
        {
            MessageBox.Show(Locale.Get("Product_ValidPrice"), Locale.Get("App_TitleShort"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        decimal.TryParse(CostBox.Text,  NumberStyles.Any, CultureInfo.InvariantCulture, out var cost);
        decimal.TryParse(StockBox.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out var stock);

        Result = new ProductEditDto
        {
            Id           = _model.Id,
            Name         = NameBox.Text.Trim(),
            Barcode      = string.IsNullOrWhiteSpace(BarcodeBox.Text) ? null : BarcodeBox.Text.Trim(),
            CategoryId   = catId,
            Price        = price,
            Cost         = cost,
            InitialStock = stock < 0 ? 0 : stock,
            ImagePath    = _model.ImagePath,
            IsActive     = ActiveToggle.IsChecked == true
        };
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) =>
        DialogResult = false;

    private void BrowseImage_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title  = Locale.Get("Product_ImageDialogTitle"),
            Filter = "Image files|*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.webp|All files|*.*"
        };
        if (dlg.ShowDialog() != true) return;

        _model.ImagePath = dlg.FileName;
        UpdateImagePreview(dlg.FileName);
    }

    private void ClearImage_Click(object sender, RoutedEventArgs e)
    {
        _model.ImagePath = null;
        UpdateImagePreview(null);
    }

    private void UpdateImagePreview(string? path)
    {
        var hasImage = !string.IsNullOrWhiteSpace(path) && File.Exists(path);
        ProductImagePreview.Source = hasImage
            ? (BitmapImage?)_imgConverter.Convert(path, typeof(BitmapImage), null, CultureInfo.CurrentCulture)
            : null;
        NoImagePlaceholder.Visibility = hasImage ? Visibility.Collapsed : Visibility.Visible;
    }
}
