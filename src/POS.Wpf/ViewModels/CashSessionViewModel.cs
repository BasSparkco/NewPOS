using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using POS.Application.Abstractions;
using POS.Application.Models;
using POS.Core.Enums;
using POS.Infrastructure.Data;
using POS.Wpf.Localization;

namespace POS.Wpf.ViewModels;

/// <summary>
/// Drives the Cash Session window: open/close this machine's register drawer, record manual cash
/// in/out, and review the running ledger. Deliberately independent of login state — opening/closing a
/// session is always an explicit action here, never an implicit side effect of signing in or out.
/// </summary>
public partial class CashSessionViewModel : ObservableObject
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ICurrentDevice _currentDevice;
    private readonly ICurrentSession _session;

    public CashSessionViewModel(IServiceScopeFactory scopeFactory, ICurrentDevice currentDevice, ICurrentSession session)
    {
        _scopeFactory = scopeFactory;
        _currentDevice = currentDevice;
        _session = session;
    }

    [ObservableProperty] private Guid? _registerId;
    [ObservableProperty] private string _registerLabel = string.Empty;
    [ObservableProperty] private CashSessionDto? _activeSession;
    [ObservableProperty] private CashSessionSummaryDto? _summary;
    [ObservableProperty] private ObservableCollection<CashMovementDto> _movements = new();
    [ObservableProperty] private string _openingAmountText = "0";
    [ObservableProperty] private string _closingCountedAmountText = "0";
    [ObservableProperty] private string _closingNotes = string.Empty;
    [ObservableProperty] private string _manualAmountText = "0";
    [ObservableProperty] private string _manualNotes = string.Empty;
    [ObservableProperty] private string _approverUsername = string.Empty;
    [ObservableProperty] private string _statusText = string.Empty;
    [ObservableProperty] private bool _isBusy;

    public bool HasOpenSession => ActiveSession is not null;
    public bool HasRegister => RegisterId.HasValue;
    public bool CanOpenSession => RegisterId.HasValue && ActiveSession is null;

    public string SummaryTotalsText
    {
        get
        {
            if (Summary is null)
                return string.Empty;

            var currency = Summary.Session.CurrencyCode;
            return string.Format(
                Locale.Get("CashSession_SummaryTotalsFormat"),
                Summary.Session.OpeningCashAmount, currency,
                Summary.TotalCashReceipts, currency,
                Summary.TotalCashRefunds, currency,
                Summary.TotalCashIn, currency,
                Summary.TotalCashOut, currency,
                Summary.ExpectedCashAmount, currency);
        }
    }

    private void RaiseComputedPropertiesChanged()
    {
        OnPropertyChanged(nameof(HasOpenSession));
        OnPropertyChanged(nameof(HasRegister));
        OnPropertyChanged(nameof(CanOpenSession));
        OnPropertyChanged(nameof(SummaryTotalsText));
    }

    public async Task LoadAsync()
    {
        IsBusy = true;
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<PosDbContext>>();
            await using var db = await dbFactory.CreateDbContextAsync();

            var device = await db.Devices
                .AsNoTracking()
                .Where(d => d.StoreId == _session.StoreId && d.Name == _currentDevice.Name && !d.IsDeleted)
                .Select(d => new { d.RegisterId })
                .FirstOrDefaultAsync();

            if (device?.RegisterId is null)
            {
                RegisterId = null;
                RegisterLabel = Locale.Get("CashSession_NoRegister");
                ActiveSession = null;
                Summary = null;
                Movements = new ObservableCollection<CashMovementDto>();
                RaiseComputedPropertiesChanged();
                return;
            }

            RegisterId = device.RegisterId;
            var register = await db.Registers.AsNoTracking().Where(r => r.Id == device.RegisterId).Select(r => new { r.Number, r.Name }).FirstOrDefaultAsync();
            RegisterLabel = register is null ? string.Empty : $"#{register.Number} — {register.Name}";

            var cashSessions = scope.ServiceProvider.GetRequiredService<ICashSessionService>();
            ActiveSession = await cashSessions.GetActiveSessionAsync(RegisterId.Value);

            if (ActiveSession is not null)
            {
                Summary = await cashSessions.GetSummaryAsync(ActiveSession.Id);
                Movements = new ObservableCollection<CashMovementDto>(Summary?.Movements ?? Array.Empty<CashMovementDto>());
            }
            else
            {
                Summary = null;
                Movements = new ObservableCollection<CashMovementDto>();
            }

            RaiseComputedPropertiesChanged();
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task OpenSessionAsync()
    {
        if (RegisterId is null)
        {
            MessageBox.Show(Locale.Get("CashSession_NoRegister"), Locale.Get("App_TitleShort"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!decimal.TryParse(OpeningAmountText, out var openingAmount) || openingAmount < 0)
        {
            MessageBox.Show(Locale.Get("CashSession_InvalidAmount"), Locale.Get("App_TitleShort"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        IsBusy = true;
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var cashSessions = scope.ServiceProvider.GetRequiredService<ICashSessionService>();
            var (success, error, _) = await cashSessions.OpenSessionAsync(RegisterId.Value, openingAmount);

            if (!success)
            {
                MessageBox.Show(error, Locale.Get("App_TitleShort"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            OpeningAmountText = "0";
            StatusText = Locale.Get("CashSession_Opened");
            await LoadAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task CloseSessionAsync()
    {
        if (ActiveSession is null)
            return;

        if (!decimal.TryParse(ClosingCountedAmountText, out var countedAmount) || countedAmount < 0)
        {
            MessageBox.Show(Locale.Get("CashSession_InvalidAmount"), Locale.Get("App_TitleShort"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (MessageBox.Show(
                Locale.Get("CashSession_ConfirmClose"),
                Locale.Get("App_TitleShort"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        IsBusy = true;
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var cashSessions = scope.ServiceProvider.GetRequiredService<ICashSessionService>();
            var (success, error, summary) = await cashSessions.CloseSessionAsync(
                ActiveSession.Id, countedAmount, string.IsNullOrWhiteSpace(ClosingNotes) ? null : ClosingNotes.Trim());

            if (!success)
            {
                MessageBox.Show(error, Locale.Get("App_TitleShort"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            ClosingCountedAmountText = "0";
            ClosingNotes = string.Empty;
            StatusText = string.Format(
                Locale.Get("CashSession_ClosedFormat"),
                summary!.Session.DiscrepancyAmount ?? 0m,
                summary.Session.CurrencyCode);
            await LoadAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task CashInAsync() => await RecordManualMovementAsync(CashMovementType.CashIn);

    [RelayCommand]
    private async Task CashOutAsync() => await RecordManualMovementAsync(CashMovementType.CashOut);

    private async Task RecordManualMovementAsync(CashMovementType type)
    {
        if (ActiveSession is null)
            return;

        if (!decimal.TryParse(ManualAmountText, out var amount) || amount <= 0)
        {
            MessageBox.Show(Locale.Get("CashSession_InvalidAmount"), Locale.Get("App_TitleShort"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        IsBusy = true;
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            Guid? approverUserId = null;

            var approverName = ApproverUsername.Trim();
            if (type == CashMovementType.CashOut && approverName.Length > 0)
            {
                var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<PosDbContext>>();
                await using var db = await dbFactory.CreateDbContextAsync();
                approverUserId = await db.Users
                    .AsNoTracking()
                    .Where(u => u.StoreId == _session.StoreId && u.Username == approverName && !u.IsDeleted)
                    .Select(u => (Guid?)u.Id)
                    .FirstOrDefaultAsync();

                if (approverUserId is null)
                {
                    MessageBox.Show(Locale.Get("CashSession_ApproverNotFound"), Locale.Get("App_TitleShort"), MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }

            var cashSessions = scope.ServiceProvider.GetRequiredService<ICashSessionService>();
            var (success, error) = await cashSessions.RecordManualMovementAsync(
                ActiveSession.Id, type, amount, string.IsNullOrWhiteSpace(ManualNotes) ? null : ManualNotes.Trim(), approverUserId);

            if (!success)
            {
                MessageBox.Show(error, Locale.Get("App_TitleShort"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            ManualAmountText = "0";
            ManualNotes = string.Empty;
            ApproverUsername = string.Empty;
            StatusText = type == CashMovementType.CashIn ? Locale.Get("CashSession_CashInRecorded") : Locale.Get("CashSession_CashOutRecorded");
            await LoadAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }
}
