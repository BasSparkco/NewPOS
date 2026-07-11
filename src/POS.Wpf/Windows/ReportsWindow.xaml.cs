using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using POS.Core.Enums;
using POS.Infrastructure.Data;
using POS.Wpf.Localization;

namespace POS.Wpf.Windows;

public partial class ReportsWindow : Window
{
    private readonly IServiceScopeFactory _scopeFactory;

    public ReportsWindow(IServiceScopeFactory scopeFactory)
    {
        InitializeComponent();
        _scopeFactory = scopeFactory;
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        ApplyLocalization();
        ReportDate.SelectedDate = DateTime.Today;
        await LoadReportAsync(DateTime.Today);
    }

    private void ApplyLocalization()
    {
        Locale.ApplyFlowDirection(this);
        Title                         = Locale.Get("Reports_Title");
        ReportsHeaderTb.Text          = Locale.Get("Reports_Header");
        ReportsDateLabelTb.Text       = Locale.Get("Reports_Date");
        ReportsRefreshBtn.Content     = Locale.Get("Reports_Refresh");
        ReportsLblRevenue.Text        = Locale.Get("Reports_Revenue");
        ReportsLblInvoicesCard.Text   = Locale.Get("Reports_Invoices");
        ReportsLblItemsSold.Text      = Locale.Get("Reports_ItemsSold");
        ReportsLblAvgSale.Text        = Locale.Get("Reports_AvgSale");
        ReportsLblTopProducts.Text    = Locale.Get("Reports_TopProducts");
        ReportsLblInvoicesSection.Text = Locale.Get("Reports_InvoicesSection");
        ReportsCloseBtn.Content       = Locale.Get("Reports_Close");
    }

    private async void ReportDate_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (ReportDate.SelectedDate is DateTime d)
            await LoadReportAsync(d);
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (ReportDate.SelectedDate is DateTime d)
            await LoadReportAsync(d);
    }

    private async Task LoadReportAsync(DateTime date)
    {
        var dayStart = date.Date.ToUniversalTime();
        var dayEnd   = dayStart.AddDays(1);

        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PosDbContext>();

        // Paid invoices for the selected day
        var invoices = await db.Invoices
            .AsNoTracking()
            .Where(i => i.Status == InvoiceStatus.Paid
                        && !i.IsDeleted
                        && i.UpdatedAt >= dayStart
                        && i.UpdatedAt <  dayEnd)
            .OrderBy(i => i.UpdatedAt)
            .Select(i => new { i.Id, i.TotalAmount, i.UpdatedAt })
            .ToListAsync();

        var invoiceIds = invoices.Select(i => i.Id).ToList();

        // Line items for those invoices (for top-products calc)
        var lines = await db.InvoiceItems
            .AsNoTracking()
            .Where(l => invoiceIds.Contains(l.InvoiceId) && !l.IsDeleted)
            .Join(db.Products.AsNoTracking(),
                l => l.ProductId,
                p => p.Id,
                (l, p) => new { p.Name, l.Quantity, l.LineTotal })
            .ToListAsync();

        // Summary metrics
        decimal revenue   = invoices.Sum(i => i.TotalAmount);
        int     count     = invoices.Count;
        decimal itemsSold = lines.Sum(l => l.Quantity);
        decimal avg       = count > 0 ? revenue / count : 0m;

        CardRevenue.Text  = revenue.ToString("N2");
        CardInvoices.Text = count.ToString();
        CardItems.Text    = itemsSold.ToString("N0");
        CardAvg.Text      = avg.ToString("N2");

        // Top products
        var topProducts = lines
            .GroupBy(l => l.Name)
            .Select((g, _) => new ReportProductRow
            {
                Name    = g.Key,
                QtySold = g.Sum(x => x.Quantity),
                Revenue = g.Sum(x => x.LineTotal)
            })
            .OrderByDescending(p => p.Revenue)
            .Take(20)
            .Select((p, i) => { p.Rank = i + 1; return p; })
            .ToList();

        TopProductsList.ItemsSource = topProducts;

        // Invoice rows
        InvoicesList.ItemsSource = invoices.Select(i => new ReportInvoiceRow
        {
            InvoiceNumber = i.Id.ToString("N")[..12].ToUpperInvariant(),
            Time          = i.UpdatedAt.ToLocalTime().ToString("HH:mm"),
            Total         = i.TotalAmount
        }).ToList();

        var dStr = date.ToString("D", CultureInfo.CurrentUICulture);
        var tStr = DateTime.Now.ToString("t", CultureInfo.CurrentUICulture);
        FooterText.Text = string.Format(CultureInfo.CurrentUICulture, Locale.Get("Reports_FooterFormat"), dStr, tStr);
    }

    private void Close_Click(object sender, RoutedEventArgs e) =>
        Close();
}

internal sealed class ReportProductRow
{
    public int     Rank    { get; set; }
    public string  Name    { get; set; } = "";
    public decimal QtySold { get; set; }
    public decimal Revenue { get; set; }
}

internal sealed class ReportInvoiceRow
{
    public string  InvoiceNumber { get; set; } = "";
    public string  Time         { get; set; } = "";
    public decimal Total        { get; set; }
}
