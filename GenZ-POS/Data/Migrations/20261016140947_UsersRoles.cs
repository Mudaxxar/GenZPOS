using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace GenZPOS.Data.Migrations
{
    /// <inheritdoc />
    public partial class UsersRoles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Roles",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    IsAdmin = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Roles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RolePermissions",
                columns: table => new
                {
                    RoleId = table.Column<int>(type: "int", nullable: false),
                    Permission = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RolePermissions", x => new { x.RoleId, x.Permission });
                    table.ForeignKey(
                        name: "FK_RolePermissions_Roles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "Roles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Users",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UserName = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    FullName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    PasswordHash = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    RoleId = table.Column<int>(type: "int", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    MustChangePassword = table.Column<bool>(type: "bit", nullable: false),
                    FailedLogins = table.Column<int>(type: "int", nullable: false),
                    LockedUntil = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastLoginAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Users", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Users_Roles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "Roles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.InsertData(
                table: "Roles",
                columns: new[] { "Id", "Description", "IsAdmin", "Name" },
                values: new object[,]
                {
                    { 1, "Owner: every right", true, "Admin" },
                    { 2, "Day-to-day running, no users or settings", false, "Manager" },
                    { 3, "Counter sales and payments", false, "Cashier" }
                });

            migrationBuilder.InsertData(
                table: "RolePermissions",
                columns: new[] { "Permission", "RoleId" },
                values: new object[,]
                {
                    { "Catalog.Manage", 2 },
                    { "Customers.Manage", 2 },
                    { "Dashboard.View", 2 },
                    { "Payments.Receive", 2 },
                    { "PurchaseReturns.Manage", 2 },
                    { "Purchases.Delete", 2 },
                    { "Purchases.Manage", 2 },
                    { "Reports.Balances", 2 },
                    { "Reports.DayBook", 2 },
                    { "Reports.ItemSales", 2 },
                    { "Reports.Profit", 2 },
                    { "SaleReturns.Manage", 2 },
                    { "Sales.Create", 2 },
                    { "Sales.Delete", 2 },
                    { "Sales.View", 2 },
                    { "Stock.Adjust", 2 },
                    { "Stock.View", 2 },
                    { "Suppliers.Manage", 2 },
                    { "Suppliers.Pay", 2 },
                    { "Customers.Manage", 3 },
                    { "Payments.Receive", 3 },
                    { "Sales.Create", 3 },
                    { "Sales.View", 3 },
                    { "Stock.View", 3 }
                });

            migrationBuilder.CreateIndex(
                name: "IX_Roles_Name",
                table: "Roles",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Users_RoleId",
                table: "Users",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "IX_Users_UserName",
                table: "Users",
                column: "UserName",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RolePermissions");

            migrationBuilder.DropTable(
                name: "Users");

            migrationBuilder.DropTable(
                name: "Roles");
        }
    }
}
