using POS.Application.Models;

namespace POS.Application.Abstractions;

/// <summary>Store-scoped CRUD for users and roles, backing the admin "Users" screen.</summary>
public interface IUserManagementService
{
    Task<IReadOnlyList<RoleDto>> GetRolesAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<UserListItemDto>> GetUsersAsync(CancellationToken cancellationToken = default);
    /// <summary>Creates a user with a randomly generated temporary password, returned so the caller can hand it to the new user out-of-band.</summary>
    Task<(bool Success, string? Error, string? GeneratedPassword)> CreateUserAsync(string username, Guid roleId, bool isActive, CancellationToken cancellationToken = default);
    Task<(bool Success, string? Error)> UpdateUserAsync(Guid userId, string username, Guid roleId, bool isActive, CancellationToken cancellationToken = default);
    /// <summary>Changes the signed-in user's own password after verifying <paramref name="currentPassword"/>. Available to any authenticated user, no permission required.</summary>
    Task<(bool Success, string? Error)> ChangeOwnPasswordAsync(string currentPassword, string newPassword, CancellationToken cancellationToken = default);
    /// <summary>Resets another user's password to a new randomly generated temporary one, returned so the caller can hand it to them out-of-band. Requires ManageUsers.</summary>
    Task<(bool Success, string? Error, string? GeneratedPassword)> ResetUserPasswordAsync(Guid userId, CancellationToken cancellationToken = default);
    /// <summary>Adds a new role ("user type") that can then be assigned to users.</summary>
    Task<(bool Success, string? Error, RoleDto? Role)> CreateRoleAsync(string roleName, int permissionsMask = 0, CancellationToken cancellationToken = default);
    /// <summary>Updates a role's granted permissions. The role named "Admin" always keeps <c>Permission.ManageUsers</c> to prevent a total lockout.</summary>
    Task<(bool Success, string? Error)> UpdateRolePermissionsAsync(Guid roleId, int permissionsMask, CancellationToken cancellationToken = default);
}
