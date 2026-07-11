using POS.Application.Abstractions;

namespace POS.Wpf.Services;

public sealed class CurrentSession : ICurrentSession
{
    public Guid UserId { get; private set; }
    public Guid StoreId { get; private set; }
    public string Username { get; private set; } = "";
    public string RoleName { get; private set; } = "";
    public string BaseCurrencyCode { get; private set; } = "ILS";
    public string? CurrencySymbol { get; private set; }
    public bool IsAuthenticated { get; private set; }

    public void Set(Guid userId, Guid storeId, string username, string roleName, string baseCurrencyCode, string? currencySymbol)
    {
        UserId = userId;
        StoreId = storeId;
        Username = username;
        RoleName = roleName;
        BaseCurrencyCode = string.IsNullOrWhiteSpace(baseCurrencyCode) ? "ILS" : baseCurrencyCode.Trim();
        CurrencySymbol = string.IsNullOrWhiteSpace(currencySymbol) ? null : currencySymbol.Trim();
        IsAuthenticated = true;
    }

    public void Clear()
    {
        UserId = Guid.Empty;
        StoreId = Guid.Empty;
        Username = "";
        RoleName = "";
        BaseCurrencyCode = "ILS";
        CurrencySymbol = null;
        IsAuthenticated = false;
    }
}
