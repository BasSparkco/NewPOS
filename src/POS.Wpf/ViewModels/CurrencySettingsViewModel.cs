using System.Globalization;
using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using POS.Application.Abstractions;
using POS.Application.Models;
using POS.Wpf.Localization;

namespace POS.Wpf.ViewModels;

public partial class CurrencySettingsViewModel : ObservableObject
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ICurrentSession _session;
    private bool _isLoading;
    private CurrencyRateItem? _selectedBaseCurrency;

    public CurrencySettingsViewModel(IServiceScopeFactory scopeFactory, ICurrentSession session)
    {
        _scopeFactory = scopeFactory;
        _session = session;
    }

    [ObservableProperty] private ObservableCollection<CurrencyRateItem> _currencies = new();
    [ObservableProperty] private string _storeName = string.Empty;
    [ObservableProperty] private string _statusText = string.Empty;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _allowNegativeStock;
    [ObservableProperty] private string _lowStockThresholdText = "5";
    [ObservableProperty] private string _defaultTaxPercentText = "0";
    [ObservableProperty] private bool _pricesIncludeVat;
    [ObservableProperty] private string _receiptFooterText = string.Empty;
    [ObservableProperty] private bool _useArabicIndicDigits;

    public CurrencyRateItem? SelectedBaseCurrency
    {
        get => _selectedBaseCurrency;
        set
        {
            var previous = _selectedBaseCurrency;
            if (!SetProperty(ref _selectedBaseCurrency, value) || _isLoading || previous is null || value is null || previous.Id == value.Id)
                return;

            NormalizeRatesForNewBase(value.ExchangeRate);
        }
    }

    public async Task LoadAsync()
    {
        _isLoading = true;
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var currencyService = scope.ServiceProvider.GetRequiredService<ICurrencyService>();
            var settingsService = scope.ServiceProvider.GetRequiredService<ISettingsService>();
            var policy = await currencyService.GetStoreCurrencyPolicyAsync();
            var settings = await settingsService.GetStoreSettingsAsync();

            StoreName = policy.StoreName;
            Currencies = new ObservableCollection<CurrencyRateItem>(policy.Currencies.Select(c => new CurrencyRateItem
            {
                Id = c.Id,
                Code = c.Code,
                Name = c.Name,
                Symbol = c.Symbol,
                ExchangeRate = c.ExchangeRate
            }));
            SelectedBaseCurrency = Currencies.FirstOrDefault(c => c.Id == policy.BaseCurrencyId);
            AllowNegativeStock = settings.AllowNegativeStock;
            LowStockThresholdText = settings.LowStockThreshold.ToString("0.##", CultureInfo.CurrentCulture);
            DefaultTaxPercentText = settings.DefaultTaxPercent.ToString("0.##", CultureInfo.CurrentCulture);
            PricesIncludeVat = settings.PricesIncludeVat;
            ReceiptFooterText = settings.ReceiptFooterText ?? string.Empty;
            UseArabicIndicDigits = settings.UseArabicIndicDigits;
            StatusText = Locale.Get("CurrencySettings_Loaded");
        }
        finally
        {
            _isLoading = false;
        }
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (SelectedBaseCurrency is null)
        {
            MessageBox.Show(Locale.Get("CurrencySettings_SelectBase"), Locale.Get("App_TitleShort"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (Currencies.Any(c => c.ExchangeRate <= 0))
        {
            MessageBox.Show(Locale.Get("CurrencySettings_ValidRate"), Locale.Get("App_TitleShort"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!TryParseDecimalInput(LowStockThresholdText, out var lowStockThreshold) || lowStockThreshold < 0)
        {
            MessageBox.Show(Locale.Get("CurrencySettings_ValidLowStockThreshold"), Locale.Get("App_TitleShort"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!TryParseDecimalInput(DefaultTaxPercentText, out var defaultTaxPercent) || defaultTaxPercent < 0 || defaultTaxPercent > 100)
        {
            MessageBox.Show(Locale.Get("CurrencySettings_ValidDefaultTax"), Locale.Get("App_TitleShort"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        IsBusy = true;
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var currencyService = scope.ServiceProvider.GetRequiredService<ICurrencyService>();
            var settingsService = scope.ServiceProvider.GetRequiredService<ISettingsService>();
            var rates = Currencies.Select(c => new CurrencyRateUpdateDto(c.Id, c.ExchangeRate)).ToList();
            var settings = new StoreSettingsDto(
                AllowNegativeStock,
                lowStockThreshold,
                defaultTaxPercent,
                string.IsNullOrWhiteSpace(ReceiptFooterText) ? null : ReceiptFooterText.Trim(),
                UseArabicIndicDigits,
                PricesIncludeVat);

            await settingsService.UpdateStoreSettingsAsync(settings);
            await currencyService.UpdateStoreCurrencyPolicyAsync(SelectedBaseCurrency.Id, rates);

            _session.Set(
                _session.TenantId,
                _session.UserId,
                _session.StoreId,
                _session.Username,
                _session.RoleName,
                _session.PermissionsMask,
                SelectedBaseCurrency.Code,
                SelectedBaseCurrency.Symbol);

            StatusText = Locale.Get("CurrencySettings_Saved");
            if (System.Windows.Application.Current.Windows.OfType<Window>().FirstOrDefault(w => ReferenceEquals(w.DataContext, this)) is { } window)
                window.DialogResult = true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, Locale.Get("App_TitleShort"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void NormalizeRatesForNewBase(decimal newBaseOldRate)
    {
        if (newBaseOldRate <= 0)
            return;

        foreach (var currency in Currencies)
            currency.ExchangeRate = Math.Round(currency.ExchangeRate / newBaseOldRate, 6, MidpointRounding.AwayFromZero);

        if (SelectedBaseCurrency is not null)
            SelectedBaseCurrency.ExchangeRate = 1m;

        StatusText = Locale.Get("CurrencySettings_Rebased");
    }

    private static bool TryParseDecimalInput(string? input, out decimal value)
    {
        input = string.IsNullOrWhiteSpace(input) ? "0" : input.Trim();

        return decimal.TryParse(input, NumberStyles.Number, CultureInfo.CurrentCulture, out value)
            || decimal.TryParse(input, NumberStyles.Number, CultureInfo.InvariantCulture, out value);
    }
}