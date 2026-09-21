namespace POS.Application.Abstractions;

public interface ICurrentSession
{
    Guid TenantId { get; }
    Guid UserId { get; }
    Guid StoreId { get; }
    string Username { get; }
    string RoleName { get; }
    /// <summary>Bitmask of <see cref="POS.Core.Enums.Permission"/> flags granted to the current user's role.</summary>
    int PermissionsMask { get; }
    /// <summary>ISO-like code for the store base currency (e.g. ILS).</summary>
    string BaseCurrencyCode { get; }
    /// <summary>Optional display symbol (e.g. ₪).</summary>
    string? CurrencySymbol { get; }
    bool IsAuthenticated { get; }

    void Set(Guid tenantId, Guid userId, Guid storeId, string username, string roleName, int permissionsMask, string baseCurrencyCode, string? currencySymbol);
    void Clear();
}
