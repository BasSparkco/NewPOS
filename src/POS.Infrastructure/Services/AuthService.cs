using Microsoft.EntityFrameworkCore;
using POS.Application.Abstractions;
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

    public async Task<AuthResult> LoginAsync(string username, CancellationToken cancellationToken = default)
    {
        var name = username.Trim();
        if (string.IsNullOrEmpty(name))
            return new AuthResult(false, "Username is required.");

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        // SQLite default collation is case-sensitive; match case-insensitively.
        var nameLower = name.ToLowerInvariant();
        var user = await db.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(
                u => u.Username.ToLower() == nameLower && !u.IsDeleted,
                cancellationToken);

        if (user is null || !user.IsActive)
            return new AuthResult(false, "Invalid username.");

        var roleName = await db.Roles
            .AsNoTracking()
            .Where(r => r.Id == user.RoleId)
            .Select(r => r.Name)
            .FirstOrDefaultAsync(cancellationToken)
            ?? string.Empty;

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
        var symbol   = currency?.Symbol;
        _session.Set(user.Id, user.StoreId, user.Username, roleName, baseCode, symbol);

        return new AuthResult(true, null);
    }
}
