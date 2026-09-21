using POS.Application.Abstractions;

namespace POS.Wpf.Services;

public sealed class CurrentSession : ICurrentSession
{
    public Guid TenantId { get; private set; }
    public Guid UserId { get; private set; }
    public Guid StoreId { get; private set; }
    public string Username { get; private set; } = "";
    public string RoleName { get; private set; } = "";
    public int PermissionsMask { get; private set; }
    public string BaseCurrencyCode { get; private set; } = "ILS";
    public string? CurrencySymbol { get; private set; } = "₪";
    public bool IsAuthenticated { get; private set; }
    public string? Password { get; private set; }
    public DateTime? LastOnlineContactUtc { get; private set; }

    public void Set(Guid tenantId, Guid userId, Guid storeId, string username, string roleName, int permissionsMask, string baseCurrencyCode, string? currencySymbol)
    {
        TenantId = tenantId;
        UserId = userId;
        StoreId = storeId;
        Username = username;
        RoleName = roleName;
        PermissionsMask = permissionsMask;
        BaseCurrencyCode = string.IsNullOrWhiteSpace(baseCurrencyCode) ? "ILS" : baseCurrencyCode.Trim();
        CurrencySymbol = string.IsNullOrWhiteSpace(currencySymbol)
            ? (BaseCurrencyCode == "ILS" ? "₪" : null)
            : currencySymbol.Trim();
        IsAuthenticated = true;
    }

    public void SetPassword(string? password) => Password = password;

    public void SetLastOnlineContactUtc(DateTime? lastOnlineContactUtc) => LastOnlineContactUtc = lastOnlineContactUtc;

    public void Clear()
    {
        TenantId = Guid.Empty;
        UserId = Guid.Empty;
        StoreId = Guid.Empty;
        Username = "";
        RoleName = "";
        PermissionsMask = 0;
        BaseCurrencyCode = "ILS";
        CurrencySymbol = "₪";
        IsAuthenticated = false;
        Password = null;
        LastOnlineContactUtc = null;
    }
}
