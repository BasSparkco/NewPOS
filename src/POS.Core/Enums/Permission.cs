namespace POS.Core.Enums;

/// <summary>Granular capabilities a <see cref="Entities.Role"/> can be granted, stored as a bitmask on <see cref="Entities.Role.PermissionsMask"/>.</summary>
[Flags]
public enum Permission
{
    None           = 0,
    ManageProducts = 1 << 0,
    ViewReports    = 1 << 1,
    ViewAudit      = 1 << 2,
    ManageUsers    = 1 << 3,
    ManageSettings = 1 << 4,
    ProcessRefunds = 1 << 5,
    All = ManageProducts | ViewReports | ViewAudit | ManageUsers | ManageSettings | ProcessRefunds
}
