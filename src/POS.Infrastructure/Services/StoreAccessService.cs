using Microsoft.EntityFrameworkCore;
using POS.Application.Abstractions;
using POS.Application.Models;
using POS.Core.Entities;
using POS.Infrastructure.Data;

namespace POS.Infrastructure.Services;

internal sealed class StoreAccessService : IStoreAccessService
{
    private readonly IDbContextFactory<PosDbContext> _dbFactory;
    private readonly ICurrentSession _session;
    private readonly IAuditLogService _auditLogService;

    public StoreAccessService(IDbContextFactory<PosDbContext> dbFactory, ICurrentSession session, IAuditLogService auditLogService)
    {
        _dbFactory = dbFactory;
        _session = session;
        _auditLogService = auditLogService;
    }

    public async Task<IReadOnlyList<StoreListItemDto>> GetTenantStoresAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var tenantId = await GetCurrentTenantIdAsync(db, cancellationToken);

        return await ProjectStores(db.Stores.AsNoTracking().Where(s => s.TenantId == tenantId && !s.IsDeleted).OrderBy(s => s.Name))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<StoreListItemDto>> GetAccessibleStoresAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var userId = _session.UserId;
        var tenantId = await GetCurrentTenantIdAsync(db, cancellationToken);

        var grantedStoreIds = await db.UserStoreAccesses
            .AsNoTracking()
            .Where(a => a.TenantId == tenantId && a.UserId == userId && !a.IsDeleted)
            .Select(a => a.StoreId)
            .ToListAsync(cancellationToken);

        var homeStoreId = await db.Users
            .AsNoTracking()
            .Where(u => u.Id == userId && u.TenantId == tenantId && !u.IsDeleted)
            .Select(u => (Guid?)u.StoreId)
            .FirstOrDefaultAsync(cancellationToken);

        var accessibleIds = grantedStoreIds.ToHashSet();
        if (homeStoreId is { } home)
            accessibleIds.Add(home);

        return await ProjectStores(db.Stores.AsNoTracking().Where(s => s.TenantId == tenantId && !s.IsDeleted && accessibleIds.Contains(s.Id)).OrderBy(s => s.Name))
            .ToListAsync(cancellationToken);
    }

    public async Task<bool> CanCurrentUserAccessStoreAsync(Guid storeId, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var tenantId = await GetCurrentTenantIdAsync(db, cancellationToken);
        return await CanAccessStoreCoreAsync(db, tenantId, _session.UserId, storeId, cancellationToken);
    }

    public async Task<bool> CanUserAccessStoreAsync(Guid tenantId, Guid userId, Guid storeId, CancellationToken cancellationToken = default)
    {
        if (tenantId == Guid.Empty || userId == Guid.Empty)
            return false;

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        return await CanAccessStoreCoreAsync(db, tenantId, userId, storeId, cancellationToken);
    }

    private static async Task<bool> CanAccessStoreCoreAsync(PosDbContext db, Guid tenantId, Guid userId, Guid storeId, CancellationToken cancellationToken)
    {
        if (storeId == Guid.Empty)
            return false;

        var storeInTenant = await db.Stores.AsNoTracking().AnyAsync(s => s.Id == storeId && s.TenantId == tenantId && !s.IsDeleted, cancellationToken);
        if (!storeInTenant)
            return false;

        var isHomeStore = await db.Users.AsNoTracking().AnyAsync(u => u.Id == userId && u.TenantId == tenantId && u.StoreId == storeId && !u.IsDeleted, cancellationToken);
        if (isHomeStore)
            return true;

        return await db.UserStoreAccesses.AsNoTracking().AnyAsync(
            a => a.TenantId == tenantId && a.UserId == userId && a.StoreId == storeId && !a.IsDeleted,
            cancellationToken);
    }

    public async Task<(bool Success, string? Error, StoreListItemDto? Store)> CreateStoreAsync(string name, string? address, string? phone, CancellationToken cancellationToken = default)
    {
        name = (name ?? string.Empty).Trim();
        if (name.Length == 0)
            return (false, "Store name is required.", null);

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var tenantId = await GetCurrentTenantIdAsync(db, cancellationToken);

        // tenant.md §3: one canonical catalog/base currency per tenant — a new branch inherits the
        // tenant's existing base currency rather than picking its own, so shared product prices stay
        // meaningful across every store in the tenant. Any existing store gives the answer since every
        // store in a tenant is required to already share one.
        var baseCurrencyId = await db.Stores
            .AsNoTracking()
            .Where(s => s.TenantId == tenantId && !s.IsDeleted)
            .Select(s => (Guid?)s.BaseCurrencyId)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException("Tenant has no existing store to inherit a base currency from.");

        var duplicate = await db.Stores.AnyAsync(
            s => s.TenantId == tenantId && !s.IsDeleted && s.Name.ToLower() == name.ToLower(),
            cancellationToken);
        if (duplicate)
            return (false, $"A store named '{name}' already exists.", null);

        var now = DateTime.UtcNow;
        var store = new Store
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Name = name,
            Address = NormalizeOptional(address),
            Phone = NormalizeOptional(phone),
            BaseCurrencyId = baseCurrencyId,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.Stores.Add(store);
        await db.SaveChangesAsync(cancellationToken);

        await TryWriteAuditAsync("StoreCreated", nameof(Store), store.Id, $"Name={store.Name}", cancellationToken);

        var currencyCode = await db.Currencies.AsNoTracking().Where(c => c.Id == baseCurrencyId).Select(c => c.Code).FirstOrDefaultAsync(cancellationToken) ?? string.Empty;
        return (true, null, new StoreListItemDto(store.Id, store.Name, store.Address, store.Phone, currencyCode, IsActiveSelection: false));
    }

    public async Task<IReadOnlyList<StoreUserAccessRowDto>> GetStoreAccessRowsAsync(Guid storeId, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var tenantId = await GetCurrentTenantIdAsync(db, cancellationToken);

        var storeInTenant = await db.Stores.AsNoTracking().AnyAsync(s => s.Id == storeId && s.TenantId == tenantId && !s.IsDeleted, cancellationToken);
        if (!storeInTenant)
            return Array.Empty<StoreUserAccessRowDto>();

        var grantedUserIds = await db.UserStoreAccesses
            .AsNoTracking()
            .Where(a => a.TenantId == tenantId && a.StoreId == storeId && !a.IsDeleted)
            .Select(a => a.UserId)
            .ToListAsync(cancellationToken);
        var grantedSet = grantedUserIds.ToHashSet();

        var users = await db.Users
            .AsNoTracking()
            .Where(u => u.TenantId == tenantId && !u.IsDeleted)
            .OrderBy(u => u.Username)
            .Select(u => new { u.Id, u.Username, u.StoreId })
            .ToListAsync(cancellationToken);

        return users
            .Select(u => new StoreUserAccessRowDto(
                u.Id,
                u.Username,
                HasAccess: u.StoreId == storeId || grantedSet.Contains(u.Id),
                IsHomeStore: u.StoreId == storeId))
            .ToList();
    }

    public async Task<(bool Success, string? Error)> GrantStoreAccessAsync(Guid userId, Guid storeId, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var tenantId = await GetCurrentTenantIdAsync(db, cancellationToken);

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId && u.TenantId == tenantId && !u.IsDeleted, cancellationToken);
        if (user is null)
            return (false, "User not found.");

        var store = await db.Stores.AsNoTracking().FirstOrDefaultAsync(s => s.Id == storeId && s.TenantId == tenantId && !s.IsDeleted, cancellationToken);
        if (store is null)
            return (false, "Store not found.");

        var existing = await db.UserStoreAccesses.FirstOrDefaultAsync(
            a => a.TenantId == tenantId && a.UserId == userId && a.StoreId == storeId,
            cancellationToken);

        var now = DateTime.UtcNow;
        if (existing is not null)
        {
            if (!existing.IsDeleted)
                return (true, null);

            existing.IsDeleted = false;
            existing.UpdatedAt = now;
        }
        else
        {
            db.UserStoreAccesses.Add(new UserStoreAccess
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                UserId = userId,
                StoreId = storeId,
                CreatedAt = now,
                UpdatedAt = now
            });
        }

        await db.SaveChangesAsync(cancellationToken);
        await TryWriteAuditAsync("StoreAccessGranted", nameof(UserStoreAccess), userId, $"User={user.Username}; Store={store.Name}", cancellationToken);
        return (true, null);
    }

    public async Task<(bool Success, string? Error)> RevokeStoreAccessAsync(Guid userId, Guid storeId, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var tenantId = await GetCurrentTenantIdAsync(db, cancellationToken);

        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId && u.TenantId == tenantId && !u.IsDeleted, cancellationToken);
        if (user is null)
            return (false, "User not found.");

        if (user.StoreId == storeId)
            return (false, "Cannot revoke access to a user's home store. Reassign their home store first.");

        var existing = await db.UserStoreAccesses.FirstOrDefaultAsync(
            a => a.TenantId == tenantId && a.UserId == userId && a.StoreId == storeId && !a.IsDeleted,
            cancellationToken);
        if (existing is null)
            return (true, null);

        existing.IsDeleted = true;
        existing.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);

        var storeName = await db.Stores.AsNoTracking().Where(s => s.Id == storeId).Select(s => s.Name).FirstOrDefaultAsync(cancellationToken) ?? storeId.ToString();
        await TryWriteAuditAsync("StoreAccessRevoked", nameof(UserStoreAccess), userId, $"User={user.Username}; Store={storeName}", cancellationToken);
        return (true, null);
    }

    private async Task<Guid> GetCurrentTenantIdAsync(PosDbContext db, CancellationToken cancellationToken) =>
        await db.Stores
            .AsNoTracking()
            .Where(s => s.Id == _session.StoreId && !s.IsDeleted)
            .Select(s => (Guid?)s.TenantId)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException("Store not found.");

    private static IQueryable<StoreListItemDto> ProjectStores(IQueryable<Store> query) =>
        query.Select(s => new StoreListItemDto(s.Id, s.Name, s.Address, s.Phone, s.BaseCurrency!.Code, false));

    private static string? NormalizeOptional(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }

    private async Task TryWriteAuditAsync(string action, string entityName, Guid? entityId, string? details, CancellationToken cancellationToken)
    {
        try
        {
            await _auditLogService.WriteAsync(action, entityName, entityId, details, cancellationToken);
        }
        catch
        {
            // Best-effort logging only, matching the existing pattern in ManagementController/other services.
        }
    }
}
