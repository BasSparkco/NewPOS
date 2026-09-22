using POS.Application.Models;

namespace POS.Application.Abstractions;

/// <summary>
/// Tenant-scoped store (branch) administration and multi-store access — tenant.md Stage 4T/T5.
/// <see cref="UserStoreAccess"/> grants (plus a user's own home <see cref="POS.Core.Entities.User.StoreId"/>)
/// determine which stores a user may switch into; nothing here changes the active store of the current
/// request/session by itself — callers (e.g. a Web controller re-issuing the auth cookie) own that.
/// </summary>
public interface IStoreAccessService
{
    /// <summary>Every non-deleted store in the current session's tenant.</summary>
    Task<IReadOnlyList<StoreListItemDto>> GetTenantStoresAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Stores the current session's user may switch into: their home store (<c>User.StoreId</c>) plus
    /// any explicit <see cref="POS.Core.Entities.UserStoreAccess"/> grant, tenant-scoped.
    /// </summary>
    Task<IReadOnlyList<StoreListItemDto>> GetAccessibleStoresAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// True only if <paramref name="storeId"/> belongs to the current session's tenant AND the current
    /// session's user has access to it (home store or an explicit grant). Never trusts a client-supplied
    /// store id on its own — this is the check a store switch must pass before the active store changes.
    /// </summary>
    Task<bool> CanCurrentUserAccessStoreAsync(Guid storeId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Session-independent form of <see cref="CanCurrentUserAccessStoreAsync"/>, for callers that run before
    /// <see cref="ICurrentSession"/> is populated for the request — e.g. a cookie-authentication
    /// <c>OnValidatePrincipal</c> handler re-checking an already-issued cookie's claims on every request, so a
    /// mid-session <see cref="RevokeStoreAccessAsync"/> is enforced before the cookie's sliding expiration would
    /// otherwise let it re-authenticate. Never trusts <paramref name="tenantId"/> alone: the store must actually
    /// belong to that tenant.
    /// </summary>
    Task<bool> CanUserAccessStoreAsync(Guid tenantId, Guid userId, Guid storeId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a new store (branch) in the current session's tenant, inheriting the tenant's existing
    /// canonical base currency (tenant.md §3: one base currency per tenant catalog — a new store cannot
    /// pick its own). Requires <c>Permission.ManageStores</c> at the call site.
    /// </summary>
    Task<(bool Success, string? Error, StoreListItemDto? Store)> CreateStoreAsync(string name, string? address, string? phone, CancellationToken cancellationToken = default);

    /// <summary>Store access rows for every tenant user, against the given store — for a tenant admin's grant/revoke screen.</summary>
    Task<IReadOnlyList<StoreUserAccessRowDto>> GetStoreAccessRowsAsync(Guid storeId, CancellationToken cancellationToken = default);

    /// <summary>Grants a tenant user access to an additional store. Requires <c>Permission.ManageStores</c> at the call site.</summary>
    Task<(bool Success, string? Error)> GrantStoreAccessAsync(Guid userId, Guid storeId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Revokes a previously granted store access. Refuses to revoke a user's own home store (that
    /// requires reassigning their home store first, not this) so a user can never be left with zero
    /// accessible stores through this action alone.
    /// </summary>
    Task<(bool Success, string? Error)> RevokeStoreAccessAsync(Guid userId, Guid storeId, CancellationToken cancellationToken = default);
}
