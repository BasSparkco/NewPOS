using System;
using System.Globalization;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace POS.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTenantFoundation : Migration
    {
        // Random values are safe here: Up() only ever runs once per database, and both ids only
        // need to be internally consistent within this one migration's SQL — never referenced again.
        private static readonly Guid BootstrapTenantId = Guid.NewGuid();

        // Fixed rather than random: used only as a shared placeholder for legacy invoices that
        // somehow have no DeviceId at all, so a memorable/greppable value is more useful than
        // another random one.
        private static readonly Guid FallbackDeviceId = Guid.Parse("00000000-0000-0000-0000-000000000001");

        // Microsoft.Data.Sqlite binds Guid parameters (e.g. from any later EF-generated INSERT/
        // UPDATE that references these ids, such as DatabaseSeeder creating the first Roles/Users
        // for this tenant) as UPPERCASE text, while Guid.ToString() — and therefore any naive string
        // interpolation of a Guid into raw SQL — produces lowercase text. SQLite's default TEXT
        // comparison is byte-exact, so a lowercase literal here would silently fail every future FK
        // check against it. Both literals below must stay uppercase for that reason.
        private static readonly string BootstrapTenantIdText = BootstrapTenantId.ToString("D").ToUpperInvariant();
        private static readonly string FallbackDeviceIdText = FallbackDeviceId.ToString("D").ToUpperInvariant();

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            var ts = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);

            // EF Core's SQLite provider does not support AlterColumnOperation at all (confirmed via
            // `dotnet ef migrations script`, which throws NotSupportedException the moment such an
            // operation is generated for Sqlite) — there is no automatic table-rebuild fallback for
            // it the way there is for some other operations. Every column below is therefore added
            // as NOT NULL directly, using a single uniform default value that is correct for every
            // row that already exists: the repository audit (docs/TENANT_T0_AUDIT.md) confirmed the
            // current database is demonstrably single-business (exactly one Store, with everything
            // else already keying off it), so one bootstrap tenant unambiguously owns every existing
            // row. AuditLogs.StoreId and Invoices.DeviceId keep their pre-migration nullability at
            // the database level for the same reason — narrowing/widening either would require an
            // AlterColumn this provider cannot run; both are enforced at the application/entity
            // level instead (AuditLog.StoreId is Guid?, Invoice.DeviceId is a required Guid that
            // every write path already populates, with the one-time backfill below covering any
            // legacy row that predates DeviceId existing at all).

            migrationBuilder.CreateTable(
                name: "Tenants",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    NormalizedSlug = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    IsDeleted = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tenants", x => x.Id);
                });

            migrationBuilder.Sql(
                $@"INSERT INTO ""Tenants"" (""Id"", ""Name"", ""NormalizedSlug"", ""Status"", ""CreatedAt"", ""UpdatedAt"", ""IsDeleted"")
VALUES ('{BootstrapTenantIdText}', 'Default Business', 'default', 0, '{ts}', '{ts}', 0);");

            // Currencies.Id was originally seeded by AddCurrenciesAndStorePolicy using lowercase text
            // literals. That was harmless while Store.BaseCurrencyId -> Currencies.Id had no real DB
            // constraint (SQLite couldn't add one to an existing table back then — see that
            // migration's comment). This migration's AddForeignKey calls force a rebuild of several
            // tables including Stores, which — because SQLite rebuilds recreate the table from the
            // full current model, not just the delta — ends up creating that FK constraint for the
            // first time. Since Microsoft.Data.Sqlite binds Guid parameters as UPPERCASE text (the
            // same quirk documented on BootstrapTenantIdText above), any future EF-written
            // Store.BaseCurrencyId would otherwise never match the lowercase Currencies.Id. Normalize
            // it once, here, before anything else copies or references Currencies.Id.
            migrationBuilder.Sql(@"UPDATE ""Currencies"" SET ""Id"" = UPPER(""Id"");");

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "Stores",
                type: "TEXT",
                nullable: false,
                defaultValue: BootstrapTenantId);

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "Users",
                type: "TEXT",
                nullable: false,
                defaultValue: BootstrapTenantId);

            migrationBuilder.AddColumn<string>(
                name: "NormalizedUsername",
                table: "Users",
                type: "TEXT",
                maxLength: 100,
                nullable: false,
                defaultValue: "");

            migrationBuilder.Sql(@"UPDATE ""Users"" SET ""NormalizedUsername"" = UPPER(""Username"");");

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "Roles",
                type: "TEXT",
                nullable: false,
                defaultValue: BootstrapTenantId);

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "Products",
                type: "TEXT",
                nullable: false,
                defaultValue: BootstrapTenantId);

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "Categories",
                type: "TEXT",
                nullable: false,
                defaultValue: BootstrapTenantId);

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "Devices",
                type: "TEXT",
                nullable: false,
                defaultValue: BootstrapTenantId);

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "Inventories",
                type: "TEXT",
                nullable: false,
                defaultValue: BootstrapTenantId);

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "StockMovements",
                type: "TEXT",
                nullable: false,
                defaultValue: BootstrapTenantId);

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "Settings",
                type: "TEXT",
                nullable: false,
                defaultValue: BootstrapTenantId);

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "AuditLogs",
                type: "TEXT",
                nullable: false,
                defaultValue: BootstrapTenantId);

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "Invoices",
                type: "TEXT",
                nullable: false,
                defaultValue: BootstrapTenantId);

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "SyncChanges",
                type: "TEXT",
                nullable: false,
                defaultValue: BootstrapTenantId);

            migrationBuilder.CreateTable(
                name: "TenantCurrencyRates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CurrencyId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ExchangeRate = table.Column<decimal>(type: "TEXT", precision: 18, scale: 6, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    IsDeleted = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantCurrencyRates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TenantCurrencyRates_Currencies_CurrencyId",
                        column: x => x.CurrencyId,
                        principalTable: "Currencies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TenantCurrencyRates_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            // Move each currency's existing rate into the new tenant-scoped rate table before the
            // old Currencies.ExchangeRate column is dropped, so no rate data is lost. Reusing
            // Currency.Id as the new row's Id is safe: only one tenant exists yet, so each currency
            // needs exactly one TenantCurrencyRate row and collisions are impossible.
            migrationBuilder.Sql(
                $@"INSERT INTO ""TenantCurrencyRates"" (""Id"", ""TenantId"", ""CurrencyId"", ""ExchangeRate"", ""CreatedAt"", ""UpdatedAt"", ""IsDeleted"")
SELECT ""Id"", '{BootstrapTenantIdText}', ""Id"", ""ExchangeRate"", ""CreatedAt"", ""UpdatedAt"", ""IsDeleted""
FROM ""Currencies"";");

            migrationBuilder.DropColumn(
                name: "ExchangeRate",
                table: "Currencies");

            migrationBuilder.CreateTable(
                name: "UserStoreAccesses",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    StoreId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    IsDeleted = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserStoreAccesses", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UserStoreAccesses_Stores_StoreId",
                        column: x => x.StoreId,
                        principalTable: "Stores",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_UserStoreAccesses_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_UserStoreAccesses_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            // Every existing user's current StoreId becomes their first explicit store grant.
            // Reusing User.Id as the new row's Id is safe for the same reason as TenantCurrencyRates
            // above: exactly one grant is created per user here, so there is nothing to collide with.
            migrationBuilder.Sql(
                $@"INSERT INTO ""UserStoreAccesses"" (""Id"", ""TenantId"", ""UserId"", ""StoreId"", ""CreatedAt"", ""UpdatedAt"", ""IsDeleted"")
SELECT ""Id"", '{BootstrapTenantIdText}', ""Id"", ""StoreId"", '{ts}', '{ts}', 0
FROM ""Users"";");

            // Invoice.DeviceId is required at the application/entity level from here on (every sale
            // is keyed by tenant + branch + box + user). In practice SaleService has always stamped
            // a real device on every invoice it creates, so this only matters for legacy rows synced
            // in before DeviceId existed. Give those a shared placeholder "Box" so no invoice is left
            // unattributed, even though the DB column itself stays nullable (see note above Up()).
            migrationBuilder.Sql(
                $@"INSERT INTO ""Devices"" (""Id"", ""TenantId"", ""StoreId"", ""Name"", ""SyncVersion"", ""CreatedAt"", ""UpdatedAt"", ""IsDeleted"")
SELECT '{FallbackDeviceIdText}', '{BootstrapTenantIdText}', ""Id"", 'Unknown Device (migrated)', 1, '{ts}', '{ts}', 0
FROM ""Stores""
WHERE EXISTS (SELECT 1 FROM ""Invoices"" WHERE ""DeviceId"" IS NULL)
LIMIT 1;");

            migrationBuilder.Sql(
                $@"UPDATE ""Invoices"" SET ""DeviceId"" = '{FallbackDeviceIdText}' WHERE ""DeviceId"" IS NULL;");

            migrationBuilder.DropIndex(
                name: "IX_Users_Username",
                table: "Users");

            migrationBuilder.DropIndex(
                name: "IX_Roles_Name",
                table: "Roles");

            migrationBuilder.DropIndex(
                name: "IX_Inventories_ProductId_StoreId",
                table: "Inventories");

            migrationBuilder.CreateIndex(
                name: "IX_Users_TenantId_NormalizedUsername",
                table: "Users",
                columns: new[] { "TenantId", "NormalizedUsername" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SyncChanges_TenantId_AggregateType_Id",
                table: "SyncChanges",
                columns: new[] { "TenantId", "AggregateType", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_Stores_TenantId",
                table: "Stores",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_StockMovements_TenantId",
                table: "StockMovements",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_Settings_TenantId",
                table: "Settings",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_Roles_TenantId_Name",
                table: "Roles",
                columns: new[] { "TenantId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Products_TenantId",
                table: "Products",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_Invoices_TenantId",
                table: "Invoices",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_Inventories_ProductId",
                table: "Inventories",
                column: "ProductId");

            migrationBuilder.CreateIndex(
                name: "IX_Inventories_TenantId_StoreId_ProductId",
                table: "Inventories",
                columns: new[] { "TenantId", "StoreId", "ProductId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Devices_TenantId",
                table: "Devices",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_Categories_TenantId",
                table: "Categories",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_TenantId_CreatedAt",
                table: "AuditLogs",
                columns: new[] { "TenantId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_TenantCurrencyRates_CurrencyId",
                table: "TenantCurrencyRates",
                column: "CurrencyId");

            migrationBuilder.CreateIndex(
                name: "IX_TenantCurrencyRates_TenantId_CurrencyId",
                table: "TenantCurrencyRates",
                columns: new[] { "TenantId", "CurrencyId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Tenants_NormalizedSlug",
                table: "Tenants",
                column: "NormalizedSlug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserStoreAccesses_StoreId",
                table: "UserStoreAccesses",
                column: "StoreId");

            migrationBuilder.CreateIndex(
                name: "IX_UserStoreAccesses_TenantId_UserId_StoreId",
                table: "UserStoreAccesses",
                columns: new[] { "TenantId", "UserId", "StoreId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserStoreAccesses_UserId",
                table: "UserStoreAccesses",
                column: "UserId");

            migrationBuilder.AddForeignKey(
                name: "FK_AuditLogs_Tenants_TenantId",
                table: "AuditLogs",
                column: "TenantId",
                principalTable: "Tenants",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Categories_Tenants_TenantId",
                table: "Categories",
                column: "TenantId",
                principalTable: "Tenants",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Devices_Tenants_TenantId",
                table: "Devices",
                column: "TenantId",
                principalTable: "Tenants",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Inventories_Tenants_TenantId",
                table: "Inventories",
                column: "TenantId",
                principalTable: "Tenants",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Invoices_Tenants_TenantId",
                table: "Invoices",
                column: "TenantId",
                principalTable: "Tenants",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Products_Tenants_TenantId",
                table: "Products",
                column: "TenantId",
                principalTable: "Tenants",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Roles_Tenants_TenantId",
                table: "Roles",
                column: "TenantId",
                principalTable: "Tenants",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Settings_Tenants_TenantId",
                table: "Settings",
                column: "TenantId",
                principalTable: "Tenants",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_StockMovements_Tenants_TenantId",
                table: "StockMovements",
                column: "TenantId",
                principalTable: "Tenants",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Stores_Tenants_TenantId",
                table: "Stores",
                column: "TenantId",
                principalTable: "Tenants",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_SyncChanges_Tenants_TenantId",
                table: "SyncChanges",
                column: "TenantId",
                principalTable: "Tenants",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Users_Tenants_TenantId",
                table: "Users",
                column: "TenantId",
                principalTable: "Tenants",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AuditLogs_Tenants_TenantId",
                table: "AuditLogs");

            migrationBuilder.DropForeignKey(
                name: "FK_Categories_Tenants_TenantId",
                table: "Categories");

            migrationBuilder.DropForeignKey(
                name: "FK_Devices_Tenants_TenantId",
                table: "Devices");

            migrationBuilder.DropForeignKey(
                name: "FK_Inventories_Tenants_TenantId",
                table: "Inventories");

            migrationBuilder.DropForeignKey(
                name: "FK_Invoices_Tenants_TenantId",
                table: "Invoices");

            migrationBuilder.DropForeignKey(
                name: "FK_Products_Tenants_TenantId",
                table: "Products");

            migrationBuilder.DropForeignKey(
                name: "FK_Roles_Tenants_TenantId",
                table: "Roles");

            migrationBuilder.DropForeignKey(
                name: "FK_Settings_Tenants_TenantId",
                table: "Settings");

            migrationBuilder.DropForeignKey(
                name: "FK_StockMovements_Tenants_TenantId",
                table: "StockMovements");

            migrationBuilder.DropForeignKey(
                name: "FK_Stores_Tenants_TenantId",
                table: "Stores");

            migrationBuilder.DropForeignKey(
                name: "FK_SyncChanges_Tenants_TenantId",
                table: "SyncChanges");

            migrationBuilder.DropForeignKey(
                name: "FK_Users_Tenants_TenantId",
                table: "Users");

            migrationBuilder.DropTable(
                name: "TenantCurrencyRates");

            migrationBuilder.DropTable(
                name: "UserStoreAccesses");

            migrationBuilder.DropTable(
                name: "Tenants");

            migrationBuilder.DropIndex(
                name: "IX_Users_TenantId_NormalizedUsername",
                table: "Users");

            migrationBuilder.DropIndex(
                name: "IX_SyncChanges_TenantId_AggregateType_Id",
                table: "SyncChanges");

            migrationBuilder.DropIndex(
                name: "IX_Stores_TenantId",
                table: "Stores");

            migrationBuilder.DropIndex(
                name: "IX_StockMovements_TenantId",
                table: "StockMovements");

            migrationBuilder.DropIndex(
                name: "IX_Settings_TenantId",
                table: "Settings");

            migrationBuilder.DropIndex(
                name: "IX_Roles_TenantId_Name",
                table: "Roles");

            migrationBuilder.DropIndex(
                name: "IX_Products_TenantId",
                table: "Products");

            migrationBuilder.DropIndex(
                name: "IX_Invoices_TenantId",
                table: "Invoices");

            migrationBuilder.DropIndex(
                name: "IX_Inventories_ProductId",
                table: "Inventories");

            migrationBuilder.DropIndex(
                name: "IX_Inventories_TenantId_StoreId_ProductId",
                table: "Inventories");

            migrationBuilder.DropIndex(
                name: "IX_Devices_TenantId",
                table: "Devices");

            migrationBuilder.DropIndex(
                name: "IX_Categories_TenantId",
                table: "Categories");

            migrationBuilder.DropIndex(
                name: "IX_AuditLogs_TenantId_CreatedAt",
                table: "AuditLogs");

            migrationBuilder.DropColumn(
                name: "NormalizedUsername",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "SyncChanges");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "Stores");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "StockMovements");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "Roles");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "Inventories");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "Devices");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "Categories");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "AuditLogs");

            migrationBuilder.AddColumn<decimal>(
                name: "ExchangeRate",
                table: "Currencies",
                type: "TEXT",
                precision: 18,
                scale: 6,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.CreateIndex(
                name: "IX_Users_Username",
                table: "Users",
                column: "Username",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Roles_Name",
                table: "Roles",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Inventories_ProductId_StoreId",
                table: "Inventories",
                columns: new[] { "ProductId", "StoreId" },
                unique: true);
        }
    }
}
