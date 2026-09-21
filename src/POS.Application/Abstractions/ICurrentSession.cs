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
    /// <summary>
    /// The verified plaintext password from the current login, kept in memory only, for
    /// re-authenticating this same identity against the API when a device needs its own HTTP
    /// session (e.g. the WPF background sync worker). Never persisted or logged.
    /// </summary>
    string? Password { get; }
    /// <summary>
    /// UTC time this identity last proved its authorization to the server (a successful online
    /// login), used to enforce a finite offline-authorization validity window (tenant.md §5) before
    /// blocking administrative actions on a long-disconnected device. Null means "never proven."
    /// </summary>
    DateTime? LastOnlineContactUtc { get; }

    void Set(Guid tenantId, Guid userId, Guid storeId, string username, string roleName, int permissionsMask, string baseCurrencyCode, string? currencySymbol);
    void SetPassword(string? password);
    void SetLastOnlineContactUtc(DateTime? lastOnlineContactUtc);
    void Clear();
}
