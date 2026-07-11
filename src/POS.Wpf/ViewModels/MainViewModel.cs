using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using POS.Application.Abstractions;
using POS.Application.Models;
using POS.Wpf.Windows;

namespace POS.Wpf.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IServiceProvider     _services;
    private readonly IReceiptPrinter      _printer;
    private readonly ICurrentSession      _session;

    public MainViewModel(
        IServiceScopeFactory scopeFactory,
        IServiceProvider     services,
        IReceiptPrinter      printer,
        ICurrentSession      session)
    {
        _scopeFactory = scopeFactory;
        _services     = services;
        _printer      = printer;
        _session      = session;
    }

    // ── Tabs ────────────────────────────────────────────────────────────────
    [ObservableProperty] ObservableCollection<InvoiceTab> _invoiceTabs = new();
    [ObservableProperty] InvoiceTab? _activeTab;

    /// <summary>Derived from the active tab. Null only before the first sale is started.</summary>
    public Guid? CurrentInvoiceId => ActiveTab?.InvoiceId;

    /// <summary>Called whenever the user switches tabs (or a new tab becomes active).</summary>
    partial void OnActiveTabChanged(InvoiceTab? value)
    {
        foreach (var t in InvoiceTabs)
            t.IsActive = t == value;

        OnPropertyChanged(nameof(CurrentInvoiceId));
        OnPropertyChanged(nameof(InvoiceLabel));
        OnPropertyChanged(nameof(InvoiceStatusLabel));
        OnPropertyChanged(nameof(ActiveTabIsHeld));
        OnPropertyChanged(nameof(HoldResumeLabel));
        OnPropertyChanged(nameof(HeldTabCount));
        _ = RefreshCartAsync();
    }

    // ── Search / product panel ──────────────────────────────────────────────
    [ObservableProperty] string _searchQuery = "";
    [ObservableProperty] ObservableCollection<ProductListItemDto> _searchResults = new();
    [ObservableProperty] ObservableCollection<CategoryFilterItem> _categoryFilters = new();

    // ── Cart ────────────────────────────────────────────────────────────────
    [ObservableProperty] ObservableCollection<CartLineItem> _cartLines = new();
    [ObservableProperty] decimal _subtotal;
    [ObservableProperty] decimal _taxPercent;
    [ObservableProperty] decimal _taxAmount;
    [ObservableProperty] decimal _total;
    /// <summary>The cart line the bottom calculator currently types into; null means the calculator's own QTY/PRICE fields.</summary>
    [ObservableProperty] CartLineItem? _activeCalcLine;

    // ── Misc UI state ───────────────────────────────────────────────────────
    [ObservableProperty] string _statusText         = "";
    [ObservableProperty] string _qtyInput           = "1";
    [ObservableProperty] string _priceInput         = "0";
    [ObservableProperty] string _activeCalcField    = "Qty";  // "Qty" or "Price" — which display the numpad types into
    [ObservableProperty] bool   _showProductImages  = true;   // default ON in new design
    [ObservableProperty] bool   _customerDisplayOpen;
    [ObservableProperty] bool   _isDarkMode;
    [ObservableProperty] bool   _isFullscreen;
    [ObservableProperty] bool   _showNumpad;
    [ObservableProperty] string _selectedPage       = "Cashier";
    [ObservableProperty] string _uiLanguage         = "en";

    private CustomerDisplayWindow? _customerDisplay;

    partial void OnActiveCalcFieldChanged(string value)
    {
        OnPropertyChanged(nameof(IsQtyFieldActive));
        OnPropertyChanged(nameof(IsPriceFieldActive));
        SyncLineCalcHighlight();
    }

    partial void OnActiveCalcLineChanged(CartLineItem? value)
    {
        OnPropertyChanged(nameof(IsQtyFieldActive));
        OnPropertyChanged(nameof(IsPriceFieldActive));
        SyncLineCalcHighlight();
    }

    /// <summary>Pushes the current calculator target down onto each cart line so its Qty/Disc box can highlight itself.</summary>
    private void SyncLineCalcHighlight()
    {
        foreach (var line in CartLines)
        {
            line.IsQtyCalcActive  = ReferenceEquals(line, ActiveCalcLine) && ActiveCalcField == "Qty";
            line.IsDiscCalcActive = ReferenceEquals(line, ActiveCalcLine) && ActiveCalcField == "Disc";
        }
    }

    public bool IsQtyFieldActive   => ActiveCalcLine is null && ActiveCalcField != "Price";
    public bool IsPriceFieldActive => ActiveCalcLine is null && ActiveCalcField == "Price";

    public int    HeldTabCount    => InvoiceTabs.Count(t => t.IsHeld);
    public string DarkModeIcon    => IsDarkMode ? "☀" : "🌙";
    public string FullscreenIcon  => IsFullscreen ? "🗗" : "⛶";
    public string NumpadIcon      => ShowNumpad ? "⌨ Hide Numpad" : "⌨ Numpad";
    public FlowDirection UiFlowDirection => IsRtlLanguage(UiLanguage) ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
    public bool IsUiLanguageArabic => string.Equals(UiLanguage, "ar", StringComparison.OrdinalIgnoreCase);
    public bool IsUiLanguageEnglish => string.Equals(UiLanguage, "en", StringComparison.OrdinalIgnoreCase);
    public bool IsUiLanguageHebrew => string.Equals(UiLanguage, "he", StringComparison.OrdinalIgnoreCase);
    public string LanguageLabel => T("Language:", "اللغة:", "שפה:");
    public string NavHomeLabel => T("Home", "الرئيسية", "בית");
    public string NavCustomersLabel => T("Customers", "العملاء", "לקוחות");
    public string NavOrdersLabel => T("Orders", "الطلبات", "הזמנות");
    public string NavProductsLabel => T("Products", "المنتجات", "מוצרים");
    public string NavReportsLabel => T("Reports", "التقارير", "דוחות");
    public string NavAuditLabel => T("Audit", "التدقيق", "ביקורת");
    public string NavSettingsLabel => T("Settings", "الإعدادات", "הגדרות");
    public string NavLogoutLabel => T("Logout", "تسجيل الخروج", "התנתקות");
    public string OnlineLabel => T("Online", "متصل", "מחובר");
    public string HoldOrdersLabel => T("Hold Orders", "فواتير معلقة", "הזמנות מושהות");
    public string NewInvoiceLabel => T("+ New Invoice", "+ فاتورة جديدة", "+ חשבונית חדשה");
    public string ScanLabel => T("Scan", "مسح", "סריקה");
    public string LowStockLabel => T("Low", "منخفض", "נמוך");
    public string WalkInCustomerLabel => T("Walk-in Customer", "عميل مباشر", "לקוח מזדמן");
    public string EmptyCartLabel => T("Cart is empty", "السلة فارغة", "העגלה ריקה");
    public string QtyLabel => T("QTY", "الكمية", "כמות");
    public string EachLabel => T("each", "للوحدة", "ליחידה");
    public string AddNoteLabel => T("Add note to invoice", "إضافة ملاحظة للفاتورة", "הוסף הערה לחשבונית");
    public string RefundLabel => T("Refund", "استرجاع", "החזר");
    public string QtyWeightLabel => T("QTY / WEIGHT", "الكمية / الوزن", "כמות / משקל");
    public string PriceFieldLabel => T("PRICE", "السعر", "מחיר");
    public string AddToCartLabel => T("Add", "إضافة", "הוסף");
    public string AddCustomItemToolTip => T("Add custom-priced item to cart", "إضافة صنف بسعر مخصص إلى السلة", "הוסף פריט במחיר מותאם לעגלה");
    public string ClearCalcLabel => T("C", "C", "C");
    public string CustomItemDefaultName => T("Custom Item", "صنف مخصص", "פריט מותאם");
    public string SubtotalLabel => T("Subtotal", "المجموع الفرعي", "סכום ביניים");
    public string TaxLabelPrefix => T("VAT (", "ضريبة (", "מע\"מ (");
    public string TotalLabel => T("Total", "الإجمالي", "סה\"כ");
    public string ProceedLabel => T("Proceed", "متابعة", "המשך");
    public string HoldOrderLabel => T("Hold Order", "تعليق الطلب", "השהה הזמנה");
    public string ResumeLabel => T("Resume", "استئناف", "המשך");
    public string HoldResumeActionLabel => ActiveTabIsHeld ? ResumeLabel : HoldOrderLabel;
    public string HoldResumeToolTip => T("Hold / Resume (F3)", "تعليق / استئناف (F3)", "השהה / המשך (F3)");
    public string RefreshToolTip => T("Refresh (F5)", "تحديث (F5)", "רענון (F5)");
    public string CustomerDisplayToolTip => T("Customer Display", "شاشة العميل", "צג לקוח");
    public string PriceCheckToolTip => T("Price Check (F2)", "فحص السعر (F2)", "בדיקת מחיר (F2)");
    public string ScanToolTip => T("Price Check / Scan (F2)", "فحص السعر / مسح (F2)", "בדיקת מחיר / סריקה (F2)");
    public string ToggleImagesToolTip => T("Toggle Product Images", "إظهار/إخفاء صور المنتجات", "הצג/הסתר תמונות מוצרים");
    public string ToggleDarkModeToolTip => T("Toggle Dark Mode", "تبديل الوضع الداكن", "החלף מצב כהה");
    public string HoldOrdersToolTip => T("Hold / Resume current invoice (F3)", "تعليق / استئناف الفاتورة الحالية (F3)", "השהה / המשך חשבונית נוכחית (F3)");
    public string AppTitle => T("POS", "نقطة البيع", "קופה");

    /// <summary>Store base currency for display (symbol when available, else ISO code).</summary>
    public string CurrencySuffix =>
        string.IsNullOrWhiteSpace(_session.CurrencySymbol)
            ? _session.BaseCurrencyCode
            : _session.CurrencySymbol!;

    public string FormattedSubtotal => $"{Subtotal:N2} {CurrencySuffix}";
    public string FormattedTaxAmount => $"{TaxAmount:N2} {CurrencySuffix}";
    public string FormattedTotal => $"{Total:N2} {CurrencySuffix}";

    partial void OnSubtotalChanged(decimal value) =>
        OnPropertyChanged(nameof(FormattedSubtotal));

    partial void OnTaxAmountChanged(decimal value) =>
        OnPropertyChanged(nameof(FormattedTaxAmount));

    partial void OnTotalChanged(decimal value) =>
        OnPropertyChanged(nameof(FormattedTotal));

    // ── Computed props ──────────────────────────────────────────────────────
    public bool IsCartEmpty    => CartLines.Count == 0;
    public bool IsCartNotEmpty => CartLines.Count > 0;

    public bool   ActiveTabIsHeld  => ActiveTab?.IsHeld ?? false;
    public string HoldResumeLabel  => ActiveTabIsHeld ? "▶  Resume Invoice" : "⏸  Hold Invoice";

    public string InvoiceLabel =>
        ActiveTab is not null ? $"{T("Invoice", "فاتورة", "חשבונית")} {ActiveTab.Label}" : T("New Sale", "بيع جديد", "מכירה חדשה");

    public string InvoiceStatusLabel =>
        ActiveTab?.IsHeld == true
            ? $"{T("On Hold", "معلّق", "מושהה")} · {CartLines.Count} {ItemWord(CartLines.Count)}"
            : CartLines.Count == 0
                ? $"{T("Active", "نشط", "פעיל")} · 0 {ItemWord(0)}"
                : $"{T("Active", "نشط", "פעיל")} · {CartLines.Count} {ItemWord(CartLines.Count)}";

    public string UserName  => _session.Username;
    public string RoleName  => _session.RoleName;
    public bool   IsAdmin   => string.Equals(_session.RoleName, "Admin",   StringComparison.OrdinalIgnoreCase)
                            || string.Equals(_session.RoleName, "Manager", StringComparison.OrdinalIgnoreCase);
    public string UserBadge =>
        string.IsNullOrWhiteSpace(_session.Username)
            ? "?"
            : _session.Username.Trim()[0].ToString().ToUpperInvariant();

    partial void OnCartLinesChanged(ObservableCollection<CartLineItem> value)
    {
        OnPropertyChanged(nameof(IsCartEmpty));
        OnPropertyChanged(nameof(IsCartNotEmpty));
        OnPropertyChanged(nameof(InvoiceStatusLabel));
        if (ActiveTab is not null)
            ActiveTab.ItemCount = value.Count;
        OnPropertyChanged(nameof(HoldResumeActionLabel));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Startup
    // ═══════════════════════════════════════════════════════════════════════

    public async Task OnLoadedAsync()
    {
        ApplyUiLanguage(UiLanguage);
        await NewSaleAsync();          // creates the first tab
        await LoadCategoriesAsync();
        await SearchAsync();
    }

    partial void OnUiLanguageChanged(string value)
    {
        var normalized = NormalizeLanguage(value);
        if (!string.Equals(value, normalized, StringComparison.OrdinalIgnoreCase))
        {
            UiLanguage = normalized;
            return;
        }

        ApplyUiLanguage(normalized);
    }

    private async Task LoadCategoriesAsync()
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var catalog = scope.ServiceProvider.GetRequiredService<IProductCatalogService>();
        var cats = await catalog.GetCategoriesAsync();

        var filters = new ObservableCollection<CategoryFilterItem>
        {
            new() { CategoryId = null, Name = "All", IsSelected = true }
        };
        foreach (var c in cats)
            filters.Add(new CategoryFilterItem { CategoryId = c.Id, Name = c.Name });
        CategoryFilters = filters;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Tab commands
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Creates a brand-new invoice in a new tab.</summary>
    [RelayCommand]
    private async Task NewSaleAsync()
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var sales = scope.ServiceProvider.GetRequiredService<ISaleService>();
        var id = await sales.StartNewSaleAsync();

        var tab = new InvoiceTab
        {
            InvoiceId = id,
            Label     = $"#{InvoiceTabs.Count + 1:D3}"
        };

        InvoiceTabs.Add(tab);
        ActiveTab = tab;    // triggers OnActiveTabChanged → refreshes empty cart
        QtyInput  = "1";
        StatusText = $"Invoice {tab.Label} opened.";
    }

    /// <summary>Switches the visible cart to the chosen tab.</summary>
    [RelayCommand]
    private void SwitchTab(InvoiceTab? tab)
    {
        if (tab is null || tab == ActiveTab) return;
        ActiveTab = tab;    // triggers OnActiveTabChanged
    }

    /// <summary>Toggles the active invoice between Open and Held.</summary>
    [RelayCommand]
    private async Task HoldResumeAsync()
    {
        if (ActiveTab is null || CurrentInvoiceId is null) return;

        await using var scope = _scopeFactory.CreateAsyncScope();
        var sales = scope.ServiceProvider.GetRequiredService<ISaleService>();

        if (ActiveTab.IsHeld)
        {
            await sales.ResumeInvoiceAsync(CurrentInvoiceId.Value);
            ActiveTab.IsHeld = false;
            StatusText = $"Invoice {ActiveTab.Label} resumed.";
        }
        else
        {
            await sales.HoldInvoiceAsync(CurrentInvoiceId.Value);
            ActiveTab.IsHeld = true;
            StatusText = $"Invoice {ActiveTab.Label} is on hold.";
        }

        OnPropertyChanged(nameof(ActiveTabIsHeld));
        OnPropertyChanged(nameof(HoldResumeLabel));
        OnPropertyChanged(nameof(HoldResumeActionLabel));
        OnPropertyChanged(nameof(InvoiceStatusLabel));
        OnPropertyChanged(nameof(HeldTabCount));
    }

    /// <summary>Removes a tab; cancels its invoice in the DB.</summary>
    [RelayCommand]
    private async Task CloseTabAsync(InvoiceTab? tab)
    {
        if (tab is null) return;

        // Cancel the invoice (it's being abandoned)
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var sales = scope.ServiceProvider.GetRequiredService<ISaleService>();
            await sales.CancelInvoiceAsync(tab.InvoiceId);
        }
        catch { /* best-effort */ }

        if (InvoiceTabs.Count <= 1)
        {
            // Keep at least one tab – reset it to a fresh invoice
            await NewSaleAsync();
            if (InvoiceTabs.Count > 1)
                InvoiceTabs.RemoveAt(0);   // remove the old (now-cancelled) tab
            return;
        }

        int  idx      = InvoiceTabs.IndexOf(tab);
        bool wasActive = tab == ActiveTab;
        InvoiceTabs.Remove(tab);

        if (wasActive)
            ActiveTab = InvoiceTabs[Math.Min(idx, InvoiceTabs.Count - 1)];
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Sidebar navigation + top-bar utilities
    // ═══════════════════════════════════════════════════════════════════════

    [RelayCommand]
    private async Task RefreshAsync()
    {
        await SearchAsync();
        await RefreshCartAsync();
        StatusText = T("Refreshed.", "تم التحديث.", "רוענן.");
    }

    [RelayCommand]
    private void ToggleDarkMode()
    {
        IsDarkMode = !IsDarkMode;
        App.SetTheme(IsDarkMode);
        OnPropertyChanged(nameof(DarkModeIcon));
    }

    [RelayCommand]
    private void ToggleFullscreen()
    {
        var win = System.Windows.Application.Current.MainWindow;
        if (win is null) return;
        if (IsFullscreen)
        {
            win.WindowStyle = System.Windows.WindowStyle.SingleBorderWindow;
            win.WindowState = System.Windows.WindowState.Normal;
        }
        else
        {
            win.WindowStyle = System.Windows.WindowStyle.None;
            win.WindowState = System.Windows.WindowState.Maximized;
        }
        IsFullscreen = !IsFullscreen;
        OnPropertyChanged(nameof(FullscreenIcon));
    }

    [RelayCommand]
    private void ToggleNumpad()
    {
        ShowNumpad = !ShowNumpad;
        OnPropertyChanged(nameof(NumpadIcon));
    }

    [RelayCommand]
    private void SetUiLanguage(string? code) =>
        UiLanguage = NormalizeLanguage(code);

    /// <summary>Sidebar: Home / Cashier — refresh product list.</summary>
    [RelayCommand]
    private async Task GoHomeAsync()
    {
        SelectedPage = "Cashier";
        await SearchAsync();
    }

    /// <summary>Sidebar: Customers (future feature).</summary>
    [RelayCommand]
    private void GoCustomers()
    {
        SelectedPage = "Customers";
        MessageBox.Show(
            T("Customer management is coming in the next version.",
              "إدارة العملاء قادمة في الإصدار القادم.",
              "ניהול לקוחות יתווסף בגרסה הבאה."),
            AppTitle, MessageBoxButton.OK, MessageBoxImage.Information);
        SelectedPage = "Cashier";
    }

    /// <summary>Sidebar: Orders → opens Reports window.</summary>
    [RelayCommand]
    private void GoOrders()
    {
        SelectedPage = "Orders";
        OpenReports();
        SelectedPage = "Cashier";
    }

    /// <summary>Sidebar: Products → opens catalog (Admin only).</summary>
    [RelayCommand]
    private async Task GoProductsAsync()
    {
        if (!IsAdmin) { StatusText = T("Admin access required.", "صلاحية المدير مطلوبة.", "נדרשת הרשאת מנהל."); return; }
        SelectedPage = "Products";
        await ManageProductsAsync();
        SelectedPage = "Cashier";
    }

    /// <summary>Sidebar: Reports → opens reports window (Admin only).</summary>
    [RelayCommand]
    private void GoReports()
    {
        if (!IsAdmin) { StatusText = T("Admin access required.", "صلاحية المدير مطلوبة.", "נדרשת הרשאת מנהל."); return; }
        SelectedPage = "Reports";
        OpenReports();
        SelectedPage = "Cashier";
    }

    /// <summary>Sidebar: Audit → opens audit log window (Admin only).</summary>
    [RelayCommand]
    private void GoAudit()
    {
        if (!IsAdmin) { StatusText = T("Admin access required.", "صلاحية المدير مطلوبة.", "נדרשת הרשאת מנהל."); return; }
        SelectedPage = "Audit";
        OpenAudit();
        SelectedPage = "Cashier";
    }

    /// <summary>Sidebar: Settings → opens store settings (Admin only).</summary>
    [RelayCommand]
    private async Task GoSettingsAsync()
    {
        if (!IsAdmin) { StatusText = T("Admin access required.", "صلاحية المدير مطلوبة.", "נדרשת הרשאת מנהל."); return; }
        SelectedPage = "Settings";

        var window = _services.GetRequiredService<CurrencySettingsWindow>();
        window.Owner = System.Windows.Application.Current.MainWindow;
        if (window.ShowDialog() == true)
        {
            RefreshCurrencyPresentation();
            await SearchAsync();
            await RefreshCartAsync();
            StatusText = T("Store settings updated.", "تم تحديث إعدادات المتجر.", "הגדרות החנות עודכנו.");
        }

        SelectedPage = "Cashier";
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Sale lifecycle
    // ═══════════════════════════════════════════════════════════════════════

    [RelayCommand]
    private async Task CompleteSaleAsync()
    {
        if (CurrentInvoiceId is null) return;
        if (ActiveTab?.IsHeld == true)
        {
            MessageBox.Show(
                T("This invoice is on hold.\nResume it first before completing the sale.",
                  "هذه الفاتورة معلّقة.\nاستأنفها أولاً قبل إتمام البيع.",
                  "חשבונית זו מושהית.\nיש להמשיך אותה לפני השלמת המכירה."),
                AppTitle, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        await using var scope = _scopeFactory.CreateAsyncScope();
        var sales = scope.ServiceProvider.GetRequiredService<ISaleService>();

        var total = await sales.GetInvoiceTotalAsync(CurrentInvoiceId.Value);
        if (total <= 0)
        {
            MessageBox.Show(EmptyCartLabel, AppTitle, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var pay = new PaymentWindow(total, CurrencySuffix) { Owner = System.Windows.Application.Current.MainWindow };
        if (pay.ShowDialog() != true) return;

        var result = await sales.CompleteCashSaleAsync(CurrentInvoiceId.Value, pay.CashTendered);
        if (!result.Success || result.Receipt is null)
        {
            MessageBox.Show(result.ErrorMessage ?? T("Could not complete sale.", "تعذر إتمام البيع.", "לא ניתן להשלים את המכירה."), AppTitle,
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _printer.Print(result.Receipt);
        MessageBox.Show(
            $"{T("Sale complete!", "تمت عملية البيع!", "המכירה הושלמה!")}\n" +
            $"{TotalLabel}:   {result.Receipt.Total:N2} {CurrencySuffix}\n" +
            $"{T("Change", "الباقي", "עודף")}: {result.Receipt.Change:N2} {CurrencySuffix}",
            AppTitle, MessageBoxButton.OK, MessageBoxImage.Information);

        // Close this tab and open a fresh one
        var completedTab = ActiveTab;
        await NewSaleAsync();
        if (completedTab is not null)
            InvoiceTabs.Remove(completedTab);

        await SearchAsync();
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Product search & categories
    // ═══════════════════════════════════════════════════════════════════════

    [RelayCommand]
    private async Task SearchAsync()
    {
        var selectedCat = CategoryFilters.FirstOrDefault(c => c.IsSelected);
        await using var scope = _scopeFactory.CreateAsyncScope();
        var catalog = scope.ServiceProvider.GetRequiredService<IProductCatalogService>();
        var list = await catalog.SearchProductsAsync(
            string.IsNullOrWhiteSpace(SearchQuery) ? null : SearchQuery,
            selectedCat?.CategoryId);
        SearchResults = new ObservableCollection<ProductListItemDto>(list);
    }

    /// <summary>
    /// Handles keyboard-wedge barcode bursts: exact barcode match is auto-added to cart.
    /// </summary>
    public async Task ProcessBarcodeScanAsync(string? rawCode)
    {
        if (CurrentInvoiceId is null || string.IsNullOrWhiteSpace(rawCode))
            return;

        var code = rawCode.Trim();

        await using var scope = _scopeFactory.CreateAsyncScope();
        var catalog = scope.ServiceProvider.GetRequiredService<IProductCatalogService>();
        var matches = await catalog.SearchProductsAsync(code);
        var product = matches.FirstOrDefault(p =>
            !string.IsNullOrWhiteSpace(p.Barcode) &&
            string.Equals(p.Barcode, code, StringComparison.OrdinalIgnoreCase));

        if (product is null)
        {
            StatusText = $"{T("Barcode not found", "لم يتم العثور على باركود", "ברקוד לא נמצא")}: {code}";
            return;
        }

        await AddProductToCartAsync(product);
        SearchQuery = string.Empty;
    }

    partial void OnSearchQueryChanged(string value) =>
        _ = SearchAsync();

    [RelayCommand]
    private async Task FilterByCategoryAsync(CategoryFilterItem? item)
    {
        foreach (var cat in CategoryFilters) cat.IsSelected = false;
        if (item is not null) item.IsSelected = true;
        await SearchAsync();
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Cart operations
    // ═══════════════════════════════════════════════════════════════════════

    [RelayCommand]
    private async Task AddProductToCartAsync(ProductListItemDto? product)
    {
        if (product is null || CurrentInvoiceId is null) return;
        if (!decimal.TryParse(QtyInput, NumberStyles.Any, CultureInfo.InvariantCulture, out var qty) || qty <= 0)
            qty = 1m;

        await using var scope = _scopeFactory.CreateAsyncScope();
        var sales = scope.ServiceProvider.GetRequiredService<ISaleService>();
        try
        {
            await sales.AddOrMergeLineAsync(CurrentInvoiceId.Value, product.Id, qty);
            await RefreshCartAsync(scope);
            QtyInput   = "1";
            StatusText = $"{T("Added", "تمت إضافة", "נוסף")} {product.Name}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, AppTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    [RelayCommand]
    private async Task RemoveLineAsync(CartLineItem? line)
    {
        if (line is null || CurrentInvoiceId is null) return;
        line.CancelPendingCommits();
        if (ReferenceEquals(ActiveCalcLine, line)) ActiveCalcLine = null;
        await using var scope = _scopeFactory.CreateAsyncScope();
        var sales = scope.ServiceProvider.GetRequiredService<ISaleService>();
        await sales.RemoveLineAsync(CurrentInvoiceId.Value, line.LineId);
        await RefreshCartAsync(scope);
    }

    [RelayCommand]
    private async Task IncreaseLineQtyAsync(CartLineItem? line)
    {
        if (line is null || CurrentInvoiceId is null) return;
        await using var scope = _scopeFactory.CreateAsyncScope();
        var sales = scope.ServiceProvider.GetRequiredService<ISaleService>();
        try
        {
            await sales.SetLineQuantityAsync(CurrentInvoiceId.Value, line.LineId, line.Quantity + 1m);
            await RefreshCartAsync(scope, line.LineId);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, AppTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    [RelayCommand]
    private async Task DecreaseLineQtyAsync(CartLineItem? line)
    {
        if (line is null || CurrentInvoiceId is null) return;
        if (line.Quantity <= 1m) { await RemoveLineAsync(line); return; }

        await using var scope = _scopeFactory.CreateAsyncScope();
        var sales = scope.ServiceProvider.GetRequiredService<ISaleService>();
        try
        {
            await sales.SetLineQuantityAsync(CurrentInvoiceId.Value, line.LineId, line.Quantity - 1m);
            await RefreshCartAsync(scope, line.LineId);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, AppTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>
    /// Fired by a <see cref="CartLineItem"/> a moment after the cashier stops typing in its QTY box —
    /// this is what replaced the old checkmark "apply" button.
    /// </summary>
    private void OnLineQuantityEdited(CartLineItem line) => _ = CommitLineQuantityAsync(line);

    /// <summary>Same as <see cref="OnLineQuantityEdited"/> but for the discount % box.</summary>
    private void OnLineDiscountEdited(CartLineItem line) => _ = CommitLineDiscountAsync(line);

    private async Task CommitLineQuantityAsync(CartLineItem line)
    {
        if (CurrentInvoiceId is null) return;
        await using var scope = _scopeFactory.CreateAsyncScope();
        var sales = scope.ServiceProvider.GetRequiredService<ISaleService>();
        try
        {
            await sales.SetLineQuantityAsync(CurrentInvoiceId.Value, line.LineId, Math.Max(0m, line.Quantity));
            await RefreshCartAsync(scope, line.Quantity > 0m ? line.LineId : null);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, AppTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
            await RefreshCartAsync(scope, line.LineId); // revert the box to the last-known-good server value
        }
    }

    private async Task CommitLineDiscountAsync(CartLineItem line)
    {
        if (CurrentInvoiceId is null) return;
        await using var scope = _scopeFactory.CreateAsyncScope();
        var sales = scope.ServiceProvider.GetRequiredService<ISaleService>();
        try
        {
            await sales.SetLineDiscountAsync(CurrentInvoiceId.Value, line.LineId, line.DiscountPercent);
            await RefreshCartAsync(scope, line.LineId);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, AppTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
            await RefreshCartAsync(scope, line.LineId);
        }
    }

    /// <summary>Sets the invoice-level VAT / tax rate.</summary>
    [RelayCommand]
    private async Task SetInvoiceTaxAsync(string? taxInput)
    {
        if (CurrentInvoiceId is null) return;
        if (!decimal.TryParse(taxInput ?? TaxPercent.ToString(),
                System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var pct))
            pct = 0m;

        await using var scope = _scopeFactory.CreateAsyncScope();
        var sales = scope.ServiceProvider.GetRequiredService<ISaleService>();
        try
        {
            await sales.SetInvoiceTaxAsync(CurrentInvoiceId.Value, pct);
            await RefreshCartAsync(scope);
            StatusText = $"{T("Tax rate set to", "تم ضبط الضريبة إلى", "שיעור המע\"מ הוגדר ל")} {pct:N0}%.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, AppTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Numpad — routes into whichever field is "active": the bottom calculator's
    // own QTY/PRICE display, or (when set) a specific cart line's Qty/Disc box.
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>True right after a field becomes active — the next digit replaces its contents instead of appending.</summary>
    private bool _calcFieldFreshlySelected = true;

    [RelayCommand]
    private void SelectCalcField(string field)
    {
        ActiveCalcLine  = null;
        ActiveCalcField = field;
        _calcFieldFreshlySelected = true;
    }

    /// <summary>Routes the calculator to a cart line's QTY box.</summary>
    [RelayCommand]
    private void SelectLineQtyField(CartLineItem? line)
    {
        ActiveCalcLine  = line;
        ActiveCalcField = "Qty";
        _calcFieldFreshlySelected = true;
    }

    /// <summary>Routes the calculator to a cart line's discount box.</summary>
    [RelayCommand]
    private void SelectLineDiscField(CartLineItem? line)
    {
        ActiveCalcLine  = line;
        ActiveCalcField = "Disc";
        _calcFieldFreshlySelected = true;
    }

    private string GetActiveFieldText() =>
        ActiveCalcLine is not null
            ? (ActiveCalcField == "Disc" ? ActiveCalcLine.DiscText : ActiveCalcLine.QtyText)
            : (ActiveCalcField == "Price" ? PriceInput : QtyInput);

    private void SetActiveFieldText(string value)
    {
        if (ActiveCalcLine is not null)
        {
            if (ActiveCalcField == "Disc") ActiveCalcLine.DiscText = value;
            else ActiveCalcLine.QtyText = value;
        }
        else
        {
            if (ActiveCalcField == "Price") PriceInput = value;
            else QtyInput = value;
        }
    }

    /// <summary>Qty fields default to "1" (a sane multiplier); price/discount fields default to "0".</summary>
    private string GetDefaultForActiveField() => ActiveCalcField == "Qty" ? "1" : "0";

    [RelayCommand]
    private void NumpadPress(string key)
    {
        if (_calcFieldFreshlySelected)
        {
            SetActiveFieldText(key == "." ? "0." : key);
            _calcFieldFreshlySelected = false;
            return;
        }

        var current = GetActiveFieldText();
        if (key == "." && current.Contains('.')) return;
        if (current.Length >= 8) return;
        SetActiveFieldText(current + key);
    }

    [RelayCommand]
    private void NumpadBackspace()
    {
        var current = GetActiveFieldText();
        SetActiveFieldText(current.Length > 1 ? current[..^1] : GetDefaultForActiveField());
        _calcFieldFreshlySelected = false;
    }

    [RelayCommand]
    private void NumpadClear()
    {
        SetActiveFieldText(GetDefaultForActiveField());
        _calcFieldFreshlySelected = true;
    }

    /// <summary>Quick "+1" bump on the active field — faster than tapping digits for small adjustments.</summary>
    [RelayCommand]
    private void NumpadIncrement()
    {
        if (decimal.TryParse(GetActiveFieldText(), NumberStyles.Any, CultureInfo.InvariantCulture, out var value))
            SetActiveFieldText((value + 1m).ToString(CultureInfo.InvariantCulture));
        _calcFieldFreshlySelected = false;
    }

    /// <summary>Adds a manually-priced item (not in the catalog) to the cart using QtyInput / PriceInput.</summary>
    [RelayCommand]
    private async Task AddCustomItemAsync()
    {
        if (CurrentInvoiceId is null) return;

        if (!decimal.TryParse(QtyInput, NumberStyles.Any, CultureInfo.InvariantCulture, out var qty) || qty <= 0)
        {
            MessageBox.Show(
                T("Enter a valid quantity.", "أدخل كمية صالحة.", "הזן כמות תקינה."),
                AppTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!decimal.TryParse(PriceInput, NumberStyles.Any, CultureInfo.InvariantCulture, out var price) || price < 0)
        {
            MessageBox.Show(
                T("Enter a valid price.", "أدخل سعرًا صالحًا.", "הזן מחיר תקין."),
                AppTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        var sales = scope.ServiceProvider.GetRequiredService<ISaleService>();
        try
        {
            await sales.AddCustomItemAsync(CurrentInvoiceId.Value, CustomItemDefaultName, qty, price);
            await RefreshCartAsync(scope);
            QtyInput   = "1";
            PriceInput = "0";
            StatusText = $"{T("Added", "تمت إضافة", "נוסף")} {CustomItemDefaultName}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, AppTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Catalog window
    // ═══════════════════════════════════════════════════════════════════════

    [RelayCommand]
    private async Task PriceCheckAsync()
    {
        var w = new PriceCheckWindow(_scopeFactory)
        {
            Owner = System.Windows.Application.Current.MainWindow
        };
        if (w.ShowDialog() == true && w.SelectedProduct is not null)
            await AddProductToCartAsync(w.SelectedProduct);
    }

    [RelayCommand]
    private async Task RefundSaleAsync()
    {
        var w = new RefundWindow(_scopeFactory)
        {
            Owner = System.Windows.Application.Current.MainWindow
        };
        if (w.ShowDialog() != true || w.SelectedInvoiceId is null) return;

        await using var scope = _scopeFactory.CreateAsyncScope();
        var sales = scope.ServiceProvider.GetRequiredService<ISaleService>();
        var (success, error) = await sales.RefundInvoiceAsync(w.SelectedInvoiceId.Value);

        if (success)
        {
            StatusText = T("Refund processed — stock restored.", "تمت عملية الاسترجاع — تم تحديث المخزون.", "ההחזר בוצע — המלאי שוחזר.");
            MessageBox.Show(
                T("Refund processed successfully.\nStock has been restored.",
                  "تمت عملية الاسترجاع بنجاح.\nتمت استعادة المخزون.",
                  "ההחזר בוצע בהצלחה.\nהמלאי שוחזר."),
                AppTitle, MessageBoxButton.OK, MessageBoxImage.Information);
            await SearchAsync();
        }
        else
        {
            MessageBox.Show(error ?? T("Could not process refund.", "تعذر تنفيذ الاسترجاع.", "לא ניתן לבצע החזר."), AppTitle,
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    [RelayCommand]
    private void ToggleCustomerDisplay()
    {
        if (_customerDisplay is not null)
        {
            _customerDisplay.Close();
            _customerDisplay       = null;
            CustomerDisplayOpen    = false;
            StatusText             = T("Customer display closed.", "تم إغلاق شاشة العميل.", "צג הלקוח נסגר.");
            return;
        }

        _customerDisplay = new CustomerDisplayWindow
        {
            Owner = System.Windows.Application.Current.MainWindow
        };
        _customerDisplay.Closed += (_, _) =>
        {
            _customerDisplay    = null;
            CustomerDisplayOpen = false;
        };
        _customerDisplay.Show();
        CustomerDisplayOpen = true;
        StatusText          = T("Customer display opened.", "تم فتح شاشة العميل.", "צג הלקוח נפתח.");
        _customerDisplay.Update(CartLines.Select(l => l.ToDto()).ToList(), Total, CurrencySuffix);
    }

    [RelayCommand]
    private void OpenReports()
    {
        var w = new ReportsWindow(_scopeFactory)
        {
            Owner = System.Windows.Application.Current.MainWindow
        };
        w.ShowDialog();
    }

    private void OpenAudit()
    {
        var w = new AuditLogWindow(_scopeFactory)
        {
            Owner = System.Windows.Application.Current.MainWindow
        };
        w.ShowDialog();
    }

    [RelayCommand]
    private async Task ManageProductsAsync()
    {
        var w = _services.GetRequiredService<ProductManagementWindow>();
        w.Owner = System.Windows.Application.Current.MainWindow;
        w.ShowDialog();
        await LoadCategoriesAsync();
        await SearchAsync();
    }

    private void RefreshCurrencyPresentation()
    {
        OnPropertyChanged(nameof(CurrencySuffix));
        OnPropertyChanged(nameof(FormattedSubtotal));
        OnPropertyChanged(nameof(FormattedTaxAmount));
        OnPropertyChanged(nameof(FormattedTotal));
        _customerDisplay?.Update(CartLines.Select(l => l.ToDto()).ToList(), Total, CurrencySuffix);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Cart refresh helper
    // ═══════════════════════════════════════════════════════════════════════

    private async Task RefreshCartAsync(IServiceScope? existingScope = null, Guid? preferredLineId = null)
    {
        var targetLineId = preferredLineId ?? ActiveCalcLine?.LineId;

        // New CartLineItem instances are about to replace these — stop their debounce timers so a
        // stale one can't fire a commit for a line that's no longer part of the visible collection.
        foreach (var old in CartLines) old.CancelPendingCommits();

        if (CurrentInvoiceId is null)
        {
            CartLines     = new ObservableCollection<CartLineItem>();
            ActiveCalcLine = null;
            Subtotal   = 0; TaxPercent = 0; TaxAmount = 0; Total = 0;
            _customerDisplay?.Update([], 0, CurrencySuffix);
            return;
        }

        async Task FetchFrom(ISaleService sales)
        {
            var dtos = await sales.GetCartLinesAsync(CurrentInvoiceId.Value);
            CartLines = new ObservableCollection<CartLineItem>(
                dtos.Select(d => new CartLineItem(d, OnLineQuantityEdited, OnLineDiscountEdited)));
            ActiveCalcLine = targetLineId.HasValue
                ? CartLines.FirstOrDefault(x => x.LineId == targetLineId.Value)
                : null;
            SyncLineCalcHighlight();
            var summary = await sales.GetInvoiceSummaryAsync(CurrentInvoiceId.Value);
            Subtotal   = summary.Subtotal;
            TaxPercent = summary.TaxPercent;
            TaxAmount  = summary.TaxAmount;
            Total      = summary.Total;
            _customerDisplay?.Update(CartLines.Select(l => l.ToDto()).ToList(), Total, CurrencySuffix);
        }

        if (existingScope is not null)
        {
            await FetchFrom(existingScope.ServiceProvider.GetRequiredService<ISaleService>());
            return;
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        await FetchFrom(scope.ServiceProvider.GetRequiredService<ISaleService>());
    }

    private void ApplyUiLanguage(string code)
    {
        var culture = new CultureInfo(code);
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
        System.Threading.Thread.CurrentThread.CurrentCulture = culture;
        System.Threading.Thread.CurrentThread.CurrentUICulture = culture;

        NotifyLocalizationChanged();
        StatusText = code switch
        {
            "ar" => "تم تفعيل العربية (RTL).",
            "he" => "העברית הופעלה (RTL).",
            _ => "English enabled."
        };
    }

    private void NotifyLocalizationChanged()
    {
        OnPropertyChanged(nameof(UiFlowDirection));
        OnPropertyChanged(nameof(IsUiLanguageArabic));
        OnPropertyChanged(nameof(IsUiLanguageEnglish));
        OnPropertyChanged(nameof(IsUiLanguageHebrew));
        OnPropertyChanged(nameof(LanguageLabel));
        OnPropertyChanged(nameof(NavHomeLabel));
        OnPropertyChanged(nameof(NavCustomersLabel));
        OnPropertyChanged(nameof(NavOrdersLabel));
        OnPropertyChanged(nameof(NavProductsLabel));
        OnPropertyChanged(nameof(NavReportsLabel));
        OnPropertyChanged(nameof(NavAuditLabel));
        OnPropertyChanged(nameof(NavSettingsLabel));
        OnPropertyChanged(nameof(NavLogoutLabel));
        OnPropertyChanged(nameof(OnlineLabel));
        OnPropertyChanged(nameof(HoldOrdersLabel));
        OnPropertyChanged(nameof(NewInvoiceLabel));
        OnPropertyChanged(nameof(ScanLabel));
        OnPropertyChanged(nameof(LowStockLabel));
        OnPropertyChanged(nameof(WalkInCustomerLabel));
        OnPropertyChanged(nameof(EmptyCartLabel));
        OnPropertyChanged(nameof(QtyLabel));
        OnPropertyChanged(nameof(EachLabel));
        OnPropertyChanged(nameof(AddNoteLabel));
        OnPropertyChanged(nameof(RefundLabel));
        OnPropertyChanged(nameof(QtyWeightLabel));
        OnPropertyChanged(nameof(PriceFieldLabel));
        OnPropertyChanged(nameof(AddToCartLabel));
        OnPropertyChanged(nameof(AddCustomItemToolTip));
        OnPropertyChanged(nameof(ClearCalcLabel));
        OnPropertyChanged(nameof(CustomItemDefaultName));
        OnPropertyChanged(nameof(SubtotalLabel));
        OnPropertyChanged(nameof(TaxLabelPrefix));
        OnPropertyChanged(nameof(TotalLabel));
        OnPropertyChanged(nameof(ProceedLabel));
        OnPropertyChanged(nameof(HoldOrderLabel));
        OnPropertyChanged(nameof(ResumeLabel));
        OnPropertyChanged(nameof(HoldResumeActionLabel));
        OnPropertyChanged(nameof(HoldResumeToolTip));
        OnPropertyChanged(nameof(RefreshToolTip));
        OnPropertyChanged(nameof(CustomerDisplayToolTip));
        OnPropertyChanged(nameof(PriceCheckToolTip));
        OnPropertyChanged(nameof(ScanToolTip));
        OnPropertyChanged(nameof(ToggleImagesToolTip));
        OnPropertyChanged(nameof(ToggleDarkModeToolTip));
        OnPropertyChanged(nameof(HoldOrdersToolTip));
        OnPropertyChanged(nameof(AppTitle));
        OnPropertyChanged(nameof(InvoiceLabel));
        OnPropertyChanged(nameof(InvoiceStatusLabel));
    }

    private string T(string en, string ar, string he) =>
        UiLanguage switch
        {
            "ar" => ar,
            "he" => he,
            _ => en
        };

    private string ItemWord(int count)
    {
        if (IsUiLanguageArabic) return count == 1 ? "عنصر" : "عناصر";
        if (IsUiLanguageHebrew) return count == 1 ? "פריט" : "פריטים";
        return count == 1 ? "item" : "items";
    }

    private static bool IsRtlLanguage(string? code) =>
        string.Equals(code, "ar", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(code, "he", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeLanguage(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
            return "en";

        var normalized = code.Trim().ToLowerInvariant();
        return normalized is "ar" or "he" ? normalized : "en";
    }
}
