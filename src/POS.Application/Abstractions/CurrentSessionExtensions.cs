using POS.Core.Enums;

namespace POS.Application.Abstractions;

public static class CurrentSessionExtensions
{
    public static bool HasPermission(this ICurrentSession session, Permission permission) =>
        ((Permission)session.PermissionsMask).HasFlag(permission);
}
