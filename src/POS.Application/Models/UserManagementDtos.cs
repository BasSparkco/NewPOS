namespace POS.Application.Models;

public sealed record RoleDto(Guid Id, string Name, int PermissionsMask);

public sealed record UserListItemDto(Guid Id, string Username, Guid RoleId, string RoleName, bool IsActive, bool IsCurrentUser);
