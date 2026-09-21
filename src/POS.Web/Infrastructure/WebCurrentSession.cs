using POS.Application.Abstractions;

namespace POS.Web.Infrastructure;

internal sealed class WebCurrentSession : ICurrentSession
{
    public Guid TenantId { get; private set; }
    public Guid UserId { get; private set; }
    public Guid StoreId { get; private set; }
    public string Username { get; private set; } = string.Empty;
    public string RoleName { get; private set; } = string.Empty;
    public int PermissionsMask { get; private set; }
    public string BaseCurrencyCode { get; private set; } = "ILS";
    public string? CurrencySymbol { get; private set; } = "₪";
    public bool IsAuthenticated => UserId != Guid.Empty && StoreId != Guid.Empty;
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
        BaseCurrencyCode = baseCurrencyCode;
        CurrencySymbol = string.IsNullOrWhiteSpace(currencySymbol)
            ? (baseCurrencyCode == "ILS" ? "₪" : null)
            : currencySymbol;
    }

    public void SetPassword(string? password) => Password = password;

    public void SetLastOnlineContactUtc(DateTime? lastOnlineContactUtc) => LastOnlineContactUtc = lastOnlineContactUtc;

    public void Clear()
    {
        TenantId = Guid.Empty;
        UserId = Guid.Empty;
        StoreId = Guid.Empty;
        Username = string.Empty;
        RoleName = string.Empty;
        PermissionsMask = 0;
        BaseCurrencyCode = "ILS";
        CurrencySymbol = "₪";
        Password = null;
        LastOnlineContactUtc = null;
    }
}