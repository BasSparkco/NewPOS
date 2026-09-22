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
    /// True once TenantId/StoreId are populated — either by an interactive <see cref="Set"/> login or
    /// by <see cref="SetDeviceSyncScope"/> restoring a device-scoped store binding after the interactive
    /// user has logged out. The minimum a background sync pass needs to know which store to act on;
    /// unlike <see cref="IsAuthenticated"/>, it does not imply an interactive identity is signed in.
    /// </summary>
    bool HasSyncScope { get; }
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
    /// <summary>
    /// Restores TenantId/StoreId only (never UserId/Username/Password/permissions/IsAuthenticated) so
    /// background device-credential sync can keep running after an interactive logout. A WPF install is
    /// bound to exactly one store, so this only ever re-establishes which store to sync, never grants
    /// any interactive access. No-op semantics beyond WPF's background sync worker — other hosts never
    /// call this.
    /// </summary>
    void SetDeviceSyncScope(Guid tenantId, Guid storeId);
    void SetPassword(string? password);
    void SetLastOnlineContactUtc(DateTime? lastOnlineContactUtc);
    void Clear();
}
