using Microsoft.EntityFrameworkCore;
using POS.Application.Abstractions;
using POS.Core.Enums;
using POS.Infrastructure.Data;

namespace POS.Infrastructure.Services;

internal sealed class AuthService : IAuthService
{
    private readonly IDbContextFactory<PosDbContext> _dbFactory;
    private readonly ICurrentSession _session;

    public AuthService(IDbContextFactory<PosDbContext> dbFactory, ICurrentSession session)
    {
        _dbFactory = dbFactory;
        _session = session;
    }

    public async Task<AuthResult> LoginAsync(string username, string password, string? tenantSlug = null, CancellationToken cancellationToken = default)
    {
        var name = (username ?? string.Empty).Trim();
        const string genericFailure = "Invalid username or password.";
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(password))
            return new AuthResult(false, genericFailure);

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        var tenantId = await ResolveTenantIdAsync(db, tenantSlug, cancellationToken);
        if (tenantId is null)
            return new AuthResult(false, genericFailure);

        // SQLite default collation is case-sensitive; match case-insensitively.
        var nameLower = name.ToLowerInvariant();
        var user = await db.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(
                u => u.TenantId == tenantId.Value && u.Username.ToLower() == nameLower && !u.IsDeleted,
                cancellationToken);

        // Same generic message whether the tenant/username doesn't exist, the account is inactive, or
        // the password is wrong — do not let a login failure reveal which of those it was.
        if (user is null || !user.IsActive || !PasswordHasher.Verify(password, user.PasswordHash))
            return new AuthResult(false, genericFailure);

        var role = await db.Roles
            .AsNoTracking()
            .Where(r => r.Id == user.RoleId)
            .Select(r => new { r.Name, r.PermissionsMask })
            .FirstOrDefaultAsync(cancellationToken);
        var roleName = role?.Name ?? string.Empty;
        var permissionsMask = role?.PermissionsMask ?? 0;

        var storeCurrencyId = await db.Stores
            .AsNoTracking()
            .Where(s => s.Id == user.StoreId && !s.IsDeleted)
            .Select(s => (Guid?)s.BaseCurrencyId)
            .FirstOrDefaultAsync(cancellationToken);

        var currency = storeCurrencyId is null
            ? null
            : await db.Currencies
                .AsNoTracking()
                .Where(c => c.Id == storeCurrencyId && !c.IsDeleted)
                .Select(c => new { c.Code, c.Symbol })
                .FirstOrDefaultAsync(cancellationToken);

        var baseCode = currency?.Code ?? "ILS";
        var symbol   = string.IsNullOrWhiteSpace(currency?.Symbol) && baseCode == "ILS" ? "₪" : currency?.Symbol;
        _session.Set(user.TenantId, user.Id, user.StoreId, user.Username, roleName, permissionsMask, baseCode, symbol);
        _session.SetPassword(password);

        return new AuthResult(true, null);
    }

    /// <summary>
    /// Resolves the login's tenant scope. An explicit slug must match an active tenant. With no slug,
    /// auto-resolution only succeeds when exactly one active tenant exists — true for every desktop
    /// SQLite profile and today's single-bootstrap-tenant server — so a second tenant can never be
    /// silently guessed.
    /// </summary>
    private static async Task<Guid?> ResolveTenantIdAsync(PosDbContext db, string? tenantSlug, CancellationToken cancellationToken)
    {
        var slug = (tenantSlug ?? string.Empty).Trim().ToLowerInvariant();

        if (slug.Length > 0)
        {
            var tenant = await db.Tenants
                .AsNoTracking()
                .FirstOrDefaultAsync(t => t.NormalizedSlug == slug && !t.IsDeleted, cancellationToken);
            return tenant is not null && tenant.Status == TenantStatus.Active ? tenant.Id : null;
        }

        var candidates = await db.Tenants
            .AsNoTracking()
            .Where(t => !t.IsDeleted)
            .Select(t => new { t.Id, t.Status })
            .Take(2)
            .ToListAsync(cancellationToken);

        return candidates.Count == 1 && candidates[0].Status == TenantStatus.Active ? candidates[0].Id : null;
    }
}
