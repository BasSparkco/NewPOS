using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace POS.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class UpgradeAdminRolesManageStores : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // T6 upgrade/bootstrap fixup: Permission.ManageStores (bit 64, 1 << 6) was added after
            // Permission.All had already shipped as bits 1|2|4|8|16|32 (= 63). An *existing* tenant's
            // Admin role therefore did not retroactively gain ManageStores — every prior permission-bit
            // addition in this codebase has had the same gap, previously fixed only via a manual re-save
            // through the WPF role editor (see STATUS.md's Stage 4T/T5 entry).
            //
            // Deliberately value-scoped, not name- or tenant-scoped: only a role whose PermissionsMask is
            // EXACTLY the pre-ManageStores "every other permission" value (63) is touched, across every
            // tenant. A role literally named "Admin" that was deliberately narrowed to fewer permissions
            // is left alone, and a custom role that never held every other permission is never touched —
            // this cannot expand any role that wasn't already functionally "full admin" under the old
            // permission set.
            migrationBuilder.Sql(@"UPDATE ""Roles"" SET ""PermissionsMask"" = ""PermissionsMask"" | 64 WHERE ""PermissionsMask"" = 63;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Best-effort inverse only: reverts a role sitting at exactly the fully-upgraded value (127)
            // back to 63. This cannot distinguish a role the Up step touched from one that independently
            // reached 127 some other way (e.g. a brand-new tenant's Admin role, created with Permission.All
            // from day one) — a known, accepted imprecision of scoped data-fixup migrations, same class as
            // the irreversible backfills in 20260921073017_AddTenantFoundation.
            migrationBuilder.Sql(@"UPDATE ""Roles"" SET ""PermissionsMask"" = ""PermissionsMask"" & ~64 WHERE ""PermissionsMask"" = 127;");
        }
    }
}
