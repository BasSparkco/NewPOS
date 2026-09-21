using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using POS.Core.Enums;
using POS.Infrastructure.Data;
using POS.Wpf.Localization;

namespace POS.Wpf.Windows;

public partial class RefundWindow : Window
{
    private readonly IServiceScopeFactory _scopeFactory;

    public Guid? SelectedInvoiceId { get; private set; }

    public RefundWindow(IServiceScopeFactory scopeFactory)
    {
        InitializeComponent();
        _scopeFactory = scopeFactory;
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        ApplyLocalization();
        await LoadRecentInvoicesAsync();
    }

    private void ApplyLocalization()
    {
        Locale.ApplyFlowDirection(this);
        Title                    = Locale.Get("Refund_Title");
        RefundTitleText.Text     = Locale.Get("Refund_Header");
        RefundSubtitleText.Text  = Locale.Get("Refund_Subtitle");
        RefundWarningText.Text   = Locale.Get("Refund_Warning");
        RefundListEmptyText.Text = Locale.Get("Refund_NoInvoices");
        RefundCancelBtn.Content  = Locale.Get("Refund_Cancel");
        RefundButton.Content     = Locale.Get("Refund_Process");
    }

    private async Task LoadRecentInvoicesAsync()
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PosDbContext>();

        var invoices = await db.Invoices
            .AsNoTracking()
            .Where(i => i.Status == InvoiceStatus.Paid && !i.IsDeleted)
            .OrderByDescending(i => i.UpdatedAt)
            .Take(50)
            .Select(i => new RefundInvoiceRow
            {
                InvoiceId     = i.Id,
                InvoiceNumber = i.Id.ToString("N").Substring(0, 12).ToUpperInvariant(),
                PaidAt        = i.UpdatedAt.ToLocalTime().ToString("yyyy-MM-dd  HH:mm"),
                Total         = i.TotalAmount
            })
            .ToListAsync();

        InvoiceList.ItemsSource = invoices;
        RefundListEmptyText.Visibility = invoices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void InvoiceList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        RefundButton.IsEnabled = InvoiceList.SelectedItem is RefundInvoiceRow;
    }

    private void Refund_Click(object sender, RoutedEventArgs e)
    {
        if (InvoiceList.SelectedItem is not RefundInvoiceRow row) return;

        var body = string.Format(CultureInfo.CurrentUICulture, Locale.Get("Refund_ConfirmBody"),
            row.InvoiceNumber, Locale.ToDisplayDigits(row.Total.ToString("N2", CultureInfo.InvariantCulture)), row.PaidAt);
        var confirm = MessageBox.Show(body, Locale.Get("Refund_ConfirmTitle"), MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (confirm != MessageBoxResult.Yes) return;

        SelectedInvoiceId = row.InvoiceId;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) =>
        DialogResult = false;
}

internal sealed class RefundInvoiceRow
{
    public Guid   InvoiceId     { get; set; }
    public string InvoiceNumber { get; set; } = "";
    public string PaidAt        { get; set; } = "";
    public decimal Total        { get; set; }
}
