using POS.Application.Abstractions;

namespace POS.Api;

internal sealed class ApiCurrentSession : ICurrentSession
{
    public Guid TenantId { get; private set; }
    public Guid UserId { get; private set; }
    public Guid StoreId { get; private set; }
    public string Username { get; private set; } = string.Empty;
    public string RoleName { get; private set; } = string.Empty;
    public int PermissionsMask { get; private set; }
    public string BaseCurrencyCode { get; private set; } = "USD";
    public string? CurrencySymbol { get; private set; }
    public bool IsAuthenticated => UserId != Guid.Empty && StoreId != Guid.Empty;
    public string? Password { get; private set; }

    public void Set(Guid tenantId, Guid userId, Guid storeId, string username, string roleName, int permissionsMask, string baseCurrencyCode, string? currencySymbol)
    {
        TenantId = tenantId;
        UserId = userId;
        StoreId = storeId;
        Username = username;
        RoleName = roleName;
        PermissionsMask = permissionsMask;
        BaseCurrencyCode = baseCurrencyCode;
        CurrencySymbol = currencySymbol;
    }

    public void SetPassword(string? password) => Password = password;

    public void Clear()
    {
        TenantId = Guid.Empty;
        UserId = Guid.Empty;
        StoreId = Guid.Empty;
        Username = string.Empty;
        RoleName = string.Empty;
        PermissionsMask = 0;
        BaseCurrencyCode = "USD";
        CurrencySymbol = null;
        Password = null;
    }
}