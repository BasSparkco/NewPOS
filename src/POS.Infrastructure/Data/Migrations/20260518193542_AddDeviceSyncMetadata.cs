using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace POS.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDeviceSyncMetadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "DeviceId",
                table: "Invoices",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsSynced",
                table: "Invoices",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "SyncVersion",
                table: "Invoices",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.CreateTable(
                name: "Devices",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    StoreId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    SyncVersion = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 1),
                    IsDeleted = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Devices", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Devices_Stores_StoreId",
                        column: x => x.StoreId,
                        principalTable: "Stores",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Invoices_DeviceId",
                table: "Invoices",
                column: "DeviceId");

            migrationBuilder.CreateIndex(
                name: "IX_Invoices_StoreId_IsSynced",
                table: "Invoices",
                columns: new[] { "StoreId", "IsSynced" });

            migrationBuilder.CreateIndex(
                name: "IX_Devices_StoreId_Name",
                table: "Devices",
                columns: new[] { "StoreId", "Name" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Invoices_Devices_DeviceId",
                table: "Invoices",
                column: "DeviceId",
                principalTable: "Devices",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Invoices_Devices_DeviceId",
                table: "Invoices");

            migrationBuilder.DropTable(
                name: "Devices");

            migrationBuilder.DropIndex(
                name: "IX_Invoices_DeviceId",
                table: "Invoices");

            migrationBuilder.DropIndex(
                name: "IX_Invoices_StoreId_IsSynced",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "DeviceId",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "IsSynced",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "SyncVersion",
                table: "Invoices");
        }
    }
}
