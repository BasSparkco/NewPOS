using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using POS.Application.Abstractions;
using POS.Application.Models;
using POS.Wpf.Localization;

namespace POS.Wpf.Windows;

public partial class PriceCheckWindow : Window
{
    private readonly IServiceScopeFactory _scopeFactory;
    private CancellationTokenSource _cts = new();

    /// <summary>
    /// The product the cashier selected. Non-null only when DialogResult == true.
    /// </summary>
    public ProductListItemDto? SelectedProduct { get; private set; }

    public PriceCheckWindow(IServiceScopeFactory scopeFactory)
    {
        InitializeComponent();
        _scopeFactory = scopeFactory;
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        Locale.ApplyFlowDirection(this);
        Title                      = Locale.Get("PriceCheck_Title");
        PriceCheckHeaderTb.Text    = Locale.Get("PriceCheck_Header");
        PriceCheckPlaceholderTb.Text = Locale.Get("PriceCheck_Placeholder");
        PriceCheckNoResultsTb.Text  = Locale.Get("PriceCheck_NoResults");
        PriceCheckCloseBtn.Content   = Locale.Get("PriceCheck_Close");
        AddButton.Content            = Locale.Get("PriceCheck_AddToInvoice");
        LowStockBadgeText.Text       = Locale.Get("LowStock_Badge");
        SearchBox.Focus();
    }

    // ── Search ───────────────────────────────────────────────────────────────

    private async void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        // Cancel any pending search
        _cts.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        var query = SearchBox.Text.Trim();
        if (string.IsNullOrEmpty(query))
        {
            ShowState(State.Placeholder);
            return;
        }

        try
        {
            await Task.Delay(180, ct); // debounce

            await using var scope = _scopeFactory.CreateAsyncScope();
            var catalog = scope.ServiceProvider.GetRequiredService<IProductCatalogService>();
            var results = await catalog.SearchProductsAsync(query, null, ct);

            if (ct.IsCancellationRequested) return;

            if (results.Count == 0)
            {
                ShowState(State.NoResults);
            }
            else
            {
                ResultsList.ItemsSource = results;
                ResultsList.SelectedIndex = 0;
                ShowState(State.Results);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            ShowState(State.NoResults);
            System.Diagnostics.Debug.WriteLine($"PriceCheck search error: {ex.Message}");
        }
    }

    private void ResultsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ResultsList.SelectedItem is ProductListItemDto p)
            ShowDetail(p);
        else
            HideDetail();
    }

    // ── State helpers ────────────────────────────────────────────────────────

    private enum State { Placeholder, NoResults, Results }

    private void ShowState(State state)
    {
        PlaceholderPanel.Visibility = state == State.Placeholder ? Visibility.Visible : Visibility.Collapsed;
        NoResultsPanel.Visibility   = state == State.NoResults   ? Visibility.Visible : Visibility.Collapsed;
        ResultsPanel.Visibility     = state == State.Results     ? Visibility.Visible : Visibility.Collapsed;
        if (state != State.Results) HideDetail();
    }

    private void ShowDetail(ProductListItemDto p)
    {
        SelectedProduct          = p;
        DetailName.Text          = p.Name;
        DetailPrice.Text         = Locale.ToDisplayDigits(p.Price.ToString("N2", CultureInfo.InvariantCulture));
        DetailStock.Text = string.Format(CultureInfo.CurrentUICulture, Locale.Get("PriceCheck_StockFormat"),
            Locale.ToDisplayDigits(p.QuantityOnHand.ToString("N0", CultureInfo.InvariantCulture)));
        LowStockBadge.Visibility = p.IsLowStock ? Visibility.Visible : Visibility.Collapsed;
        DetailBarcode.Text = string.IsNullOrEmpty(p.Barcode)
            ? ""
            : string.Format(CultureInfo.CurrentUICulture, Locale.Get("Barcode_Format"), p.Barcode);
        DetailBarcode.Visibility = string.IsNullOrEmpty(p.Barcode) ? Visibility.Collapsed : Visibility.Visible;
        DetailCard.Visibility    = Visibility.Visible;
        AddButton.IsEnabled      = true;
    }

    private void HideDetail()
    {
        SelectedProduct       = null;
        DetailCard.Visibility = Visibility.Collapsed;
        AddButton.IsEnabled   = false;
    }

    // ── Button handlers ──────────────────────────────────────────────────────

    private void AddToInvoice_Click(object sender, RoutedEventArgs e) =>
        DialogResult = true;

    private void Close_Click(object sender, RoutedEventArgs e) =>
        DialogResult = false;
}
