using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace POS.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddRegistersAndCashSessions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "CashSessionId",
                table: "Payments",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "RegisterId",
                table: "Invoices",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DeviceSecretHash",
                table: "Devices",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "RegisterId",
                table: "Devices",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Registers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    StoreId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Number = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    SyncVersion = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 1),
                    IsDeleted = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Registers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Registers_Stores_StoreId",
                        column: x => x.StoreId,
                        principalTable: "Stores",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Registers_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CashSessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    StoreId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RegisterId = table.Column<Guid>(type: "TEXT", nullable: false),
                    OpenedByUserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ClosedByUserId = table.Column<Guid>(type: "TEXT", nullable: true),
                    OpenedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ClosedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    OpeningCashAmount = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false),
                    ClosingCountedAmount = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: true),
                    ExpectedCashAmount = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: true),
                    DiscrepancyAmount = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: true),
                    CurrencyCode = table.Column<string>(type: "TEXT", maxLength: 10, nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    IsSharedSession = table.Column<bool>(type: "INTEGER", nullable: false),
                    Notes = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    IsSynced = table.Column<bool>(type: "INTEGER", nullable: false),
                    SyncVersion = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 1),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    IsDeleted = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CashSessions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CashSessions_Registers_RegisterId",
                        column: x => x.RegisterId,
                        principalTable: "Registers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CashSessions_Stores_StoreId",
                        column: x => x.StoreId,
                        principalTable: "Stores",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CashSessions_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CashSessions_Users_ClosedByUserId",
                        column: x => x.ClosedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CashSessions_Users_OpenedByUserId",
                        column: x => x.OpenedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CashMovements",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CashSessionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Type = table.Column<int>(type: "INTEGER", nullable: false),
                    Amount = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false),
                    Method = table.Column<int>(type: "INTEGER", nullable: false),
                    CurrencyCode = table.Column<string>(type: "TEXT", maxLength: 10, nullable: false),
                    InvoiceId = table.Column<Guid>(type: "TEXT", nullable: true),
                    PaymentId = table.Column<Guid>(type: "TEXT", nullable: true),
                    PerformedByUserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ApprovedByUserId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Notes = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    IsDeleted = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CashMovements", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CashMovements_CashSessions_CashSessionId",
                        column: x => x.CashSessionId,
                        principalTable: "CashSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CashMovements_Invoices_InvoiceId",
                        column: x => x.InvoiceId,
                        principalTable: "Invoices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CashMovements_Payments_PaymentId",
                        column: x => x.PaymentId,
                        principalTable: "Payments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CashMovements_Users_ApprovedByUserId",
                        column: x => x.ApprovedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CashMovements_Users_PerformedByUserId",
                        column: x => x.PerformedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Payments_CashSessionId",
                table: "Payments",
                column: "CashSessionId");

            migrationBuilder.CreateIndex(
                name: "IX_Invoices_RegisterId",
                table: "Invoices",
                column: "RegisterId");

            migrationBuilder.CreateIndex(
                name: "IX_Devices_RegisterId",
                table: "Devices",
                column: "RegisterId");

            migrationBuilder.CreateIndex(
                name: "IX_CashMovements_ApprovedByUserId",
                table: "CashMovements",
                column: "ApprovedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_CashMovements_CashSessionId_Type",
                table: "CashMovements",
                columns: new[] { "CashSessionId", "Type" });

            migrationBuilder.CreateIndex(
                name: "IX_CashMovements_InvoiceId",
                table: "CashMovements",
                column: "InvoiceId");

            migrationBuilder.CreateIndex(
                name: "IX_CashMovements_PaymentId",
                table: "CashMovements",
                column: "PaymentId");

            migrationBuilder.CreateIndex(
                name: "IX_CashMovements_PerformedByUserId",
                table: "CashMovements",
                column: "PerformedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_CashSessions_ClosedByUserId",
                table: "CashSessions",
                column: "ClosedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_CashSessions_OpenedByUserId",
                table: "CashSessions",
                column: "OpenedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_CashSessions_RegisterId",
                table: "CashSessions",
                column: "RegisterId",
                unique: true,
                filter: "\"Status\" = 0");

            migrationBuilder.CreateIndex(
                name: "IX_CashSessions_StoreId",
                table: "CashSessions",
                column: "StoreId");

            migrationBuilder.CreateIndex(
                name: "IX_CashSessions_TenantId",
                table: "CashSessions",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_Registers_StoreId_Number",
                table: "Registers",
                columns: new[] { "StoreId", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Registers_TenantId",
                table: "Registers",
                column: "TenantId");

            migrationBuilder.AddForeignKey(
                name: "FK_Devices_Registers_RegisterId",
                table: "Devices",
                column: "RegisterId",
                principalTable: "Registers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Invoices_Registers_RegisterId",
                table: "Invoices",
                column: "RegisterId",
                principalTable: "Registers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Payments_CashSessions_CashSessionId",
                table: "Payments",
                column: "CashSessionId",
                principalTable: "CashSessions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Devices_Registers_RegisterId",
                table: "Devices");

            migrationBuilder.DropForeignKey(
                name: "FK_Invoices_Registers_RegisterId",
                table: "Invoices");

            migrationBuilder.DropForeignKey(
                name: "FK_Payments_CashSessions_CashSessionId",
                table: "Payments");

            migrationBuilder.DropTable(
                name: "CashMovements");

            migrationBuilder.DropTable(
                name: "CashSessions");

            migrationBuilder.DropTable(
                name: "Registers");

            migrationBuilder.DropIndex(
                name: "IX_Payments_CashSessionId",
                table: "Payments");

            migrationBuilder.DropIndex(
                name: "IX_Invoices_RegisterId",
                table: "Invoices");

            migrationBuilder.DropIndex(
                name: "IX_Devices_RegisterId",
                table: "Devices");

            migrationBuilder.DropColumn(
                name: "CashSessionId",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "RegisterId",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "DeviceSecretHash",
                table: "Devices");

            migrationBuilder.DropColumn(
                name: "RegisterId",
                table: "Devices");
        }
    }
}
