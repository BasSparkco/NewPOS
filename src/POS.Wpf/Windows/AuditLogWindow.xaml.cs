using System.Globalization;
using System.Windows;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using POS.Application.Abstractions;
using POS.Infrastructure.Data;
using POS.Wpf.Localization;

namespace POS.Wpf.Windows;

public partial class AuditLogWindow : Window
{
    private readonly IServiceScopeFactory _scopeFactory;

    public AuditLogWindow(IServiceScopeFactory scopeFactory)
    {
        InitializeComponent();
        _scopeFactory = scopeFactory;
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        ApplyLocalization();
        AuditFromDate.SelectedDate = DateTime.Today.AddDays(-6);
        AuditToDate.SelectedDate = DateTime.Today;
        await LoadFilterOptionsAsync();
        await LoadAuditLogsAsync();
    }

    private void ApplyLocalization()
    {
        Locale.ApplyFlowDirection(this);
        Title = Locale.Get("Audit_Title");
        AuditHeaderTb.Text = Locale.Get("Audit_Header");
        AuditSubtitleTb.Text = Locale.Get("Audit_Subtitle");
        AuditFromLbl.Text = Locale.Get("Audit_FromDate");
        AuditToLbl.Text = Locale.Get("Audit_ToDate");
        AuditActionLbl.Text = Locale.Get("Audit_Action");
        AuditEntityLbl.Text = Locale.Get("Audit_Entity");
        AuditRefreshBtn.Content = Locale.Get("Audit_Refresh");
        AuditCloseBtn.Content = Locale.Get("Audit_Close");
        AuditEmptyText.Text = Locale.Get("Audit_NoRows");

        AuditWhenCol.Header = Locale.Get("Audit_ColWhen");
        AuditActorCol.Header = Locale.Get("Audit_ColActor");
        AuditActionCol.Header = Locale.Get("Audit_ColAction");
        AuditEntityCol.Header = Locale.Get("Audit_ColEntity");
        AuditDetailsCol.Header = Locale.Get("Audit_ColDetails");
    }

    private async Task LoadFilterOptionsAsync()
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PosDbContext>();
        var session = scope.ServiceProvider.GetRequiredService<ICurrentSession>();

        var actionValues = await db.AuditLogs
            .AsNoTracking()
            .Where(x => x.StoreId == session.StoreId && !x.IsDeleted)
            .Select(x => x.Action)
            .Distinct()
            .OrderBy(x => x)
            .ToListAsync();

        var entityValues = await db.AuditLogs
            .AsNoTracking()
            .Where(x => x.StoreId == session.StoreId && !x.IsDeleted)
            .Select(x => x.EntityName)
            .Distinct()
            .OrderBy(x => x)
            .ToListAsync();

        AuditActionFilter.ItemsSource = new[]
                { new AuditFilterOption(Locale.Get("Audit_AllActions"), null) }
                .Concat(actionValues.Select(x => new AuditFilterOption(x, x)))
                .ToList();
        AuditActionFilter.SelectedIndex = 0;

        AuditEntityFilter.ItemsSource = new[]
                { new AuditFilterOption(Locale.Get("Audit_AllEntities"), null) }
                .Concat(entityValues.Select(x => new AuditFilterOption(x, x)))
                .ToList();
        AuditEntityFilter.SelectedIndex = 0;
    }

    private async Task LoadAuditLogsAsync()
    {
        var fromDate = AuditFromDate.SelectedDate;
        var toDate = AuditToDate.SelectedDate;

        if (fromDate.HasValue && toDate.HasValue && fromDate.Value.Date > toDate.Value.Date)
        {
            MessageBox.Show(
                Locale.Get("Audit_InvalidDateRange"),
                Locale.Get("App_TitleShort"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        var fromUtc = fromDate.HasValue
            ? DateTime.SpecifyKind(fromDate.Value.Date, DateTimeKind.Local).ToUniversalTime()
            : (DateTime?)null;
        var toExclusiveUtc = toDate.HasValue
            ? DateTime.SpecifyKind(toDate.Value.Date.AddDays(1), DateTimeKind.Local).ToUniversalTime()
            : (DateTime?)null;
        var action = AuditActionFilter.SelectedValue as string;
        var entity = AuditEntityFilter.SelectedValue as string;

        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PosDbContext>();
        var session = scope.ServiceProvider.GetRequiredService<ICurrentSession>();

        var query = db.AuditLogs
            .AsNoTracking()
            .Where(x => x.StoreId == session.StoreId && !x.IsDeleted);

        if (fromUtc is not null)
            query = query.Where(x => x.CreatedAt >= fromUtc.Value);

        if (toExclusiveUtc is not null)
            query = query.Where(x => x.CreatedAt < toExclusiveUtc.Value);

        if (!string.IsNullOrWhiteSpace(action))
            query = query.Where(x => x.Action == action);

        if (!string.IsNullOrWhiteSpace(entity))
            query = query.Where(x => x.EntityName == entity);

        var items = await query
            .OrderByDescending(x => x.CreatedAt)
            .Take(500)
            .GroupJoin(
                db.Users.AsNoTracking(),
                log => log.UserId,
                user => (Guid?)user.Id,
                (log, users) => new
                {
                    log.CreatedAt,
                    log.Action,
                    log.EntityName,
                    log.EntityId,
                    log.Details,
                    Username = users.Select(u => u.Username).FirstOrDefault()
                })
            .ToListAsync();

        var rows = items
            .Select(x => new AuditLogRow
            {
                WhenLocal = x.CreatedAt.ToLocalTime(),
                Actor = string.IsNullOrWhiteSpace(x.Username) ? "-" : x.Username,
                Action = x.Action,
                Entity = x.EntityId.HasValue
                    ? $"{x.EntityName} / {x.EntityId.Value.ToString("N")[..8].ToUpperInvariant()}"
                    : x.EntityName,
                Details = x.Details ?? string.Empty
            })
            .ToList();

        AuditGrid.ItemsSource = rows;
        AuditEmptyText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        AuditCountText.Text = string.Format(CultureInfo.CurrentUICulture, Locale.Get("Audit_CountFormat"), rows.Count);
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) =>
        await LoadAuditLogsAsync();

    private void Close_Click(object sender, RoutedEventArgs e) =>
        Close();
}

internal sealed record AuditFilterOption(string Label, string? Value);

internal sealed class AuditLogRow
{
    public DateTime WhenLocal { get; set; }
    public string Actor { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public string Entity { get; set; } = string.Empty;
    public string Details { get; set; } = string.Empty;
}