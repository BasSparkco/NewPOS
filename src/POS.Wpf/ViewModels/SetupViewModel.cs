using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using POS.Application.Abstractions;
using POS.Infrastructure.Data;
using POS.Wpf.Localization;

namespace POS.Wpf.ViewModels;

/// <summary>
/// Shown once, only when the local database is fresh (no local accounts yet) — lets the operator
/// either start a brand-new independent business (today's existing behavior) or join a business that
/// already exists on a remote server (Stage 4T/T3). See tenant.md §7 T3.
/// </summary>
public partial class SetupViewModel : ObservableObject
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _configuration;

    public SetupViewModel(IServiceScopeFactory scopeFactory, IConfiguration configuration)
    {
        _scopeFactory = scopeFactory;
        _configuration = configuration;
        ApiUrl = configuration["Sync:ApiBaseUrl"] ?? string.Empty;
    }

    public Window? Owner { get; set; }

    /// <summary>Read by App.xaml.cs after the dialog closes successfully to decide whether to skip LoginWindow.</summary>
    public bool JoinedExistingBusiness { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowChoicePanel))]
    private bool _showJoinForm;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotBusy))]
    private bool _isBusy;

    [ObservableProperty] private string? _statusText;

    public bool ShowChoicePanel => !ShowJoinForm;
    public bool IsNotBusy => !IsBusy;
    [ObservableProperty] private string _apiUrl;
    [ObservableProperty] private string _businessSlug = "";
    [ObservableProperty] private string _username = "";
    [ObservableProperty] private string _password = "";

    [RelayCommand]
    private void ShowJoin()
    {
        StatusText = null;
        ShowJoinForm = true;
    }

    [RelayCommand]
    private void Back()
    {
        StatusText = null;
        ShowJoinForm = false;
    }

    [RelayCommand]
    private async Task StartFreshAsync()
    {
        IsBusy = true;
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<PosDbContext>>();
            await using var db = await dbFactory.CreateDbContextAsync();
            DatabaseSeeder.SeedIfNeeded(db);

            JoinedExistingBusiness = false;
            if (Owner is not null)
                Owner.DialogResult = true;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task JoinAsync()
    {
        if (string.IsNullOrWhiteSpace(ApiUrl) || string.IsNullOrWhiteSpace(BusinessSlug)
            || string.IsNullOrWhiteSpace(Username) || string.IsNullOrWhiteSpace(Password))
        {
            StatusText = Locale.Get("Setup_Join_MissingFields");
            return;
        }

        IsBusy = true;
        StatusText = Locale.Get("Setup_Join_InProgress");
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var join = scope.ServiceProvider.GetRequiredService<IBusinessJoinService>();
            var (success, error) = await join.JoinExistingBusinessAsync(ApiUrl, BusinessSlug, Username, Password);

            if (!success)
            {
                StatusText = error;
                return;
            }

            PersistSyncConfigurationForFutureRuns();

            JoinedExistingBusiness = true;
            if (Owner is not null)
                Owner.DialogResult = true;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// BusinessJoinService already updated the live in-memory configuration (so this run's background
    /// sync worker works immediately) — this makes the choice stick for future launches too. Best
    /// effort: a write failure here doesn't undo the join that already succeeded.
    /// </summary>
    private void PersistSyncConfigurationForFutureRuns()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "appsettings.local.json");
            var existing = File.Exists(path) ? File.ReadAllText(path) : "{}";
            var root = JsonNode.Parse(existing) as JsonObject ?? new JsonObject();

            if (root["Sync"] is not JsonObject sync)
            {
                sync = new JsonObject();
                root["Sync"] = sync;
            }

            sync["Enabled"] = true;
            sync["ApiBaseUrl"] = ApiUrl;

            File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Best-effort — the join itself already succeeded for this run.
        }
    }
}
