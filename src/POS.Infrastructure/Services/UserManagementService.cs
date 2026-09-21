using Microsoft.EntityFrameworkCore;
using POS.Application.Abstractions;
using POS.Application.Models;
using POS.Application.Support;
using POS.Core.Entities;
using POS.Core.Enums;
using POS.Infrastructure.Data;

namespace POS.Infrastructure.Services;

internal sealed class UserManagementService : IUserManagementService
{
    private readonly IDbContextFactory<PosDbContext> _dbFactory;
    private readonly ICurrentSession _session;
    private readonly IAuditLogService _auditLogService;

    public UserManagementService(
        IDbContextFactory<PosDbContext> dbFactory,
        ICurrentSession session,
        IAuditLogService auditLogService)
    {
        _dbFactory = dbFactory;
        _session = session;
        _auditLogService = auditLogService;
    }

    public async Task<IReadOnlyList<RoleDto>> GetRolesAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var tenantId = await GetTenantIdAsync(db, cancellationToken);
        return await db.Roles
            .AsNoTracking()
            .Where(r => r.TenantId == tenantId && !r.IsDeleted)
            .OrderBy(r => r.Name)
            .Select(r => new RoleDto(r.Id, r.Name, r.PermissionsMask))
            .ToListAsync(cancellationToken);
    }

    private async Task<Guid> GetTenantIdAsync(PosDbContext db, CancellationToken cancellationToken) =>
        await db.Stores
            .AsNoTracking()
            .Where(s => s.Id == _session.StoreId && !s.IsDeleted)
            .Select(s => (Guid?)s.TenantId)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException("Store not found.");

    public async Task<IReadOnlyList<UserListItemDto>> GetUsersAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var storeId = _session.StoreId;
        var currentUserId = _session.UserId;

        var rows = await db.Users
            .AsNoTracking()
            .Where(u => u.StoreId == storeId && !u.IsDeleted)
            .Join(
                db.Roles.AsNoTracking().Where(r => !r.IsDeleted),
                u => u.RoleId,
                r => r.Id,
                (u, r) => new UserListItemDto(u.Id, u.Username, r.Id, r.Name, u.IsActive, u.Id == currentUserId))
            .ToListAsync(cancellationToken);

        return rows
            .OrderByDescending(u => u.IsCurrentUser)
            .ThenBy(u => u.Username)
            .ToList();
    }

    public async Task<(bool Success, string? Error, string? GeneratedPassword)> CreateUserAsync(string username, Guid roleId, bool isActive, CancellationToken cancellationToken = default)
    {
        username = (username ?? string.Empty).Trim();
        if (username.Length == 0)
            return (false, "Username is required.", null);

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var storeId = _session.StoreId;

        var role = await db.Roles.AsNoTracking().FirstOrDefaultAsync(r => r.Id == roleId && !r.IsDeleted, cancellationToken);
        if (role is null)
            return (false, "Selected role was not found.", null);

        var normalized = username.ToLowerInvariant();
        var exists = await db.Users.AnyAsync(
            u => u.StoreId == storeId && !u.IsDeleted && u.Username.ToLower() == normalized,
            cancellationToken);
        if (exists)
            return (false, $"User '{username}' already exists.", null);

        var tenantId = await GetTenantIdAsync(db, cancellationToken);
        var generatedPassword = RandomPasswordGenerator.Generate();
        var now = DateTime.UtcNow;
        var user = new User
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Username = username,
            NormalizedUsername = normalized.ToUpperInvariant(),
            PasswordHash = PasswordHasher.Hash(generatedPassword),
            RoleId = role.Id,
            StoreId = storeId,
            IsActive = isActive,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.Users.Add(user);
        db.UserStoreAccesses.Add(new UserStoreAccess
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            UserId = user.Id,
            StoreId = storeId,
            CreatedAt = now,
            UpdatedAt = now
        });
        await db.SaveChangesAsync(cancellationToken);

        await TryWriteAuditAsync(
            "UserCreated",
            nameof(User),
            user.Id,
            $"Username={user.Username}; Role={role.Name}; Active={user.IsActive}",
            cancellationToken);

        return (true, null, generatedPassword);
    }

    public async Task<(bool Success, string? Error)> UpdateUserAsync(Guid userId, string username, Guid roleId, bool isActive, CancellationToken cancellationToken = default)
    {
        username = (username ?? string.Empty).Trim();
        if (username.Length == 0)
            return (false, "Username is required.");

        if (!isActive && userId == _session.UserId)
            return (false, "You cannot deactivate the account used for the current session.");

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var storeId = _session.StoreId;

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId && u.StoreId == storeId && !u.IsDeleted, cancellationToken);
        if (user is null)
            return (false, "User not found.");

        var role = await db.Roles.AsNoTracking().FirstOrDefaultAsync(r => r.Id == roleId && !r.IsDeleted, cancellationToken);
        if (role is null)
            return (false, "Selected role was not found.");

        var normalized = username.ToLowerInvariant();
        var duplicate = await db.Users.AnyAsync(
            u => u.StoreId == storeId && u.Id != userId && !u.IsDeleted && u.Username.ToLower() == normalized,
            cancellationToken);
        if (duplicate)
            return (false, $"Another user already uses '{username}'.");

        var oldDetails = $"Username={user.Username}; RoleId={user.RoleId}; Active={user.IsActive}";

        user.Username = username;
        user.NormalizedUsername = normalized.ToUpperInvariant();
        user.RoleId = role.Id;
        user.IsActive = isActive;
        user.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);

        await TryWriteAuditAsync(
            "UserUpdated",
            nameof(User),
            user.Id,
            $"{oldDetails} -> Username={user.Username}; Role={role.Name}; Active={user.IsActive}",
            cancellationToken);

        return (true, null);
    }

    public async Task<(bool Success, string? Error)> ChangeOwnPasswordAsync(string currentPassword, string newPassword, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(currentPassword) || string.IsNullOrWhiteSpace(newPassword))
            return (false, "Current and new password are required.");

        if (newPassword.Length < 8)
            return (false, "New password must be at least 8 characters.");

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == _session.UserId && !u.IsDeleted, cancellationToken);
        if (user is null)
            return (false, "User not found.");

        if (!PasswordHasher.Verify(currentPassword, user.PasswordHash))
            return (false, "Current password is incorrect.");

        user.PasswordHash = PasswordHasher.Hash(newPassword);
        user.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);

        // Keep the in-memory credential the sync worker re-authenticates with (ICurrentSession.Password) in
        // step with the new password, so background sync doesn't keep retrying the now-stale one until next login.
        _session.SetPassword(newPassword);

        await TryWriteAuditAsync("UserPasswordChanged", nameof(User), user.Id, $"Username={user.Username}", cancellationToken);

        return (true, null);
    }

    public async Task<(bool Success, string? Error, string? GeneratedPassword)> ResetUserPasswordAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var storeId = _session.StoreId;

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId && u.StoreId == storeId && !u.IsDeleted, cancellationToken);
        if (user is null)
            return (false, "User not found.", null);

        var generatedPassword = RandomPasswordGenerator.Generate();
        user.PasswordHash = PasswordHasher.Hash(generatedPassword);
        user.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);

        await TryWriteAuditAsync("UserPasswordReset", nameof(User), user.Id, $"Username={user.Username}", cancellationToken);

        return (true, null, generatedPassword);
    }

    public async Task<(bool Success, string? Error, RoleDto? Role)> CreateRoleAsync(string roleName, int permissionsMask = 0, CancellationToken cancellationToken = default)
    {
        roleName = (roleName ?? string.Empty).Trim();
        if (roleName.Length == 0)
            return (false, "Role name is required.", null);

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var tenantId = await GetTenantIdAsync(db, cancellationToken);

        var normalized = roleName.ToLowerInvariant();
        var exists = await db.Roles.AnyAsync(r => r.TenantId == tenantId && !r.IsDeleted && r.Name.ToLower() == normalized, cancellationToken);
        if (exists)
            return (false, $"Role '{roleName}' already exists.", null);

        var now = DateTime.UtcNow;
        var role = new Role
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Name = roleName,
            PermissionsMask = permissionsMask,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.Roles.Add(role);
        await db.SaveChangesAsync(cancellationToken);

        await TryWriteAuditAsync("RoleCreated", nameof(Role), role.Id, $"Name={role.Name}; Permissions={(Permission)role.PermissionsMask}", cancellationToken);

        return (true, null, new RoleDto(role.Id, role.Name, role.PermissionsMask));
    }

    public async Task<(bool Success, string? Error)> UpdateRolePermissionsAsync(Guid roleId, int permissionsMask, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        var role = await db.Roles.FirstOrDefaultAsync(r => r.Id == roleId && !r.IsDeleted, cancellationToken);
        if (role is null)
            return (false, "Role not found.");

        // The Admin role always keeps ManageUsers so nobody can lock every account out of the Users screen.
        if (string.Equals(role.Name, "Admin", StringComparison.OrdinalIgnoreCase))
            permissionsMask |= (int)Permission.ManageUsers;

        if (role.PermissionsMask == permissionsMask)
            return (true, null);

        var oldMask = role.PermissionsMask;
        role.PermissionsMask = permissionsMask;
        role.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);

        await TryWriteAuditAsync(
            "RolePermissionsUpdated",
            nameof(Role),
            role.Id,
            $"Name={role.Name}; Permissions={(Permission)oldMask} -> {(Permission)role.PermissionsMask}",
            cancellationToken);

        return (true, null);
    }

    private async Task TryWriteAuditAsync(string action, string entityName, Guid? entityId, string? details, CancellationToken cancellationToken)
    {
        try
        {
            await _auditLogService.WriteAsync(action, entityName, entityId, details, cancellationToken);
        }
        catch
        {
            // Audit logging is best-effort and must not block the business write.
        }
    }
}
