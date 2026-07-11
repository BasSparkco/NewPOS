using System.Globalization;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using POS.Infrastructure.Data;

#nullable disable

namespace POS.Infrastructure.Data.Migrations;

[DbContext(typeof(PosDbContext))]
[Migration("20260402120000_AddCurrenciesAndStorePolicy")]
public sealed class AddCurrenciesAndStorePolicy : Migration
{
    private static readonly Guid IlsId = Guid.Parse("a0000001-0000-0000-0000-000000000001");
    private static readonly Guid UsdId = Guid.Parse("a0000001-0000-0000-0000-000000000002");
    private static readonly Guid EurId = Guid.Parse("a0000001-0000-0000-0000-000000000003");
    private static readonly DateTime SeedUtc = new(2026, 4, 2, 12, 0, 0, DateTimeKind.Utc);

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "Currencies",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                Code = table.Column<string>(type: "TEXT", maxLength: 10, nullable: false),
                Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                Symbol = table.Column<string>(type: "TEXT", maxLength: 10, nullable: true),
                ExchangeRate = table.Column<decimal>(type: "TEXT", precision: 18, scale: 6, nullable: false),
                CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                IsDeleted = table.Column<bool>(type: "INTEGER", nullable: false)
            },
            constraints: table => { table.PrimaryKey("PK_Currencies", x => x.Id); });

        migrationBuilder.CreateIndex(
            name: "IX_Currencies_Code",
            table: "Currencies",
            column: "Code",
            unique: true);

        // Raw SQL: InsertData() requires a migration .Designer.cs target model; this migration is hand-authored.
        var ts = SeedUtc.ToString("o", CultureInfo.InvariantCulture);
        var r1 = 1m.ToString("F6", CultureInfo.InvariantCulture);
        var rUsd = 3.65m.ToString("F6", CultureInfo.InvariantCulture);
        var rEur = 4.00m.ToString("F6", CultureInfo.InvariantCulture);

        migrationBuilder.Sql(
            $@"INSERT INTO ""Currencies"" (""Id"", ""Code"", ""Name"", ""Symbol"", ""ExchangeRate"", ""CreatedAt"", ""UpdatedAt"", ""IsDeleted"")
VALUES ('{IlsId:D}', 'ILS', 'Israeli Shekel', '₪', '{r1}', '{ts}', '{ts}', 0);");

        migrationBuilder.Sql(
            $@"INSERT INTO ""Currencies"" (""Id"", ""Code"", ""Name"", ""Symbol"", ""ExchangeRate"", ""CreatedAt"", ""UpdatedAt"", ""IsDeleted"")
VALUES ('{UsdId:D}', 'USD', 'US Dollar', '$', '{rUsd}', '{ts}', '{ts}', 0);");

        migrationBuilder.Sql(
            $@"INSERT INTO ""Currencies"" (""Id"", ""Code"", ""Name"", ""Symbol"", ""ExchangeRate"", ""CreatedAt"", ""UpdatedAt"", ""IsDeleted"")
VALUES ('{EurId:D}', 'EUR', 'Euro', '€', '{rEur}', '{ts}', '{ts}', 0);");

        migrationBuilder.AddColumn<Guid>(
            name: "BaseCurrencyId",
            table: "Stores",
            type: "TEXT",
            nullable: false,
            defaultValue: IlsId);

        migrationBuilder.CreateIndex(
            name: "IX_Stores_BaseCurrencyId",
            table: "Stores",
            column: "BaseCurrencyId");

        // SQLite does not support AddForeignKeyOperation on an existing table (no ALTER ADD CONSTRAINT).
        // The relationship remains in the EF model; optional DB-level enforcement would require a full table rebuild.
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_Stores_BaseCurrencyId",
            table: "Stores");

        migrationBuilder.DropColumn(
            name: "BaseCurrencyId",
            table: "Stores");

        migrationBuilder.DropTable(
            name: "Currencies");
    }
}
